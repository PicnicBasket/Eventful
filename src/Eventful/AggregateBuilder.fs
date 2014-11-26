﻿namespace Eventful

open System
open FSharpx.Choice
open FSharpx.Option
open FSharpx.Collections
open FSharp.Control

open Eventful.EventStream

type CommandSuccess<'TBaseEvent, 'TMetadata> = {
    Events : (string * 'TBaseEvent * 'TMetadata) list
    Position : EventPosition option
}
with 
    static member Empty = {
        Events = List.empty
        Position = None
    }

type CommandResult<'TBaseEvent,'TMetadata> = Choice<CommandSuccess<'TBaseEvent,'TMetadata>,NonEmptyList<CommandFailure>> 

type StreamNameBuilder<'TId> = ('TId -> string)

type IRegistrationVisitor<'T,'U> =
    abstract member Visit<'TCmd> : 'T -> 'U

type IRegistrationVisitable =
    abstract member Receive<'T,'U> : 'T -> IRegistrationVisitor<'T,'U> -> 'U

type EventResult = unit

type metadataBuilder<'TMetadata> = string -> 'TMetadata

type IStateChangeHandler<'TMetadata, 'TBaseEvent> =
    abstract member AddStateBuilder : IStateBlockBuilder<'TMetadata, unit> list -> IStateBlockBuilder<'TMetadata, unit> list 
    abstract member Handler : StateSnapshot -> StateSnapshot -> seq<'TBaseEvent * metadataBuilder<'TMetadata>>

// 'TContext can either be CommandContext or EventContext
type AggregateConfiguration<'TContext, 'TAggregateId, 'TMetadata, 'TBaseEvent> = {
    /// used to make processing idempotent
    GetUniqueId : 'TMetadata -> string option
    GetStreamName : 'TContext -> 'TAggregateId -> string
    StateBuilder : IStateBuilder<Map<string,obj>, 'TMetadata, unit>
    StateChangeHandlers : LazyList<IStateChangeHandler<'TMetadata, 'TBaseEvent>>
}

type ICommandHandler<'TAggregateId,'TCommandContext, 'TMetadata, 'TBaseEvent> =
    abstract member CmdType : Type
    abstract member AddStateBuilder : IStateBlockBuilder<'TMetadata, unit> list -> IStateBlockBuilder<'TMetadata, unit> list
    abstract member GetId : 'TCommandContext-> obj -> 'TAggregateId
                    // AggregateType -> Cmd -> Source Stream -> EventNumber -> Program
    abstract member Handler : AggregateConfiguration<'TCommandContext, 'TAggregateId, 'TMetadata, 'TBaseEvent> -> 'TCommandContext -> obj -> EventStreamProgram<CommandResult<'TBaseEvent,'TMetadata>,'TMetadata>
    abstract member Visitable : IRegistrationVisitable

type IEventHandler<'TAggregateId,'TMetadata, 'TEventContext when 'TAggregateId : equality> =
    abstract member AddStateBuilder : IStateBlockBuilder<'TMetadata, unit> list -> IStateBlockBuilder<'TMetadata, unit> list
    abstract member EventType : Type
                    // AggregateType -> Source Stream -> Source EventNumber -> Event -> -> Program
    abstract member Handler : AggregateConfiguration<'TEventContext, 'TAggregateId, 'TMetadata, 'TBaseEvent> -> 'TEventContext -> PersistedEvent<'TMetadata> -> Async<EventStreamProgram<EventResult,'TMetadata>>

type IWakeupHandler<'TAggregateId,'TCommandContext, 'TMetadata, 'TBaseEvent> =
    abstract member WakeupFold : WakeupFold<'TMetadata>
                            //streamId -> getUniqueId                   -> time     -> program
    abstract member Handler : AggregateConfiguration<'TEventContext, 'TAggregateId, 'TMetadata, 'TBaseEvent> -> string   -> DateTime -> EventStreamProgram<EventResult,'TMetadata>

type AggregateCommandHandlers<'TAggregateId,'TCommandContext,'TMetadata, 'TBaseEvent> = seq<ICommandHandler<'TAggregateId,'TCommandContext,'TMetadata, 'TBaseEvent>>
type AggregateEventHandlers<'TAggregateId,'TMetadata, 'TEventContext  when 'TAggregateId : equality> = seq<IEventHandler<'TAggregateId, 'TMetadata, 'TEventContext >>

type AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent when 'TId : equality> private 
    (
        commandHandlers : list<ICommandHandler<'TId,'TCommandContext,'TMetadata, 'TBaseEvent>>, 
        eventHandlers : list<IEventHandler<'TId,'TMetadata, 'TEventContext>>,
        stateChangeHandlers : list<IStateChangeHandler<'TMetadata, 'TBaseEvent>>
    ) =
    member x.CommandHandlers = commandHandlers
    member x.EventHandlers = eventHandlers
    member x.StateChangeHandlers = stateChangeHandlers
    member x.AddCommandHandler handler = 
        new AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent>(handler::commandHandlers, eventHandlers,stateChangeHandlers)
    member x.AddEventHandler handler = 
        new AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent>(commandHandlers, handler::eventHandlers,stateChangeHandlers)
    member x.AddStateChangeHandler handler = 
        new AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent>(commandHandlers, eventHandlers,handler::stateChangeHandlers)
    member x.Combine (y:AggregateHandlers<_,_,_,_,_>) =
        new AggregateHandlers<_,_,_,_,_>(
            List.append commandHandlers y.CommandHandlers, 
            List.append eventHandlers y.EventHandlers,
            List.append stateChangeHandlers y.StateChangeHandlers)

    static member Empty = new AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent>(List.empty, List.empty, List.empty)

module AggregateHandlers =
    let addCommandHandler handler (aggregateHandlers : AggregateHandlers<_,_,_,_,_>) =
        aggregateHandlers.AddCommandHandler handler

    let addCommandHandlers handlers (aggregateHandlers : AggregateHandlers<_,_,_,_,_>) =
        handlers
        |> Seq.fold (fun (s:AggregateHandlers<_,_,_,_,_>) h -> s.AddCommandHandler h) aggregateHandlers

    let addEventHandler handler (aggregateHandlers : AggregateHandlers<_,_,_,_,_>) =
        aggregateHandlers.AddEventHandler handler

    let addEventHandlers handlers (aggregateHandlers : AggregateHandlers<_,_,_,_,_>) =
        handlers
        |> Seq.fold (fun (s:AggregateHandlers<_,_,_,_,_>) h -> s.AddEventHandler h) aggregateHandlers

    let addStateChangeHandler handler (aggregateHandlers : AggregateHandlers<_,_,_,_,_>) =
        aggregateHandlers.AddStateChangeHandler handler

type IHandler<'TEvent,'TId,'TCommandContext,'TEventContext when 'TId : equality> = 
    abstract member add : AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent> -> AggregateHandlers<'TId,'TCommandContext,'TEventContext,'TMetadata,'TBaseEvent>

open FSharpx
open Eventful.Validation

type CommandHandlerOutput<'TBaseEvent,'TMetadata> = {
    UniqueId : string // used to make commands idempotent
    Events : seq<'TBaseEvent * metadataBuilder<'TMetadata>>
}

type CommandHandler<'TCmd, 'TCommandContext, 'TCommandState, 'TAggregateId, 'TMetadata, 'TBaseEvent> = {
    GetId : 'TCommandContext -> 'TCmd -> 'TAggregateId
    StateBuilder : IStateBuilder<'TCommandState, 'TMetadata, unit>
    Handler : 'TCommandState -> 'TCommandContext -> 'TCmd -> Async<Choice<CommandHandlerOutput<'TBaseEvent,'TMetadata>, NonEmptyList<ValidationFailure>>>
}

type MultiEventRun<'TAggregateId,'TMetadata,'TState when 'TAggregateId : equality> = ('TAggregateId * ('TState -> seq<obj * metadataBuilder<'TMetadata>>))

open Eventful.Validation

module AggregateActionBuilder =
    open EventStream

    let log = createLogger "Eventful.AggregateActionBuilder"

    let fullHandlerAsync<'TId, 'TState,'TCmd,'TEvent, 'TCommandContext,'TMetadata, 'TBaseEvent> stateBuilder f =
        {
            GetId = (fun _ -> MagicMapper.magicId<'TId>)
            StateBuilder = stateBuilder
            Handler = f
        } : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent> 

    let fullHandler<'TId, 'TState,'TCmd,'TEvent, 'TCommandContext,'TMetadata, 'TBaseEvent> stateBuilder f =
        {
            GetId = (fun _ -> MagicMapper.magicId<'TId>)
            StateBuilder = stateBuilder
            Handler = (fun a b c ->  f a b c |> Async.returnM)
        } : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent> 

    let simpleHandler<'TAggregateId, 'TState, 'TCmd, 'TCommandContext, 'TMetadata, 'TBaseEvent> stateBuilder (f : 'TCmd -> (string * 'TBaseEvent * metadataBuilder<'TMetadata>)) =
        let makeResult (uniqueId, evt : 'TBaseEvent, metadata) = { 
            UniqueId = uniqueId 
            Events =  (evt, metadata) |> Seq.singleton 
        }

        {
            GetId = (fun _ -> MagicMapper.magicId<'TAggregateId>)
            StateBuilder = stateBuilder
            Handler = (fun _ _ -> f >> makeResult >> Success >> Async.returnM)
        } : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TAggregateId, 'TMetadata, 'TBaseEvent> 

    let toChoiceValidator cmd r =
        if r |> Seq.isEmpty then
            Success cmd
        else
            NonEmptyList.create (r |> Seq.head) (r |> Seq.tail |> List.ofSeq) |> Failure

    let untypedGetId<'TId,'TCmd,'TEvent,'TState, 'TCommandContext, 'TMetadata, 'TValidatedState, 'TBaseEvent> (sb : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent>) (context : 'TCommandContext) (cmd:obj) =
        match cmd with
        | :? 'TCmd as cmd ->
            sb.GetId context cmd
        | _ -> failwith <| sprintf "Invalid command %A" (cmd.GetType())

    let uniqueIdBuilder getUniqueId =
        StateBuilder.Empty "$Eventful::UniqueIdBuilder" Set.empty
        |> StateBuilder.allEventsHandler 
            (fun m -> ()) 
            (fun (s,e,m) -> 
                match getUniqueId m with
                | Some uniqueId ->
                    s |> Set.add uniqueId
                | None -> s)
        |> StateBuilder.toInterface

    let writeEvents stream lastEventNumber events =  eventStream {
        let rawEventToEventStreamEvent (event, metadata) = getEventStreamEvent event metadata
        let! eventStreamEvents = EventStream.mapM rawEventToEventStreamEvent events

        let expectedVersion = 
            match lastEventNumber with
            | -1 -> NewStream
            | x -> AggregateVersion x

        return! writeToStream stream expectedVersion eventStreamEvents
    }
       
    let fillOutMetadata uniqueId =
        LazyList.map (fun (evt, buildMetadata) -> 
            let metadata = buildMetadata uniqueId 
            (evt, metadata)
        )

    let addStateChangeEvents blockBuilders uniqueId snapshot stateChangeHandlers events = 
        let applyEventToSnapshot (event, metadata) snapshot = 
            AggregateStateBuilder.applyToSnapshot blockBuilders () event (snapshot.LastEventNumber + 1) metadata snapshot

        let runStateChangeHandlers beforeSnapshot afterSnapshot =
            LazyList.map (fun (h:IStateChangeHandler<_, _>) -> h.Handler beforeSnapshot afterSnapshot |> LazyList.ofSeq)
            >> LazyList.concat
            >> fillOutMetadata uniqueId
        
        let generator (beforeSnapshot, events) = maybe {
            let! (x,xs) = LazyList.tryUncons events
            let afterSnapshot = applyEventToSnapshot x beforeSnapshot
            let remainingEvents = 
                stateChangeHandlers
                |> runStateChangeHandlers beforeSnapshot afterSnapshot

            return 
                (x, (afterSnapshot, LazyList.append remainingEvents xs))
        }
        
        LazyList.unfold generator (snapshot, events)

    let inline runHandler 
        getUniqueId
        blockBuilders
        stateChangeHandlers
        streamId 
        snapshot
        (commandStateBuilder : IStateBuilder<'TChildState, 'TMetadata, unit>) 
        f = 

        eventStream {
            let commandState = commandStateBuilder.GetState snapshot.State

            let! result = runAsync <| f commandState

            return! 
                match result with
                | Choice1Of2 (r : CommandHandlerOutput<'TBaseEvent,'TMetadata>) -> 
                    eventStream {
                        let uniqueIds = (uniqueIdBuilder getUniqueId).GetState snapshot.State
                        if uniqueIds |> Set.contains r.UniqueId then
                            return Choice2Of2 RunFailure<_>.AlreadyProcessed
                        else
                            let events = 
                                r.Events
                                |> LazyList.ofSeq
                                |> fillOutMetadata r.UniqueId
                                |> addStateChangeEvents blockBuilders r.UniqueId snapshot stateChangeHandlers
                                |> List.ofSeq

                            let! writeResult = writeEvents streamId snapshot.LastEventNumber events

                            log.Debug <| lazy (sprintf "WriteResult: %A" writeResult)
                            
                            return 
                                match writeResult with
                                | WriteResult.WriteSuccess pos ->
                                    Choice1Of2 {
                                        Events = events |> List.map (fun (a,b) -> (streamId, a, b))
                                        Position = Some pos
                                    }
                                | WriteResult.WrongExpectedVersion -> 
                                    Choice2Of2 RunFailure<_>.WrongExpectedVersion
                                | WriteResult.WriteError ex -> 
                                    Choice2Of2 <| RunFailure<_>.WriteError ex
                                | WriteResult.WriteCancelled -> 
                                    Choice2Of2 RunFailure<_>.WriteCancelled
                    }
                | Choice2Of2 x ->
                    eventStream { 
                        return
                            HandlerError x
                            |> Choice2Of2
                    }
        }

    let mapValidationFailureToCommandFailure (x : RunFailure<_>) =
        match x with
        | HandlerError x -> 
            (NonEmptyList.map CommandFailure.ofValidationFailure) x
        | RunFailure.WrongExpectedVersion ->
            CommandFailure.CommandError "WrongExpectedVersion"
            |> NonEmptyList.singleton 
        | AlreadyProcessed ->
            CommandFailure.CommandError "AlreadyProcessed"
            |> NonEmptyList.singleton 
        | RunFailure.WriteCancelled ->
            CommandFailure.CommandError "WriteCancelled"
            |> NonEmptyList.singleton 
        | RunFailure.WriteError ex 
        | Exception ex ->
            CommandFailure.CommandException (None, ex)
            |> NonEmptyList.singleton 

    let retryOnWrongVersion streamId stateBuilder handler = 
        let program = eventStream {
            let! streamState = 
                    stateBuilder
                    |> AggregateStateBuilder.toStreamProgram streamId ()

            return! handler streamState
        }
        EventStream.retryOnWrongVersion program

    let handleCommand
        (commandHandler:CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent>) 
        (aggregateConfiguration : AggregateConfiguration<'TCommandContext,_,_,_>) 
        (commandContext : 'TCommandContext) 
        (cmd : obj)
        : EventStreamProgram<CommandResult<'TBaseEvent,'TMetadata>,'TMetadata> =
        let processCommand cmd commandState = async {
            return! commandHandler.Handler commandState commandContext cmd
        }

        match cmd with
        | :? 'TCmd as cmd -> 
            let getId = FSharpx.Choice.protect (commandHandler.GetId commandContext) cmd
            match getId with
            | Choice1Of2 aggregateId ->
                let stream = aggregateConfiguration.GetStreamName commandContext aggregateId

                let f snapshot = 
                   runHandler 
                        aggregateConfiguration.GetUniqueId 
                        aggregateConfiguration.StateBuilder.GetBlockBuilders
                        aggregateConfiguration.StateChangeHandlers
                        stream 
                        snapshot
                        commandHandler.StateBuilder 
                        (processCommand cmd) 

                eventStream {
                    let! result = retryOnWrongVersion stream aggregateConfiguration.StateBuilder f
                    return
                        result |> Choice.mapSecond mapValidationFailureToCommandFailure
                }
            | Choice2Of2 exn ->
                eventStream {
                    return 
                        (Some "Retrieving aggregate id from command",exn)
                        |> CommandException
                        |> NonEmptyList.singleton 
                        |> Choice2Of2
                }
        | _ -> 
            eventStream {
                return 
                    (sprintf "Invalid command type: %A expected %A" (cmd.GetType()) typeof<'TCmd>)
                    |> CommandError
                    |> NonEmptyList.singleton 
                    |> Choice2Of2
            }
        
    let ToInterface (sb : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TAggregateId, 'TMetadata,'TBaseEvent>) = {
        new ICommandHandler<'TAggregateId,'TCommandContext,'TMetadata, 'TBaseEvent> with 
             member this.GetId context cmd = untypedGetId sb context cmd
             member this.CmdType = typeof<'TCmd>
             member this.AddStateBuilder builders = AggregateStateBuilder.combineHandlers sb.StateBuilder.GetBlockBuilders builders
             member this.Handler (aggregateConfig : AggregateConfiguration<'TCommandContext, 'TAggregateId, 'TMetadata, 'TBaseEvent>) commandContext cmd = 
                handleCommand sb aggregateConfig commandContext cmd
             member this.Visitable = {
                new IRegistrationVisitable with
                    member x.Receive a r = r.Visit<'TCmd>(a)
             }
        }

    let withCmdId (getId : 'TCmd -> 'TId) (builder : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent>) = 
        { builder with GetId = (fun _ cmd -> getId cmd )}

    let buildCmd (sb : CommandHandler<'TCmd, 'TCommandContext, 'TState, 'TId, 'TMetadata, 'TBaseEvent>) : ICommandHandler<'TId,'TCommandContext,'TMetadata, 'TBaseEvent> = 
        ToInterface sb

    let buildStateChange (stateBuilder : IStateBuilder<'TState,'TMetadata,unit>) (handler : 'TState -> 'TState -> seq<'TBaseEvent * metadataBuilder<'TMetadata>>) = {
        new IStateChangeHandler<'TMetadata, 'TBaseEvent> with
            member this.AddStateBuilder builders = AggregateStateBuilder.combineHandlers stateBuilder.GetBlockBuilders builders
            member this.Handler beforeSnapshot afterSnapshot =
                let beforeState = stateBuilder.GetState beforeSnapshot.State
                let afterState = stateBuilder.GetState afterSnapshot.State
                if beforeState <> afterState then
                    handler beforeState afterState
                else
                    Seq.empty
    }

    let getEventInterfaceForLink<'TLinkEvent,'TAggregateId,'TMetadata, 'TCommandContext, 'TEventContext when 'TAggregateId : equality> (fId : 'TLinkEvent -> 'TAggregateId) (metadataBuilder : metadataBuilder<'TMetadata>) = {
        new IEventHandler<'TAggregateId,'TMetadata, 'TEventContext > with 
             member this.EventType = typeof<'TLinkEvent>
             member this.AddStateBuilder builders = builders
             member this.Handler aggregateConfig eventContext (evt : PersistedEvent<'TMetadata>) = 
                eventStream {
                    let aggregateId = fId (evt.Body :?> 'TLinkEvent)
                    let sourceMessageId = evt.EventId.ToString()
                    let metadata = metadataBuilder sourceMessageId

                    let resultingStream = aggregateConfig.GetStreamName eventContext aggregateId

                    let! result = EventStream.writeLink resultingStream Any evt.StreamId evt.EventNumber metadata

                    return 
                        match result with
                        | WriteResult.WriteSuccess _ -> 
                            log.Debug <| lazy (sprintf "Wrote Link To %A %A %A" DateTime.Now.Ticks evt.StreamId evt.EventNumber)
                            ()
                        | WriteResult.WrongExpectedVersion -> failwith "WrongExpectedVersion writing event. TODO: retry"
                        | WriteResult.WriteError ex -> failwith <| sprintf "WriteError writing event: %A" ex
                        | WriteResult.WriteCancelled -> failwith "WriteCancelled writing event" } 
                |> Async.returnM
    } 

    let processSequence
        (aggregateConfig : AggregateConfiguration<'TEventContext,'TId,'TMetadata, 'TBaseEvent>)
        (stateBuilder : IStateBuilder<'TState,_,_>)
        (eventContext : 'TEventContext)  
        (handlers : AsyncSeq<MultiEventRun<'TId,'TMetadata,'TState>>) 
        =
        handlers
        |> AsyncSeq.map (fun (id, handler) ->
            eventStream {
                let resultingStream = aggregateConfig.GetStreamName eventContext id
                let! stateSnapshot = 
                    aggregateConfig.StateBuilder 
                    |> AggregateStateBuilder.toStreamProgram resultingStream ()
                let state = stateBuilder.GetState stateSnapshot.State
                let evts = handler state

                let resultingEvents = 
                    evts
                    |> Seq.map (fun (event, buildMetadata) ->
                        // todo get this from system
                        let metadata =  
                            buildMetadata (Guid.NewGuid().ToString())
                        (event, metadata)
                    )
                    |> Seq.toList

                let! result = writeEvents resultingStream stateSnapshot.LastEventNumber resultingEvents

                return 
                    match result with
                    | WriteResult.WriteSuccess _ -> ()
                    | WriteResult.WrongExpectedVersion -> failwith "WrongExpectedVersion writing event. TODO: retry"
                    | WriteResult.WriteError ex -> failwith <| sprintf "WriteError writing event: %A" ex
                    | WriteResult.WriteCancelled -> failwith "WriteCancelled writing event"
            }
        )

    let getEventInterfaceForOnEvent<'TOnEvent, 'TEvent, 'TId, 'TState, 'TMetadata, 'TCommandContext, 'TEventContext when 'TId : equality> (stateBuilder: IStateBuilder<_,_,_>) (fId : ('TOnEvent * 'TEventContext) -> AsyncSeq<MultiEventRun<'TId,'TMetadata,'TState>>) = {
        new IEventHandler<'TId,'TMetadata, 'TEventContext> with 
            member this.EventType = typeof<'TOnEvent>
            member this.AddStateBuilder builders = AggregateStateBuilder.combineHandlers stateBuilder.GetBlockBuilders builders
            member this.Handler aggregateConfig eventContext evt = 
                let typedEvent = evt.Body :?> 'TOnEvent

                fId (typedEvent, eventContext) 
                |> processSequence aggregateConfig stateBuilder eventContext
                |> AsyncSeq.fold EventStream.combine EventStream.empty
    }

    let linkEvent<'TLinkEvent,'TId,'TCommandContext,'TEventContext,'TMetadata when 'TId : equality> fId (metadata : metadataBuilder<'TMetadata>) = 
        getEventInterfaceForLink<'TLinkEvent,'TId,'TMetadata,'TCommandContext,'TEventContext> fId metadata

    let onEventMultiAsync<'TOnEvent,'TEvent,'TEventState,'TId, 'TMetadata, 'TCommandContext,'TEventContext when 'TId : equality> 
        stateBuilder 
        (fId : ('TOnEvent * 'TEventContext) -> AsyncSeq<MultiEventRun<'TId,'TMetadata,'TEventState>>) = 
        getEventInterfaceForOnEvent<'TOnEvent,'TEvent,'TId,'TEventState, 'TMetadata, 'TCommandContext,'TEventContext> stateBuilder fId

    let onEventMulti<'TOnEvent,'TEvent,'TEventState,'TId, 'TMetadata, 'TCommandContext,'TEventContext when 'TId : equality> 
        stateBuilder 
        (fId : ('TOnEvent * 'TEventContext) -> seq<MultiEventRun<'TId,'TMetadata,'TEventState>>) = 
        onEventMultiAsync stateBuilder (fId >> AsyncSeq.ofSeq)

    let onEvent<'TOnEvent,'TEventState,'TId,'TMetadata,'TEventContext when 'TId : equality> 
        (fId : 'TOnEvent -> 'TEventContext -> 'TId) 
        (stateBuilder : IStateBuilder<'TEventState, 'TMetadata, unit>) 
        (runEvent : 'TEventState -> 'TOnEvent -> seq<obj * metadataBuilder<'TMetadata>>) = 
        
        let runEvent' = fun evt state ->
            runEvent state evt

        let handler = 
            (fun (evt, eventCtx) -> 
                let aggId = fId evt eventCtx
                let h = runEvent' evt
                (aggId, h) |> Seq.singleton
            )
        onEventMulti stateBuilder handler

type AggregateDefinition<'TAggregateId, 'TCommandContext, 'TEventContext, 'TMetadata,'TBaseEvent,'TAggregateType when 'TAggregateId : equality and 'TAggregateType : comparison> = {
    GetUniqueId : 'TMetadata -> string option
    GetCommandStreamName : 'TCommandContext -> 'TAggregateId -> string
    GetEventStreamName : 'TEventContext -> 'TAggregateId -> string
    Handlers : AggregateHandlers<'TAggregateId, 'TCommandContext, 'TEventContext, 'TMetadata, 'TBaseEvent>
    AggregateType : 'TAggregateType
    Wakeup : IWakeupHandler<'TAggregateId,'TCommandContext, 'TMetadata, 'TBaseEvent> option
}

module Aggregate = 
    let aggregateDefinitionFromHandlers
        (aggregateType : 'TAggregateType)
        (getUniqueId : 'TMetadata -> string option)
        (getCommandStreamName : 'TCommandContext -> 'TAggregateId -> string)
        (getEventStreamName : 'TEventContext -> 'TAggregateId -> string) 
        handlers =
            {
                AggregateType = aggregateType
                GetUniqueId = getUniqueId
                GetCommandStreamName = getCommandStreamName
                GetEventStreamName = getEventStreamName
                Handlers = handlers
                Wakeup = None
            }

    let toAggregateDefinition<'TEvents, 'TAggregateId, 'TCommandContext, 'TEventContext, 'TMetadata,'TBaseEvent,'TAggregateType when 'TAggregateId : equality and 'TAggregateType : comparison>
        (aggregateType : 'TAggregateType)
        (getUniqueId : 'TMetadata -> string option)
        (getCommandStreamName : 'TCommandContext -> 'TAggregateId -> string)
        (getEventStreamName : 'TEventContext -> 'TAggregateId -> string) 
        (commandHandlers : AggregateCommandHandlers<'TAggregateId,'TCommandContext, 'TMetadata,'TBaseEvent>)
        (eventHandlers : AggregateEventHandlers<'TAggregateId,'TMetadata, 'TEventContext >)
        = 
            let handlers =
                commandHandlers |> Seq.fold (fun (x:AggregateHandlers<_,_,_,_,_>) h -> x.AddCommandHandler h) AggregateHandlers.Empty

            let handlers =
                eventHandlers |> Seq.fold (fun (x:AggregateHandlers<_,_,_,_,_>) h -> x.AddEventHandler h) handlers

            aggregateDefinitionFromHandlers 
                aggregateType
                getUniqueId
                getCommandStreamName
                getEventStreamName
                handlers

    let withWakeup 
        (wakeupFold : WakeupFold<'TMetadata>) 
        (stateBuilder : IStateBuilder<'T,'TMetadata, unit>) 
        (wakeupHandler : DateTime -> 'T ->  seq<'TBaseEvent * metadataBuilder<'TMetadata>>)
        (aggregateDefinition : AggregateDefinition<_,_,_,'TMetadata,'TBaseEvent,'TAggregateType>) =

        let handler time t : Async<Choice<CommandHandlerOutput<_,_>,_>> = async {
            let result = wakeupHandler time t
            return 
                {
                    CommandHandlerOutput.UniqueId = Guid.NewGuid().ToString()
                    Events = result
                }
                |> Choice1Of2
        }

        let wakeup = {
            new IWakeupHandler<'TAggregateId,'TCommandContext, 'TMetadata, 'TBaseEvent> with
                member x.WakeupFold = wakeupFold
                member x.Handler aggregateConfiguration streamId (time : DateTime) =
                    eventStream {
                        let run streamState = AggregateActionBuilder.runHandler aggregateConfiguration.GetUniqueId aggregateConfiguration.StateBuilder.GetBlockBuilders aggregateConfiguration.StateChangeHandlers streamId streamState stateBuilder (handler time)
                        let! result = AggregateActionBuilder.retryOnWrongVersion streamId stateBuilder run
                        match result with
                        | Choice2Of2 failure ->
                            failwith "WakeupHandler failed %A" failure
                        | _ -> 
                            ()
                    }
        }
        { aggregateDefinition with Wakeup = Some wakeup }