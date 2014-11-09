﻿namespace Eventful

open System
open System.Collections.Generic
open System.Threading

open Metrics

open FSharpx

type BulkProjector<'TKey, 'TMessage when 'TMessage :> IBulkMessage>
    (
        documentProcessor : BatchEventProcessor<'TKey, 'TMessage>,
        projectorName : string,
        maxEventQueueSize : int,
        eventWorkers : int,
        onEventComplete : 'TMessage -> Async<unit>,
        getPersistedPosition : Async<EventPosition option>,
        writeUpdatedPosition : EventPosition -> Async<bool>,
        cancellationToken : CancellationToken,
        workTimeout : TimeSpan option,
        ?keyComparer : IComparer<'TKey>
    ) =
    let log = createLogger "Eventful.BulkProjector"

    let completeItemsTracker = Metric.Meter(sprintf "EventsComplete %s" projectorName, Unit.Items)
    let processingExceptions = Metric.Meter(sprintf "ProcessingExceptions %s" projectorName, Unit.Items)

    let tryEvent key events =
        documentProcessor.Process(key, events).Invoke() |> Async.AwaitTask
        
    let processEvent key values = async {
        let cachedValues = values |> Seq.cache
        let maxAttempts = 10
        let rec loop count exceptions = async {
            if count < maxAttempts then
                try
                    let! attempt = tryEvent key cachedValues
                    match attempt with
                    | Choice1Of2 _ ->
                        ()
                    | Choice2Of2 ex ->
                        return! loop (count + 1) (ex::exceptions)
                with | ex ->
                    return! loop(count + 1) (ex::exceptions)
            else
                processingExceptions.Mark()
                log.Error <| lazy(sprintf "Processing failed permanently for %s %A. Exceptions to follow." projectorName key)
                for ex in exceptions do
                    log.ErrorWithException <| lazy(sprintf "Processing failed permanently for %s %A" projectorName key, ex)
                ()
        }
        do! loop 0 []
    }

    let grouper (event : 'TMessage) =
        let docIds = 
            documentProcessor.MatchingKeys event

        (event, docIds)
    
    let tracker = 
        let t = new LastCompleteItemAgent<EventPosition>(name = projectorName)

        async {
            let! persistedPosition = getPersistedPosition

            let position = 
                persistedPosition |> Option.getOrElse EventPosition.Start

            t.Start position
            t.Complete position
                
        } |> Async.RunSynchronously

        t

    let eventComplete (event : 'TMessage) =
        seq {
            let position = event.GlobalPosition
            match position with
            | Some position ->
                yield async {
                    tracker.Complete(position)
                    completeItemsTracker.Mark(1L)
                }
            | None -> ()

            yield onEventComplete event
        }
        |> Async.Parallel
        |> Async.Ignore

    let queue = 
        new WorktrackingQueue<_,_,_>(
            grouper, 
            processEvent, 
            maxEventQueueSize, 
            eventWorkers, 
            eventComplete, 
            name = projectorName + " processing", 
            cancellationToken = cancellationToken, 
            ?groupComparer = keyComparer, 
            runImmediately = false,
            workTimeout = workTimeout)
        :> IWorktrackingQueue<_,_,_>

    let mutable lastPositionWritten : Option<EventPosition> = None

    /// fired each time a full queue is detected
    [<CLIEvent>]
    member this.QueueFullEvent = queue.QueueFullEvent

    member x.LastComplete () = tracker.LastComplete()

    // todo ensure this is idempotent
    // at the moment it can be called multiple times
    member x.StartPersistingPosition () = 
        let rec loop () =  async {
            do! Async.Sleep(5000)

            let! position = x.LastComplete()

            let! positionWasUpdated =
                match (position, lastPositionWritten) with
                | Some position, None -> 
                    writeUpdatedPosition position
                | Some position, Some lastPosition
                    when position <> lastPosition ->
                    writeUpdatedPosition position
                | _ -> async { return false }

            if positionWasUpdated then
                lastPositionWritten <- position

            let! ct = Async.CancellationToken
            if(ct.IsCancellationRequested) then
                return ()
            else
                return! loop ()
        }
            
        let taskName = sprintf "Persist Position %s" projectorName
        let task = runAsyncAsTask taskName cancellationToken <| loop ()
        
        ()

    member x.ProjectorName = projectorName

    member x.Enqueue (message : 'TMessage) =
        async {
            match message.GlobalPosition with
            | Some position -> 
                tracker.Start position
            | None -> ()

            do! queue.Add message
        }
   
    member x.WaitAll = queue.AsyncComplete

    member x.StartWork () = 
        // writeQueue.StartWork()
        queue.StartWork()