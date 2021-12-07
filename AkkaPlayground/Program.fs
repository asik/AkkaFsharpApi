open AkkaWrapper
open Akka.Actor
open Akka.FSharp
open System
open System.Threading.Tasks

open type System.Console

let system = Akka.FSharp.System.create "my-system" (Configuration.load ())

type DoorState =
| Opened
| Closed

type DoorActorMessage =
| TimerElapsed

let doorActorRef = 
    let props = PropsBuilder.Create<_, _>(
        Closed,
        fun message state { Sender = sender; ActorContext = context } -> task { 
            match message with
            | TimerElapsed -> 
                WriteLine $"Message: {sender.Path} ({state})"
            return match state with Opened -> Closed | _ -> Opened
        },
        onTerminated = fun terminated state _context -> task { 
            do WriteLine $"Terminated: {terminated}"
            return state
        },
        onLifecycle = fun message state context -> 
            WriteLine $"Lifecycle: {message} {context}" 
            state
    )
    system.ActorOf props

let program _ = task {
    for _ in 0 .. 2 do
        doorActorRef <! TimerElapsed
        do! Task.Delay(TimeSpan.FromSeconds 1)

    doorActorRef <! PoisonPill.Instance
    do! Task.Delay(TimeSpan.FromSeconds 1)
    return 0
}


[<EntryPoint>]
let main argv =
  (task {
    return! program argv
  }).Result

