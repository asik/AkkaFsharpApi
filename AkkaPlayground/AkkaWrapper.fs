module AkkaWrapper

open Akka.Actor

type LifecycleMessage =
    | PreStart
    | PostStop
    | PreRestart of exn * obj
    | PostRestart of exn
    | Unhandled of obj

type MessageContext =
    { Self: IActorRef
      Sender: IActorRef
      ActorContext: IUntypedActorContext }

type ActorWrapper<'Message, 'State> private (onLifecycleMessage, initialState: 'State) =
    inherit ReceiveActor()
            
    new(initialState, onReceive, onTerminated, onLifecycleMessage) as this =
        ActorWrapper(onLifecycleMessage, initialState)
        then
            this.ReceiveAsync<'Message>
                (fun message -> task {
                    let! newState = onReceive message this.CurrentState (this.GetMessageContext())
                    this.CurrentState <- newState
                })
            
            match onTerminated with
            | Some onTerminated ->
                this.ReceiveAsync<Terminated>
                    (fun terminated -> task {
                        let! newState = onTerminated terminated this.CurrentState (this.GetMessageContext())
                        this.CurrentState <- newState
                    })
            | None -> ()

    member val private CurrentState = initialState with get, set

    member private this.GetMessageContext() =
        { Self = this.Self
          Sender = this.Sender
          ActorContext = UntypedActor.Context }

    member private this.CallLifecycleHandler message =
        match onLifecycleMessage with
        | Some fn ->
            this.CurrentState <- fn message this.CurrentState (this.GetMessageContext())
            true
        | None -> false

    override this.PreStart() =
        if not (this.CallLifecycleHandler PreStart)
        then base.PreStart()

    override this.PostStop() =
        if not (this.CallLifecycleHandler PostStop)
        then base.PostStop()

    override this.PreRestart(exn, msg) =
        if not (this.CallLifecycleHandler(PreRestart(exn, msg)))
        then base.PreRestart(exn, msg)

    override this.PostRestart exn =
        if not (this.CallLifecycleHandler(PostRestart exn))
        then base.PostRestart exn

    override this.Unhandled msg =
        if not (this.CallLifecycleHandler(Unhandled msg))
        then base.Unhandled msg


let createProps<'Message, 'State> initialState onReceive onTerminated onLifecycle supervisorStrategy =
    match supervisorStrategy with
    | Some supervisorStrategy ->
        Props.Create(
            (fun () -> ActorWrapper<'Message, 'State>(initialState, onReceive, onTerminated, onLifecycle)),
            supervisorStrategy
        )
    | None ->
        Props.Create(
            fun () -> ActorWrapper(initialState, onReceive, onTerminated, onLifecycle)
        )

type ActorFactory(system: ActorSystem) =

    member _.Create<'Message, 'State>(initialState, onReceive, ?onTerminated, ?onLifecycle, ?supervisorStrategy) = 
        createProps<'Message, 'State> initialState onReceive onTerminated onLifecycle supervisorStrategy
        |> system.ActorOf

    member _.CreateStateless<'Message>(onReceive, ?onTerminated, ?onLifecycle, ?supervisorStrategy) =
        let onReceive = fun (message: 'Message) (_state: unit) context -> task {
            do! onReceive message context
        }
            
        let onTerminated = 
            onTerminated 
            |> Option.map(fun onTerminated -> fun (terminated: Terminated) (_state:unit) (b: MessageContext) -> task { 
                do! onTerminated terminated b
                return ()
            })

        let onLifecycle =
            onLifecycle
            |> Option.map(fun onLifecycle -> fun (message: LifecycleMessage) (_state: unit) (context: MessageContext) ->
                onLifecycle message context
            )
        createProps () onReceive onTerminated onLifecycle supervisorStrategy
        |> system.ActorOf

