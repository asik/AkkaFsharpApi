module AkkaWrapper

open Akka.Actor
open System.Threading.Tasks

type LifecycleMessage =
    | PreStart
    | PostStop
    | PreRestart of reason: exn * message: obj
    | PostRestart of reason: exn
    | Unhandled of message: obj

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

    override this.PreRestart(reason, message) =
        if not (this.CallLifecycleHandler(PreRestart(reason, message)))
        then base.PreRestart(reason, message)

    override this.PostRestart reason =
        if not (this.CallLifecycleHandler(PostRestart reason))
        then base.PostRestart reason

    override this.Unhandled message =
        if not (this.CallLifecycleHandler(Unhandled message))
        then base.Unhandled message


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

type ReceiveHandler<'Message, 'State> = 'Message -> 'State -> MessageContext -> Task<'State>
type TerminatedHandler<'State> = Terminated -> 'State -> MessageContext -> Task<'State>
type LifecycleHandler<'State> = LifecycleMessage -> 'State -> MessageContext -> 'State

type PropsBuilder<'Message, 'State> =
    { InitialState: 'State;
      OnReceive: ReceiveHandler<'Message, 'State>
      OnTerminated: TerminatedHandler<'State> option
      OnLifecycle: LifecycleHandler<'State> option
      SupervisorStrategy: SupervisorStrategy option }
with
    member x.ToProps () =
        createProps (x.InitialState) (x.OnReceive) x.OnTerminated x.OnLifecycle x.SupervisorStrategy
    
    static member Create (initialState, onReceive, ?onTerminated, ?onLifecycle, ?supervisorStrategy) =
        { InitialState = initialState 
          OnReceive = onReceive 
          OnTerminated = onTerminated
          OnLifecycle = onLifecycle
          SupervisorStrategy = supervisorStrategy }.ToProps()

type StatelessReceiveHandler<'Message> = 'Message -> MessageContext -> Task<unit>
type StatelessTerminatedHandler = Terminated -> MessageContext -> Task<unit>
type StatelessLifecycleHandler = LifecycleMessage -> MessageContext -> unit

type StatelessPropsBuilder<'Message> =
    { OnReceive: StatelessReceiveHandler<'Message>
      OnTerminated: StatelessTerminatedHandler option
      OnLifecycle: StatelessLifecycleHandler option
      SupervisorStrategy: SupervisorStrategy option }
with
    member x.ToProps () =
        let onReceive = fun (message: 'Message) (_state: unit) messageContext -> task {
            do! x.OnReceive message messageContext
        }
        
        let onTerminated = 
            x.OnTerminated 
            |> Option.map(fun onTerminated -> fun terminated _state messageContext -> task { 
                do! onTerminated terminated messageContext
                return ()
            })

        let onLifecycle =
            x.OnLifecycle
            |> Option.map(fun onLifecycle -> fun lifecycleMessage _state messageContext ->
                onLifecycle lifecycleMessage messageContext
            )
        createProps () onReceive onTerminated onLifecycle x.SupervisorStrategy
    
    static member Create (onReceive, ?onTerminated, ?onLifecycle, ?supervisorStrategy) =
        { OnReceive = onReceive 
          OnTerminated = onTerminated
          OnLifecycle = onLifecycle
          SupervisorStrategy = supervisorStrategy }.ToProps()

