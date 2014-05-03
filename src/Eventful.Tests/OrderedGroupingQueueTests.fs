﻿namespace Eventful.Tests

open Eventful.New
open System
open Xunit
open System.Threading.Tasks
open FsUnit.Xunit

module OrderedGroupingQueueTests = 

    [<Fact>]
    let ``Can do something`` () : unit = 
        let myQueue = new OrderedGroupingQueue<int, int>()

        let counter1 = new Eventful.CounterAgent()
        let counter2 = new Eventful.CounterAgent()
        let counter3 = new Eventful.CounterAgent()
        let counter4 = new Eventful.CounterAgent()

        let rec consumer (counter : Eventful.CounterAgent)  = async {
            do! myQueue.Consume((fun (g, items) -> async {
                // Console.WriteLine(sprintf "Group: %A Items: %A ItemCount: %d" g items (items |> Seq.length))
                // do! Async.Sleep 100
                do! counter.Incriment(items |> Seq.length)
                return ()
            }))
            return! consumer counter
        }

        consumer counter1 |> Async.Start
        consumer counter2 |> Async.Start
        consumer counter3 |> Async.Start
        consumer counter1 |> Async.Start
        consumer counter2 |> Async.Start
        consumer counter3 |> Async.Start
        consumer counter1 |> Async.Start
        consumer counter2 |> Async.Start
        consumer counter3 |> Async.Start
        consumer counter1 |> Async.Start
        consumer counter2 |> Async.Start
        consumer counter3 |> Async.Start
        consumer counter4 |> Async.Start
        consumer counter4 |> Async.Start
        consumer counter4 |> Async.Start
        consumer counter4 |> Async.Start
        consumer counter4 |> Async.Start
        consumer counter4 |> Async.Start

        async {

            for i in [1..1000000] do
                do! myQueue.Add(i, (fun input -> (input, [input] |> Set.ofList)))

            do! myQueue.CurrentItemsComplete()

            let! value1 = counter1.Get()
            let! value2 = counter2.Get()
            let! value3 = counter3.Get()
            let! value4 = counter4.Get()
           
            printfn "Received %d %d %d %d total: %d" value1 value2 value3 value4 (value1 + value2 + value3 + value4) 

        } |> Async.RunSynchronously