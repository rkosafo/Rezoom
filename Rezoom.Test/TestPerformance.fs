﻿module Rezoom.Test.TestPerformance
open Rezoom
open System
open System.Text
open System.Diagnostics
open NUnit.Framework
open FsUnit

let ret1 = plan { return 1 }
let time f =
    let sw = new Stopwatch()
    sw.Start()
    let mutable iterations = 0L
    while sw.ElapsedMilliseconds < 1000L do
        testSpeed f
        iterations <- iterations + 1L
    sw.Stop()
    printfn "%s iterations in %O" (iterations.ToString("#,###")) sw.Elapsed

[<Test>]
let ``single return`` () =
    time <|
        {   Task = fun () -> ret1
            Batches = []
            Result = Good 1
        }

[<Test>]
let ``nested return`` () =
    time <|
        {   Task = fun () -> plan {
                return! plan {
                    return! plan {
                        return! ret1
                    }
                }
            }
            Batches = []
            Result = Good 1
        }

[<Test>]
let ``bind chain`` () =
    time <|
        {   Task = fun () -> plan {
                let! one1 = ret1
                let! one2 = ret1
                let! one3 = ret1
                return one1 + one2 + one3
            }
            Batches = []
            Result = Good 3
        }

[<Test>]
let ``bind chain with requests`` () =
    time <|
        {   Task = fun () -> plan {
                let! _ = send "x"
                let! one1 = ret1
                let! _ = send "y"
                let! one2 = ret1
                let! _ = send "z"
                let! one3 = ret1
                return one1 + one2 + one3
            }
            Batches =
                [   ["x"]
                    ["y"]
                    ["z"]
                ]
            Result = Good 3
        }

[<Test>]
let ``bind chain with batched requests`` () =
    time <|
        {   Task = fun () -> plan {
                let! one1 = ret1
                let! _ = send "x", send "y", send "z"
                let! one2 = ret1
                let! _ = send "q", send "r", send "s"
                let! one3 = ret1
                return one1 + one2 + one3
            }
            Batches =
                [   ["x";"y";"z"]
                    ["q";"r";"s"]
                ]
            Result = Good 3
        }