﻿namespace CurryOn.Akka

open Akka.Actor
open Akka.Configuration
open Akka.Event
open Akka.FSharp
open Akka.Persistence
open Akka.Persistence.Journal
open CurryOn.Common
open FSharp.Control
open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks

[<AbstractClass>]
type StreamingEventJournal<'provider when 'provider :> IEventJournalProvider and 'provider: (new: unit -> 'provider)> (config: Config) as journal = 
    inherit AsyncWriteJournal()
    let context = AsyncWriteJournal.Context
    let persistenceIdSubscribers = new ConcurrentDictionary<string, Set<IActorRef>>()
    let tagSubscribers = new ConcurrentDictionary<string, Set<IActorRef>>()
    let provider = new 'provider() :> IEventJournalProvider
    let writeJournal = provider.GetEventJournal config context
    let tagSequenceNr = ImmutableDictionary<string, int64>.Empty;
    
    let log = context.GetLogger()
    let handled _ = true
    let unhandled message = journal.UnhandledMessage message |> fun _ -> false

    member this.UnhandledMessage message = base.Unhandled message

    override this.WriteMessagesAsync messages =
        task {
            let newPersistenceIds = Collections.Generic.HashSet<string>()
            let newTags = Collections.Generic.HashSet<string>()
            let indexOperations = 
                messages 
                |> Seq.map (fun message ->
                    operation {
                        let persistentMessages =  message.Payload |> unbox<IImmutableList<IPersistentRepresentation>> 
                        let events = persistentMessages |> Seq.map (fun persistentMessage ->
                            let eventType = persistentMessage.Payload |> getTypeName
                            let tags = 
                                match persistentMessage |> box with
                                | :? Tagged as tagged -> 
                                    let eventTags = tagged.Tags |> Seq.toArray
                                    eventTags |> Seq.iter (newTags.Add >> ignore)
                                    eventTags
                                | _ -> [||] 
                            { PersistenceId = persistentMessage.PersistenceId 
                              Manifest = persistentMessage.Payload |> getFullTypeName
                              Sender = persistentMessage.Sender
                              SequenceNumber = persistentMessage.SequenceNr
                              Event = persistentMessage.Payload
                              WriterId = persistentMessage.WriterGuid
                              Tags = tags }) |> Seq.toList


                        return! writeJournal.PersistEvents events
                    })          
                |> Operation.Parallel
        
            let results = indexOperations |> Async.RunSynchronously
            let errors = results |> Array.fold (fun acc cur ->
                match cur with
                | Success _ -> acc
                | Failure events -> 
                    let exceptions = 
                        events 
                        |> List.map (fun event -> event.ToException()) 
                        |> List.filter (fun opt -> opt.IsSome) 
                        |> List.map (fun opt -> opt.Value)
                    acc @ exceptions) List<exn>.Empty

            return match errors with
                    | [] -> null
                    | _ -> ImmutableList.CreateRange(errors) :> IImmutableList<exn>
        } 

    override this.DeleteMessagesToAsync (persistenceId, sequenceNumber) =
        task {
            return! writeJournal.DeleteEvents persistenceId sequenceNumber
                    |> PersistenceOperation.toTask
        } :> Task

    override this.ReadHighestSequenceNrAsync (persistenceId, from) =
        task {
            let! highestSequence = writeJournal.GetMaxSequenceNumber persistenceId from |> Operation.waitTask
            return match highestSequence with
                   | Success success -> 
                        match success.Result with
                        | Some result -> result
                        | None -> 0L
                   | _ -> 0L
        }
   
    override this.ReplayMessagesAsync (context, persistenceId, first, last, max, recoveryCallback) =
        task {
            let! results = writeJournal.GetEvents persistenceId first last max |> PersistenceOperation.toTask
            return results |> Seq.iter (fun persistent -> persistent |> recoveryCallback.Invoke)
        } :> Task

    


