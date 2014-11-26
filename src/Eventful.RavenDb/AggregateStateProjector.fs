﻿namespace Eventful.Raven

open System
open FSharpx
open FSharpx.Option

open Raven.Json.Linq
open Raven.Imports.Newtonsoft.Json.Linq
open Raven.Client
open Eventful

type AggregateStateDocument = {
    Snapshot : RavenJObject
    NextWakeup : string
}
with 
    static member Empty () = { 
        Snapshot = new RavenJObject() 
        NextWakeup = null 
    }

type AggregateState = {
    Snapshot : Map<string,obj>
    NextWakeup : DateTime option
}

module AggregateStateProjector =
    let deserialize (serializer :  ISerializer) (doc : RavenJObject) (blockBuilders : IStateBlockBuilder<'TMetadata, unit> list) =
        let deserializeRavenJToken targetType jToken =
            jToken.ToString()
            |> System.Text.Encoding.UTF8.GetBytes
            |> (fun x -> serializer.DeserializeObj x targetType)

        let blockBuilderMap = 
            blockBuilders
            |> Seq.map(fun b -> b.Name, b)
            |> Map.ofSeq

        let addKey stateMap key =
            let blockBuilder = blockBuilderMap.Item key
            let blockType = blockBuilder.Type
            let jToken = doc.Item key
            let value = deserializeRavenJToken blockType jToken
            stateMap |> Map.add key value

        doc.Keys
        |> Seq.fold addKey Map.empty

    let getDocumentKey streamId = 
        "AggregateState/" + streamId

    let emptyMetadata () =
        let metadata = new RavenJObject(StringComparer.OrdinalIgnoreCase)
        metadata.Add("Raven-Entity-Name", new RavenJValue("AggregateStates"))
        metadata

    let mapToRavenJObject (serializer : ISerializer) (stateMap : Map<string,obj>) =
        let jObject = new RavenJObject()
        for keyValuePair in stateMap do
            jObject.Add(keyValuePair.Key, RavenJToken.Parse(System.Text.Encoding.UTF8.GetString <| serializer.Serialize keyValuePair.Value))
        jObject

    let lastEventHeader = "LastEventNumber"

    let getLastEventNumber (metadata : RavenJObject) =
        if(metadata.ContainsKey lastEventHeader) then
            (metadata.Item lastEventHeader).Value<int>()
        else
            -1

    let setLastEventNumber (metadata : RavenJObject) (value : int) =
        if(metadata.ContainsKey lastEventHeader) then
            metadata.Remove lastEventHeader |> ignore
        metadata.Add(lastEventHeader, new RavenJValue(value))

    let deserializeDateString (value : string) =
        match value with
        | null -> None
        | value -> 
            Some (DateTime.Parse value)

    let serializeDateTimeOption = function
        | None -> null
        | Some (dateTime : DateTime) -> (new RavenJValue(dateTime)).ToString()

    let getAggregateState   
        (documentStore : Raven.Client.Document.DocumentStore) 
        serializer 
        (database : string) 
        (handlers : EventfulHandlers<'TCommandContext, 'TEventContext, 'TMetadata, 'TBaseEvent,'TAggregateType>)
        streamId 
        aggregateType
        = 
        async {
        let stateDocumentKey = getDocumentKey streamId
        use session = documentStore.OpenAsyncSession(database)
        let! doc = session.LoadAsync<AggregateStateDocument> stateDocumentKey |> Async.AwaitTask

        let blockBuilders = (handlers.AggregateTypes.Item aggregateType).StateBuilder.GetBlockBuilders
        let snapshot = deserialize serializer doc.Snapshot blockBuilders
        return {
            AggregateState.Snapshot = snapshot
            NextWakeup = deserializeDateString doc.NextWakeup
        }
    }

    let buildProjector 
        (getStreamId : 'TMessage -> string option)
        (getEventNumber : 'TMessage -> int)
        (getEvent : 'TMessage -> obj) 
        (getMetadata : 'TMessage -> 'TMetadata) 
        (serializer :  ISerializer)
        (handlers : EventfulHandlers<'TCommandContext, 'TEventContext, 'TMetadata, 'TBaseEvent,'TAggregateType>) =
        let matchingKeys = 
            getStreamId
            >> Option.map Seq.singleton 
            >> Option.getOrElse Seq.empty

        let processEvents 
            (fetcher : IDocumentFetcher) 
            (streamId : string) 
            (messages : seq<'TMessage>) = async {
                
                let documentKey = getDocumentKey streamId
                let! doc = 
                    fetcher.GetDocument documentKey
                    |> Async.AwaitTask

                let aggregateType = 
                    messages 
                    |> Seq.map getMetadata
                    |> Seq.map handlers.GetAggregateType
                    |> Seq.distinct
                    |> Seq.toList
                    |> function
                        | [aggregateType] -> aggregateType
                        | x -> failwith <| sprintf "Got messages for mixed aggreate type. Stream: %s, AggregateTypes: %A" streamId x

                return
                    match handlers.AggregateTypes |> Map.tryFind aggregateType with
                    | Some aggregateConfig ->
                        let (stateDocument : AggregateStateDocument, metadata : RavenJObject, etag) = 
                            doc
                            |> Option.getOrElseF (fun () -> (AggregateStateDocument.Empty(), emptyMetadata(), Raven.Abstractions.Data.Etag.Empty))

                        let snapshot = 
                            deserialize serializer stateDocument.Snapshot aggregateConfig.StateBuilder.GetBlockBuilders

                        let applyToSnapshot (lastEventNumber, snapshot) message =
                            let eventNumber = getEventNumber message
                            if eventNumber > lastEventNumber then
                                let event = getEvent message
                                let metadata = getMetadata message
                                let snapshot' = 
                                    snapshot
                                    |> AggregateStateBuilder.dynamicRun aggregateConfig.StateBuilder.GetBlockBuilders () event metadata 
                                (eventNumber, snapshot')
                            else
                                (lastEventNumber, snapshot)

                        let (lastEventNumber,updatedSnapshot) =
                            messages
                            |> Seq.fold applyToSnapshot (getLastEventNumber metadata, snapshot)

                        setLastEventNumber metadata lastEventNumber

                        let nextWakeup = maybe {
                            let! EventfulWakeupHandler (nextWakeupStateBuilder,_) = aggregateConfig.Wakeup
                            return! nextWakeupStateBuilder.GetState updatedSnapshot
                        }

                        let updatedDoc = {
                            AggregateStateDocument.Snapshot = mapToRavenJObject serializer updatedSnapshot
                            NextWakeup = serializeDateTimeOption nextWakeup
                        }

                        let writeDoc = 
                            ProcessAction.Write (
                                {
                                    DocumentKey = documentKey
                                    Document = updatedDoc
                                    Metadata = lazy(metadata)
                                    Etag = etag
                                } , Guid.NewGuid())

                        Seq.singleton writeDoc
                    | None ->
                        failwith <| sprintf "Could not find configuration for aggregateType: %A" aggregateType
        }

        {
            MatchingKeys = matchingKeys
            ProcessEvents = processEvents
        }