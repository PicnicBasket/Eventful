﻿namespace Eventful

open System
open FSharpx.Choice
open FSharpx.Collections

open Eventful.EventStream

type CommandResult = Choice<list<string * obj * EventMetadata>,NonEmptyList<ValidationFailure>> 
type StreamNameBuilder<'TId> = ('TId -> string)

type EventResult = unit

type IAggregateType =
    abstract Name : string with get

type ICommandHandler<'TState,'TEvent,'TId when 'TId :> IIdentity> =
    abstract member CmdType : Type
    abstract member GetId : obj -> 'TId
                    // AggregateType -> Cmd -> Source Stream -> EventNumber -> Program
    abstract member Handler : string -> obj -> EventStreamProgram<CommandResult>

type IEventHandler<'TState,'TEvent,'TId> =
    abstract member EventType : Type
                    // AggregateType -> Source Stream -> Source EventNumber -> Event -> -> Program
    abstract member Handler : string -> string -> int -> obj -> EventStreamProgram<EventResult>

type AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType when 'TId :> IIdentity and 'TAggregateType :> IAggregateType> private 
    (
        aggregateType : 'TAggregateType,
        commandHandlers : list<ICommandHandler<'TState,'TEvent,'TId>>, 
        eventHandlers : list<IEventHandler<'TState,'TEvent,'TId>>,
        stateBuilder : StateBuilder<'TState>
    ) =
    member x.CommandHandlers = commandHandlers
    member x.BuildStreamId (id:'TId) = sprintf "%s-%s" (aggregateType.ToString()) (id.GetId)
    member x.EventHandlers = eventHandlers
    member x.AggregateType = aggregateType
    member x.StateBuilder = stateBuilder
    member x.AddCommandHandler handler = 
        new AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>(aggregateType, handler::commandHandlers, eventHandlers, stateBuilder)
    member x.AddEventHandler handler = 
        new AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>(aggregateType, commandHandlers, handler::eventHandlers, stateBuilder)
    member x.Combine (y:AggregateHandlers<_,_,_,_>) =
        new AggregateHandlers<_,_,_,_>(
            aggregateType,
            List.append commandHandlers y.CommandHandlers, 
            List.append eventHandlers y.EventHandlers, 
            stateBuilder)

    static member Empty aggregateType stateBuilder = new AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>(aggregateType, List.empty, List.empty, stateBuilder)
    
type IHandler<'TState,'TEvent,'TId when 'TId :> IIdentity> = 
    abstract member add : AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType> -> AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>

open FSharpx
open Eventful.Validation

type Validator<'TCmd,'TState> = 
| CommandValidator of ('TCmd -> seq<ValidationFailure>)
| StateValidator of ('TState option -> seq<ValidationFailure>)
| CombinedValidator of ('TCmd -> 'TState option -> seq<ValidationFailure>)

type CommandHandler<'TCmd, 'TState, 'TId, 'TEvent when 'TId :> IIdentity> = {
    GetId : 'TCmd -> 'TId
    StateBuilder : StateBuilder<'TState>
    Validators : Validator<'TCmd,'TState> list
    Handler : 'TCmd -> seq<'TEvent>
}

open Eventful.EventStream
open Eventful.Validation

module AggregateActionBuilder =
    let simpleHandler<'TId, 'TState,'TCmd,'TEvent when 'TId :> IIdentity> stateBuilder (f : 'TCmd -> 'TEvent) =
        {
            GetId = MagicMapper.magicId<'TId>
            StateBuilder = stateBuilder
            Validators = List.empty
            Handler = f >> Seq.singleton
        } : CommandHandler<'TCmd, 'TState, 'TId, 'TEvent> 

    let toChoiceValidator cmd r =
        if r |> Seq.isEmpty then
            Success cmd
        else
            NonEmptyList.create (r |> Seq.head) (r |> Seq.tail |> List.ofSeq) |> Failure

    let runValidation validators cmd state =
        let v = new FSharpx.Validation.NonEmptyListValidation<ValidationFailure>()
        validators
        |> List.map (function
                        | CommandValidator validator -> validator cmd |> (toChoiceValidator cmd)
                        | StateValidator validator -> validator state |> (toChoiceValidator cmd)
                        | CombinedValidator validator -> validator cmd state |> (toChoiceValidator cmd))
         |> List.map (fun x -> x)
         |> List.fold (fun s validator -> v.apl validator s) (Choice.returnM cmd) 

    let untypedGetId<'TId,'TCmd,'TEvent,'TState when 'TId :> IIdentity> (sb : CommandHandler<'TCmd, 'TState, 'TId, 'TEvent>) (cmd:obj) =
        match cmd with
        | :? 'TCmd as cmd ->
            sb.GetId cmd
        | _ -> failwith <| sprintf "Invalid command %A" (cmd.GetType())

    let getStreamName aggregateName (id : IIdentity) =
        sprintf "%s-%s" aggregateName (id.GetId)

    let handleCommand (commandHandler:CommandHandler<'TCmd, 'TState, 'TId, 'TEvent>) aggregateType (cmd : obj) =
        let unwrapper = MagicMapper.getUnwrapper<'TEvent>()
        eventStream {
            match cmd with
            | :? 'TCmd as cmd -> 
                let id = commandHandler.GetId cmd
                let stream = getStreamName aggregateType id
                let! (eventsConsumed, state) = commandHandler.StateBuilder |> StateBuilder.toStreamProgram stream

                let result = choose {
                    let! validated = runValidation commandHandler.Validators cmd state

                    let result = commandHandler.Handler validated
                    return
                        result 
                        |> Seq.map unwrapper
                        |> Seq.map (fun evt -> 
                                        let metadata = { SourceMessageId = (Guid.NewGuid()); MessageId = (Guid.NewGuid()) }
                                        (stream, evt, metadata))
                        |> List.ofSeq
                }

                match result with
                | Choice1Of2 events ->
                    for (stream, event, metadata) in events do
                        let! eventData = getEventStreamEvent event metadata
                        let expectedVersion = 
                            match eventsConsumed with
                            | 0 -> NewStream
                            | x -> AggregateVersion x

                        let! ignored = writeToStream stream expectedVersion (Seq.singleton eventData)
                        ()
                | _ -> ()

                return result
            | _ -> return NonEmptyList.singleton (sprintf "Invalid command type: %A expected %A" (cmd.GetType()) typeof<'TCmd>) |> Choice2Of2
        }
        
    let ToInterface<'TId,'TCmd,'TEvent,'TState when 'TId :> IIdentity> (sb : CommandHandler<'TCmd, 'TState, 'TId, 'TEvent>) = {
        new ICommandHandler<'TState,'TEvent,'TId> with 
             member this.GetId cmd = untypedGetId sb cmd
             member this.CmdType = typeof<'TCmd>
             member this.Handler aggregateType cmd = handleCommand sb aggregateType cmd
        }

    let buildCmd<'TId,'TCmd,'TEvent,'TState when 'TId :> IIdentity> (sb : CommandHandler<'TCmd, 'TState, 'TId, 'TEvent>) : IHandler<'TState,'TEvent,'TId> = {
            new IHandler<'TState,'TEvent,'TId> with
                member x.add handlers =
                    let cmdInterface = ToInterface sb
                    handlers.AddCommandHandler cmdInterface
        }

    let addValidator 
        (validator : Validator<'TCmd,'TState>) 
        (handler: CommandHandler<'TCmd, 'TState, 'TId, 'TEvent>) = 
        { handler with Validators = validator::handler.Validators }

    let ensureFirstCommand x = addValidator (StateValidator (isNone id "Must be the first command")) x

    let buildSimpleCmdHandler<'TId,'TState,'TCmd,'TEvent when 'TId :> IIdentity> stateBuilder = 
        (simpleHandler<'TId,'TState,'TCmd,'TEvent> stateBuilder) >> buildCmd
        
    let getEventInterfaceForLink<'TLinkEvent,'TEvent,'TId,'TState when 'TId :> IIdentity> (fId : 'TLinkEvent -> 'TId) = {
        new IEventHandler<'TState,'TEvent,'TId> with 
             member this.EventType = typeof<'TLinkEvent>
             member this.Handler aggregateType sourceStream sourceEventNumber evt = eventStream {
                let metadata = { SourceMessageId = System.Guid.NewGuid(); MessageId = System.Guid.NewGuid() }

                let resultingStream = getStreamName aggregateType (fId (evt :?> 'TLinkEvent))

                // todo: should not be new stream
                let! _ = EventStream.writeLink resultingStream NewStream sourceStream sourceEventNumber metadata
                return ()
             }
        }

    let getEventInterfaceForOnEvent<'TOnEvent, 'TEvent, 'TId, 'TState when 'TId :> IIdentity> (fId : 'TOnEvent -> 'TId) (stateBuilder : StateBuilder<'TState>) (runEvent : 'TOnEvent -> seq<'TEvent>) = {
        new IEventHandler<'TState,'TEvent,'TId> with 
            member this.EventType = typeof<'TOnEvent>
            member this.Handler aggregateType sourceStream sourceEventNumber evt = eventStream {
                let unwrapper = MagicMapper.getUnwrapper<'TEvent>()
                let resultingStream = getStreamName aggregateType (fId (evt :?> 'TOnEvent))

                let! (eventsConsumed, state) = stateBuilder |> StateBuilder.toStreamProgram resultingStream
                let! eventTypeMap = getEventTypeMap()

                let resultingEvents = 
                    runEvent (evt :?> 'TOnEvent)
                    |> Seq.map (fun x -> 
                        let metadata = { SourceMessageId = (Guid.NewGuid()); MessageId = (Guid.NewGuid()) }
                        let event = unwrapper x
                        let eventType = eventTypeMap.FindValue (new ComparableType(event.GetType()))
                        Event { Body = event; EventType = eventType; Metadata = metadata })

                let! _ = EventStream.writeToStream resultingStream NewStream resultingEvents
                return ()
            }
    }

    let linkEvent<'TLinkEvent,'TEvent,'TId,'TState when 'TId :> IIdentity> fId (linkEvent : 'TLinkEvent -> 'TEvent) = {
        new IHandler<'TState,'TEvent,'TId> with
            member x.add handlers =
                let linkerInterface = (getEventInterfaceForLink<'TLinkEvent,'TEvent,'TId,'TState> fId)
                handlers.AddEventHandler linkerInterface
    }

    let onEvent<'TOnEvent,'TEvent,'TId,'TState when 'TId :> IIdentity> fId (stateBuilder : StateBuilder<'TState>) (runEvent : 'TOnEvent -> seq<'TEvent>) = {
        new IHandler<'TState,'TEvent,'TId> with
            member x.add handlers =
                let onEventInterface = (getEventInterfaceForOnEvent<'TOnEvent,'TEvent,'TId,'TState> fId stateBuilder runEvent)
                handlers.AddEventHandler onEventInterface
    }

module Aggregate = 
    type AggregateBuilder<'TState,'TEvent,'TId, 'TAggregateType when 'TId :> IIdentity and 'TAggregateType :> IAggregateType> (aggregateType : 'TAggregateType, stateBuilder : StateBuilder<'TState>) = 
        member this.Zero() = AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>.Empty

        member x.Delay(f : unit -> AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>) = f ()

        member this.Yield(x:IHandler<'TState,'TEvent,'TId>) :  AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType> =
            let empty = AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>.Empty aggregateType stateBuilder
            let result = x.add empty
            result

        member this.Combine (a:AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>,b:AggregateHandlers<'TState,'TEvent,'TId, 'TAggregateType>) =
            a.Combine b

    let aggregate<'TState,'TEvent,'TId, 'TAggregateType when 'TId :> IIdentity and 'TAggregateType :> IAggregateType> aggregateType stateBuilder = 
        new AggregateBuilder<'TState,'TEvent,'TId, 'TAggregateType>(aggregateType, stateBuilder)