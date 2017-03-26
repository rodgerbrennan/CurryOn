﻿#r "System.Configuration"
#r "System.Runtime.Serialization"
#r "System.Xml"
#r @"..\packages\Akka.1.1.3\lib\net45\Akka.dll"
#r @"..\packages\Akka.FSharp.1.1.3\lib\net45\Akka.FSharp.dll"
#r @"..\packages\FSPowerPack.Core.Community.3.0.0.0\lib\Net40\FSharp.PowerPack.dll"
#r @"..\packages\FSPowerPack.Linq.Community.3.0.0.0\lib\Net40\FSharp.PowerPack.Linq.dll"
#r @"..\packages\FsPickler.3.2.0\lib\net45\FsPickler.dll"
#r @"..\packages\FsPickler.Json.3.2.0\lib\net45\FsPickler.Json.dll"
#r @"..\packages\Newtonsoft.Json.10.0.1\lib\net45\Newtonsoft.Json.dll"
#r @"..\packages\System.Collections.Immutable.1.3.1\lib\portable-net45+win8+wp8+wpa81\System.Collections.Immutable.dll"
#r @"..\packages\FSharp.Quotations.Evaluator.1.0.7\lib\net40\FSharp.Quotations.Evaluator.dll"
#r @"..\CurryOn.Common\bin\debug\CurryOn.Common.dll"
#load "Messaging.fs"
#load "Serialization.fs"

open Akka.Routing
open CurryOn.Common
open CurryOn.Core
open MBrace.FsPickler.Json
open System
open System.IO

[<CLIMutable>]
type DomainEntity =
    {
        Id: Guid;
        Name: string<AggregateName>;
    }
    interface IEntity with
        member this.Id = this.Id.ToString() |> EntityId.New

type DomainEvent =
    {
        Id: Guid;
        Name: string<AggregateName>
        Version: int<version>
    }
    member this.AggregateKey = this.Id.ToString() |> AggregateKey.New
    interface IEvent with
        member this.MessageId = Guid.NewGuid()
        member this.CorrelationId = Guid.NewGuid()
        member this.MessageDate = DateTime.UtcNow
        member this.Key = this.AggregateKey
        member this.Name = "DomainAggregate" |> AggregateName.New
        member this.Tenant = None
        member this.DatePublished = None
        member this.ConsistentHashKey = this.AggregateKey |> box

[<CLIMutable>] 
type DomainAggregate =
    {
        Root: DomainEntity;
        LastEvent: int<version>;
    }
    member this.AggregateKey = this.Root.Id.ToString() |> AggregateKey.New
    interface IAggregate with
        member this.Root= this.Root :> IEntity
        member this.Key = this.AggregateKey
        member this.Name = typeof<DomainAggregate>.AggregateName
        member this.Tenant = None
        member this.LastEvent = this.LastEvent
        member this.Apply event version =
            match event with
            | :? DomainEvent as domainEvent -> {this with Root = {this.Root with Name = domainEvent.Name}; LastEvent = version}
            | _ -> { this with LastEvent = version }
            :> IAggregate
        member this.ConsistentHashKey = this.AggregateKey |> box


let stream = new MemoryStream()
let jsonSerializer = FsPickler.CreateJsonSerializer(true)
jsonSerializer.Serialize(stream, {Id = Guid.NewGuid(); Name = "Test Event" |> AggregateName.New; Version = 12<version>})
let json = stream.ToArray() |> Serialization.UTF8NoBOM.GetString