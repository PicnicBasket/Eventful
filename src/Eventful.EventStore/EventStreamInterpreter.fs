﻿namespace Eventful.EventStore 

open Eventful
open Eventful.EventStream
open FSharpx.Collections
open FSharpx.Option
open EventStore.ClientAPI
open System
open System.Runtime.Caching

module EventStreamInterpreter = 
    let log = createLogger "Eventful.EventStore.EventStreamInterpreter"
    let cachePolicy = new CacheItemPolicy()

    let getCacheKey stream eventNumber =
        stream + ":" + eventNumber.ToString()

    let interpret<'A,'TMetadata when 'TMetadata : equality> 
        (eventStore : Client) 
        (cache : System.Runtime.Caching.ObjectCache)
        (serializer : ISerializer)
        (eventStoreTypeToClassMap : EventStoreTypeToClassMap)
        (classToEventStoreTypeMap : ClassToEventStoreTypeMap)
        (readSnapshot : string -> Map<string,Type> -> Async<StateSnapshot>)
        (correlationId : Guid)
        (prog : FreeEventStream<obj,'A,'TMetadata>) : Async<'A> = 
        let rec loop prog (values : Map<EventToken,(byte[]*byte[])>) (writes : Vector<string * int * obj * 'TMetadata>) : Async<'A> =
            match prog with
            | FreeEventStream (GetEventStoreTypeToClassMap ((), f)) ->
                let next = f eventStoreTypeToClassMap
                loop next values writes
            | FreeEventStream (GetClassToEventStoreTypeMap ((), f)) ->
                let next = f classToEventStoreTypeMap
                loop next values writes
            | FreeEventStream (LogMessage (logLevel, messageTemplate, args, next)) ->
                async {
                    // todo take level into account
                    log.RichDebug messageTemplate args
                    return! loop next values writes
                }
            | FreeEventStream (RunAsync asyncBlock) ->
                log.RichDebug "RunAsync {@CorrelationId}" [|correlationId|]
                async {
                    let! next = asyncBlock 
                    return! loop next values writes
                }
            | FreeEventStream (ReadSnapshot (streamId, typeMap, f)) -> 
                log.RichDebug "ReadSnapshot {@StreamId} {@CorrelationId}" [|streamId;correlationId|]
                async {
                    let! snapshot = readSnapshot streamId typeMap
                    let next = f snapshot
                    return! loop next values writes
                }
            | FreeEventStream (ReadFromStream (streamId, eventNumber, f)) -> 
                log.RichDebug "ReadFromStream Start {@StreamId} {@EventNumber} {@CorrelationId}" [|streamId;eventNumber;correlationId|]
                let sw = System.Diagnostics.Stopwatch.StartNew()
                async {
                    let cacheKey = getCacheKey streamId eventNumber
                    let cachedEvent = cache.Get(cacheKey)

                    let! event = 
                        match cachedEvent with
                        | :? ResolvedEvent as evt ->
                            sw.Stop()
                            log.RichDebug "ReadFromStream End. Retrieved from cache {@CorrelationId}  {Elapsed:000} ms" [|correlationId;sw.ElapsedMilliseconds|]
                            async { return Some evt }
                        | _ -> 
                            async {
                                let! events = eventStore.readStreamSliceForward streamId eventNumber 100

                                for event in events do
                                    let key = getCacheKey streamId event.OriginalEventNumber
                                    let cacheItem = new CacheItem(key, event)
                                    cache.Set(cacheItem, cachePolicy)
                                sw.Stop()
                                let requestedEvent = events |> tryHead
                                log.RichDebug "ReadFromStream End. Retrieved from event store {@Event}. Retrieved {@EventCount} in total. {@CorrelationId} {Elapsed:000} ms" [|requestedEvent;events.Length;correlationId;sw.ElapsedMilliseconds|]
                                return requestedEvent
                            }
                        
                    let readEvent = 
                        match event with
                        | Some event ->
                            let event = event.Event
                            let eventToken = {
                                Stream = streamId
                                Number = eventNumber
                                EventType = event.EventType
                            }
                            Some (eventToken, (event.Data, event.Metadata))
                        | None -> None

                    match readEvent with
                    | Some (eventToken, evt) -> 
                        let next = f (Some eventToken)
                        let values' = values |> Map.add eventToken evt
                        return! loop next values' writes
                    | None ->
                        let next = f None
                        return! loop next values writes
                }
            | FreeEventStream (ReadValue (token, g)) ->
                log.RichDebug "ReadValue {@StreamId} {@EventNumber} {@EventType} {@CorrelationId}" [|token.Stream;token.Number;token.EventType;correlationId|]

                let (data, metadata) = values.[token]
                let dataClass = eventStoreTypeToClassMap.Item(token.EventType)
                let dataObj = serializer.DeserializeObj(data) dataClass
                let metadataObj = serializer.DeserializeObj(metadata) typeof<'TMetadata> :?> 'TMetatdata
                let next = g (dataObj,metadataObj)
                loop next  values writes
            | FreeEventStream (WriteToStream (streamId, eventNumber, events, next)) ->
                log.RichDebug "WriteToStream {@StreamId} {@EventNumber} {@Events} {@CorrelationId}" [|streamId;eventNumber;events;correlationId|]
                let toEventData = function
                    | Event { Body = dataObj; EventType = typeString; Metadata = metadata} -> 
                        let serializedData = serializer.Serialize(dataObj)
                        let serializedMetadata = serializer.Serialize(metadata)
                        new EventData(System.Guid.NewGuid(), typeString, true, serializedData, serializedMetadata) 
                    | EventLink (destinationStream, destinationEventNumber, metadata) ->
                        let bodyString = sprintf "%d@%s" destinationEventNumber destinationStream
                        let body = System.Text.Encoding.UTF8.GetBytes bodyString
                        let serializedMetadata = serializer.Serialize(metadata)
                        new EventData(System.Guid.NewGuid(), "$>", true, body, serializedMetadata) 

                let eventDataArray = 
                    events
                    |> Seq.map toEventData
                    |> Array.ofSeq

                async {
                    let esExpectedEvent = 
                        match eventNumber with
                        | Any -> -2
                        | NewStream -> -1
                        | AggregateVersion x -> x
                    let! writeResult = eventStore.append streamId esExpectedEvent eventDataArray
                    return! loop (next writeResult) values writes
                }
            | FreeEventStream (NotYetDone g) ->
                let next = g ()
                loop next values writes
            | Pure result ->
                log.RichDebug "Pure @{Result} {@CorrelationId}" [|result;correlationId|]

                async {
                    return result
                }
        loop prog Map.empty Vector.empty