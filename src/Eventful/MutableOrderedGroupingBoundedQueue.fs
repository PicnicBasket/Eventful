﻿namespace Eventful

open System

type GroupEntry<'TItem> = {
    Items : List<Int64 * 'TItem>
    Processing : List<Int64 * 'TItem>
}
  
type MutableOrderedGroupingBoundedQueueMessages<'TGroup, 'TItem when 'TGroup : comparison> = 
  | AddItem of (seq<'TItem * 'TGroup> * Async<unit> * AsyncReplyChannel<unit>)
  | ConsumeWork of (('TGroup * seq<'TItem> -> Async<unit>) * AsyncReplyChannel<unit>)
  | GroupComplete of 'TGroup
  | NotifyWhenAllComplete of AsyncReplyChannel<unit>

type MutableOrderedGroupingBoundedQueue<'TGroup, 'TItem when 'TGroup : comparison>(?maxItems) =
    let log = Common.Logging.LogManager.GetLogger(typeof<MutableOrderedGroupingBoundedQueue<_,_>>)

    let maxItems =
        match maxItems with
        | Some v -> v
        | None -> 10000
    
    let equalityComparer = 
        { new System.Collections.Generic.IEqualityComparer<'TGroup> with
            member this.Equals(x : 'TGroup, y : 'TGroup) : bool = 
                x.CompareTo(y) = 0
            member this.GetHashCode(x) = x.GetHashCode() }
    // normal .NET dictionary for performance
    // very mutable
    let groupItems = new System.Collections.Generic.Dictionary<'TGroup, GroupEntry<'TItem>>(equalityComparer)

    let workQueue = new System.Collections.Generic.Queue<'TGroup>()

    let lastCompleteTracker = new LastCompleteItemAgent2<int64>()

    let addItemToGroup item group =
        let (exists, value) = groupItems.TryGetValue(group)
        let value = 
            if exists then 
                value
            else 
                log.Error(sprintf "Enqueue %A" group)
                workQueue.Enqueue group
                { Items = List.empty; Processing = List.empty } 
        let value' = { value with Items = item::value.Items }
        groupItems.Remove group |> ignore
        groupItems.Add(group, value')
        ()

    let dispatcherAgent = Agent.Start(fun agent -> 
        let rec empty itemIndex = 
            agent.Scan(fun msg -> 
            match msg with
            | AddItem x -> Some (enqueue x itemIndex)
            | NotifyWhenAllComplete reply -> 
                if(itemIndex = 0L) then reply.Reply()
                else lastCompleteTracker.NotifyWhenComplete(itemIndex - 1L, async { reply.Reply() } )
                Some(empty itemIndex)
            | GroupComplete group -> Some(groupComplete group itemIndex)
            | _ -> None)
        and hasWork itemIndex =
            agent.Scan(fun msg ->
            match msg with
            | AddItem x -> Some <| enqueue x itemIndex
            | ConsumeWork x -> Some <| consume x itemIndex
            | GroupComplete group -> Some(groupComplete group itemIndex)
            | NotifyWhenAllComplete reply ->
                lastCompleteTracker.NotifyWhenComplete(itemIndex - 1L, async { reply.Reply() } )
                Some(hasWork itemIndex))
        and enqueue (items, onComplete, reply) itemIndex = async {
            let indexedItems = Seq.zip items (Seq.initInfinite (fun x -> itemIndex + int64 x)) |> Seq.cache
            for ((item, group), index) in indexedItems do
                addItemToGroup (index, item) group
                do! lastCompleteTracker.Start index
            reply.Reply()

            let lastIndex = 
                if Seq.length indexedItems > 0 then
                    indexedItems |> Seq.map snd |> Seq.last
                else
                    itemIndex

            if(lastIndex = itemIndex) then
                do! onComplete
            else
                lastCompleteTracker.NotifyWhenComplete(lastIndex, onComplete)

            return! (nextMessage (lastIndex + 1L)) }
        and groupComplete group itemIndex = async {
            let values = groupItems.Item group
            if (not (values.Items |> List.isEmpty)) then
                log.Error(sprintf "Group Complete Enqueue %A" group)
                workQueue.Enqueue(group)
            else
                groupItems.Remove group |> ignore

            return! nextMessage itemIndex }
        and consume (workCallback, reply) itemIndex = async {
            let nextKey = workQueue.Dequeue()
            let values = groupItems.Item nextKey
            async {
                try
                    log.Error(sprintf "Starting %A" nextKey) 
                    do! workCallback(nextKey,values.Items |> List.rev |> List.map snd) 
                with | e ->
                    System.Console.WriteLine ("Error" + e.Message)
                
                for (i, _) in values.Items do
                    lastCompleteTracker.Complete i
                
                log.Error(sprintf "Complete %A" nextKey) 

                agent.Post <| GroupComplete nextKey

            } |> Async.StartAsTask |> ignore

            reply.Reply()
            let newValues = { values with Items = List.empty; Processing = values.Items }
            groupItems.Remove(nextKey) |> ignore
            groupItems.Add(nextKey, newValues)
            return! nextMessage itemIndex }
        and nextMessage itemIndex = async {
            if(workQueue.Count = 0) then
                return! empty itemIndex
            else
                return! hasWork itemIndex
        }
        empty 0L )

    member this.Add (input:'TInput, group: ('TInput -> (seq<'TItem * 'TGroup>)), ?onComplete : Async<unit>) =
        async {
            let items = group input
            let onCompleteCallback = async {
                match onComplete with
                | Some callback -> return! callback
                | None -> return ()
            }
            do! dispatcherAgent.PostAndAsyncReply(fun ch ->  AddItem (items, onCompleteCallback, ch))
            ()
        }

    member this.Consume (work:(('TGroup * seq<'TItem>) -> Async<unit>)) =
        dispatcherAgent.PostAndAsyncReply(fun ch -> ConsumeWork(work, ch))

    member this.CurrentItemsComplete () = 
        dispatcherAgent.PostAndAsyncReply(fun ch -> NotifyWhenAllComplete(ch))