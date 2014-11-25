﻿namespace Eventful

open System
open FSharpx
open Eventful

type StateRunner<'TMetadata, 'TState, 'TEvent> = 'TEvent -> 'TMetadata -> 'TState -> 'TState

type IStateBlockBuilder<'TMetadata, 'TKey> = 
    abstract Type : Type
    abstract Name : string
    abstract InitialState : obj
    abstract GetRunners<'TEvent> : unit -> (GetEventKey<'TMetadata, 'TEvent, 'TKey> * StateRunner<'TMetadata, Map<string,obj>, 'TEvent>) seq

type IStateBuilder<'TState, 'TMetadata, 'TKey> = 
    abstract GetBlockBuilders : IStateBlockBuilder<'TMetadata, 'TKey> list
    abstract GetState : Map<string, obj> -> 'TState

type StateBuilder<'TState, 'TMetadata, 'TKey when 'TKey : equality>
    (
        name: string, 
        eventFold : EventFold<'TState, 'TMetadata, 'TKey>
    ) = 

    let getStateFromMap (stateMap : Map<string,obj>) =
       stateMap 
       |> Map.tryFind name 
       |> Option.map (fun s -> s :?> 'TState)
       |> Option.getOrElse eventFold.InitialState 

    static member Empty name initialState = new StateBuilder<'TState, 'TMetadata, 'TKey>(name, EventFold.Empty initialState)

    member x.InitialState = eventFold.InitialState

    member x.AddHandler<'T> (h:StateBuilderHandler<'TState, 'TMetadata, 'TKey>) =
        new StateBuilder<'TState, 'TMetadata, 'TKey>(name, eventFold.AddHandler h)

    member x.GetRunners<'TEvent> () : (GetEventKey<'TMetadata, 'TEvent, 'TKey> * StateRunner<'TMetadata, 'TState, 'TEvent>) seq = 
        seq {
            for handler in eventFold.Handlers do
               match handler with
               | AllEvents (getKey, handlerFunction) ->
                    let getKey _ metadata = getKey metadata
                    let stateRunner (evt : 'TEvent) metadata state = 
                        handlerFunction (state, evt, metadata)
                    yield (getKey, stateRunner)
               | SingleEvent (eventType, getKey, handlerFunction) ->
                    if eventType = typeof<'TEvent> then
                        let getKey evt metadata = getKey evt metadata
                        let stateRunner (evt : 'TEvent) metadata state = 
                            handlerFunction (state, evt, metadata)
                        yield (getKey, stateRunner)
        }

    interface IStateBlockBuilder<'TMetadata, 'TKey> with
        member x.Name = name
        member x.Type = typeof<'TState>
        member x.InitialState = eventFold.InitialState :> obj
        member x.GetRunners<'TEvent> () =
            x.GetRunners<'TEvent> ()
            |> Seq.map 
                (fun (getKey, handler) ->
                    let mapHandler evt metadata (stateMap : Map<string,obj>) =
                        let state = getStateFromMap stateMap 
                        let state' = handler evt metadata state
                        stateMap |> Map.add name (state' :> obj)

                    (getKey, mapHandler)
                )

    interface IStateBuilder<'TState, 'TMetadata, 'TKey> with
        member x.GetBlockBuilders = [x :> IStateBlockBuilder<'TMetadata, 'TKey>]
        member x.GetState stateMap = getStateFromMap stateMap

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StateBuilder =
    let nullStateBuilder<'TMetadata, 'TKey when 'TKey : equality> =
        StateBuilder<unit, 'TMetadata, 'TKey>.Empty "$Empty" ()

    let handler (getKey : GetEventKey<'TMetadata, 'TEvent, 'TKey>) (f : HandlerFunction<'TState, 'TMetadata, 'TEvent>) (b : StateBuilder<'TState, 'TMetadata, 'TKey>) =
        b.AddHandler <| SingleEvent (typeof<'TEvent>, EventFold.untypedGetKey getKey, EventFold.untypedHandler f)

    let unitIdHandler (f : HandlerFunction<'TState, 'TMetadata, 'TEvent>) (b : StateBuilder<'TState, 'TMetadata, unit>) =
        handler (fun _ _ -> ()) f b

    // aggregate state has no id, it is scoped to the stream
    let aggregateStateHandler (f : HandlerFunction<'TState, 'TMetadata, 'TEvent>) (b : StateBuilder<'TState, 'TMetadata, unit>) =
        SingleEvent (typeof<'TEvent>, EventFold.untypedGetKey (fun _ _ -> ()), EventFold.untypedHandler f)
        |> b.AddHandler

    let allAggregateEventsHandler (f : ('TState * obj * 'TMetadata) -> 'TState) (b : StateBuilder<'TState, 'TMetadata, unit>) =
        b.AddHandler <| AllEvents (konst (), EventFold.untypedHandler f)

    let allEventsHandler getKey (f : ('TState * obj * 'TMetadata) -> 'TState) (b : StateBuilder<'TState, 'TMetadata, 'TKey>) =
        b.AddHandler <| AllEvents (getKey, EventFold.untypedHandler f)

    let run (key : 'TKey) (evt : 'TEvent) (metadata : 'TMetadata) (builder: StateBuilder<'TState, 'TMetadata, 'TKey> , currentState : 'TState) =
        let keyHandlers = 
            builder.GetRunners<'TEvent>()
            |> Seq.map (fun (getKey, handler) -> (getKey evt metadata, handler))
            |> Seq.filter (fun (k, _) -> k = key)
            |> Seq.map snd

        let acc state (handler : StateRunner<'TMetadata, 'TState, 'TEvent>) =
            handler evt metadata state

        let state' = keyHandlers |> Seq.fold acc currentState
        (builder, state')

    let getKeys (evt : 'TEvent) (metadata : 'TMetadata) (builder: StateBuilder<'TState, 'TMetadata, 'TKey>) =
        builder.GetRunners<'TEvent>()
        |> Seq.map (fun (getKey, _) -> (getKey evt metadata))
        |> Seq.distinct

    let toInterface (builder: StateBuilder<'TState, 'TMetadata, 'TKey>) =
        builder :> IStateBuilder<'TState, 'TMetadata, 'TKey>

    let eventTypeCountBuilder (getId : 'TEvent -> 'TMetadata -> 'TId) =
        StateBuilder.Empty (sprintf "%sCount" typeof<'TEvent>.Name) 0
        |> handler getId (fun (s,_,_) -> s + 1)

type AggregateStateBuilder<'TState, 'TMetadata, 'TKey when 'TKey : equality>
    (
        unitBuilders : IStateBlockBuilder<'TMetadata, 'TKey> list,
        extract : Map<string, obj> -> 'TState
    ) = 

    static member Empty name initialState = StateBuilder.Empty name initialState

    member x.InitialState = 
        let acc s (b : IStateBlockBuilder<'TMetadata, 'TKey>) =
            s |> Map.add b.Name b.InitialState

        unitBuilders 
        |> List.fold acc Map.empty
        |> extract

    interface IStateBuilder<'TState, 'TMetadata, 'TKey> with
        member x.GetBlockBuilders = unitBuilders
        member x.GetState unitStates = extract unitStates

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AggregateStateBuilder =

    let constant<'TState,'TMetdata,'TKey when 'TKey : equality> value = new AggregateStateBuilder<'TState,'TMetdata,'TKey>([], konst value)

    let combine f (b1 : IStateBuilder<'TState1, 'TMetadata, 'TKey>) (b2 : IStateBuilder<'TState2, 'TMetadata, 'TKey>) : IStateBuilder<'TStateCombined, 'TMetadata, 'TKey> =
        let combinedUnitBuilders = 
            Seq.append b1.GetBlockBuilders b2.GetBlockBuilders 
            |> Seq.distinct
            |> List.ofSeq

        let extract unitStates = 
            f (b1.GetState unitStates) (b2.GetState unitStates)

        new AggregateStateBuilder<'TStateCombined, 'TMetadata, 'TKey>(combinedUnitBuilders, extract) :> IStateBuilder<'TStateCombined, 'TMetadata, 'TKey>

    let combineHandlers (h1 : IStateBlockBuilder<'TMetadata, 'TId> list) (h2 : IStateBlockBuilder<'TMetadata, 'TId> list) =
        List.append h1 h2 
        |> Seq.distinct
        |> List.ofSeq

    let ofStateBuilderList (builders : IStateBlockBuilder<'TMetadata, 'TKey> list) =
        new AggregateStateBuilder<Map<string,obj>,'TMetadata, 'TKey>(builders, id)

    let run<'TMetadata, 'TKey, 'TEvent when 'TKey : equality> (unitBuilders : IStateBlockBuilder<'TMetadata, 'TKey> list) key evt metadata currentUnitStates =
        let runBuilder unitStates (builder : IStateBlockBuilder<'TMetadata, 'TKey>) = 
            let keyHandlers = 
                builder.GetRunners<'TEvent>()
                |> Seq.map (fun (getKey, handler) -> (getKey evt metadata, handler))
                |> Seq.filter (fun (k, _) -> k = key)
                |> Seq.map snd

            let acc state (handler : StateRunner<'TMetadata, 'TState, 'TEvent>) =
                handler evt metadata state

            let state' = keyHandlers |> Seq.fold acc unitStates
            state'

        unitBuilders |> List.fold runBuilder currentUnitStates

    let genericRunMethod = 
        let moduleInfo = 
          System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
          |> Seq.find (fun t -> t.FullName = "Eventful.AggregateStateBuilderModule")
        let name = "run"
        moduleInfo.GetMethod(name)

    let dynamicRun (unitBuilders : IStateBlockBuilder<'TMetadata, 'TKey> list) key evt metadata currentUnitStates =
        let specializedMethod = genericRunMethod.MakeGenericMethod(typeof<'TMetadata>, typeof<'TKey>, evt.GetType())
        specializedMethod.Invoke(null, [| unitBuilders; key; evt; metadata; currentUnitStates |]) :?> Map<string, obj>

    let map (f : 'T1 -> 'T2) (stateBuilder: IStateBuilder<'T1, 'TMetadata, 'TKey>) =
        let extract unitStates = stateBuilder.GetState unitStates |> f
        new AggregateStateBuilder<'T2, 'TMetadata, 'TKey>(stateBuilder.GetBlockBuilders, extract) :> IStateBuilder<'T2, 'TMetadata, 'TKey>

    let applyToSnapshot blockBuilders key value metadata snapshot = 
        let state' = dynamicRun blockBuilders key value metadata snapshot.State 
        { snapshot with EventsApplied = snapshot.EventsApplied + 1; State = state' }

    let toStreamProgram streamName (key : 'TKey) (stateBuilder:IStateBuilder<'TState, 'TMetadata, 'TKey>) = EventStream.eventStream {
        let rec loop (snapshot : StateSnapshot) = EventStream.eventStream {
            let! token = EventStream.readFromStream streamName snapshot.EventsApplied
            match token with
            | Some token -> 
                let! (value, metadata : 'TMetadata) = EventStream.readValue token
                return! loop <| applyToSnapshot stateBuilder.GetBlockBuilders key value metadata snapshot
            | None -> 
                return snapshot }
            
        return! loop StateSnapshot.Empty
    }

    let tuple2 b1 b2 =
        combine FSharpx.Prelude.tuple2 b1 b2

    let tuple3 b1 b2 b3 =
        (tuple2 b2 b3)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c)) -> (a,b,c))

    let tuple4 b1 b2 b3 b4 =
        (tuple3 b2 b3 b4)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c,d)) -> (a,b,c,d))

    let tuple5 b1 b2 b3 b4 b5 =
        (tuple4 b2 b3 b4 b5)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c,d,e)) -> (a,b,c,d,e))

    let tuple6 b1 b2 b3 b4 b5 b6 =
        (tuple5 b2 b3 b4 b5 b6)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c,d,e,f)) -> (a,b,c,d,e,f))

    let tuple7 b1 b2 b3 b4 b5 b6 b7 =
        (tuple6 b2 b3 b4 b5 b6 b7)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c,d,e,f,g)) -> (a,b,c,d,e,f,g))

    let tuple8 b1 b2 b3 b4 b5 b6 b7 b8 =
        (tuple7 b2 b3 b4 b5 b6 b7 b8)
        |> combine FSharpx.Prelude.tuple2 b1
        |> map (fun (a,(b,c,d,e,f,g,h)) -> (a,b,c,d,e,f,g,h))