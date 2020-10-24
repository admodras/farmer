[<AutoOpen>]
module Farmer.Arm.Storage

open Farmer
open Farmer.Storage
open Farmer.CoreTypes

let storageAccounts = ResourceType ("Microsoft.Storage/storageAccounts", "2019-04-01")
let containers = ResourceType ("Microsoft.Storage/storageAccounts/blobServices/containers", "2018-03-01-preview")
let fileShares = ResourceType ("Microsoft.Storage/storageAccounts/fileServices/shares", "2019-06-01")
let queues = ResourceType ("Microsoft.Storage/storageAccounts/queueServices/queues", "2019-06-01")
let managementPolicies = ResourceType ("Microsoft.Storage/storageAccounts/managementPolicies", "2019-06-01")
let roleAssignments = ResourceType ("Microsoft.Storage/storageAccounts/providers/roleAssignments", "2018-09-01-preview")

module Providers =
    type RoleAssignment =
        { StorageAccount : StorageAccountName
          RoleDefinitionId : RoleId
          PrincipalId : PrincipalId }
        interface IArmResource with
            member this.ResourceName =
                sprintf "%s/%s/%O"
                    this.StorageAccount.ResourceName.Value
                    "Microsoft.Authorization"
                    (DeterministicGuid.create(this.StorageAccount.ResourceName.Value + this.PrincipalId.ArmExpression.Value + this.RoleDefinitionId.ToString()))
                |> ResourceName
            member this.JsonModel =
                let iar = this :> IArmResource
                let dependencies =
                    [ ResourceId.create(storageAccounts, this.StorageAccount.ResourceName) ]
                    @ (this.PrincipalId.ArmExpression.Owner |> Option.toList)

                {| roleAssignments.Create(iar.ResourceName, dependsOn = dependencies) with
                    tags =
                        {| displayName =
                            match this.PrincipalId.ArmExpression.Owner with
                            | None -> this.RoleDefinitionId.Name
                            | Some owner -> sprintf "%s (%s)" this.RoleDefinitionId.Name owner.Name.Value
                        |}
                    properties =
                        {| roleDefinitionId = this.RoleDefinitionId.ArmValue.Eval()
                           principalId = this.PrincipalId.ArmExpression.Eval() |}
                |} :> _

[<RequireQualifiedAccess>]
type StorageAccountKind =
    | V1 | V2 | BlobOnly | FilesOnly | BlockBlobOnly
    member this.ArmValue = match this with | V1 -> "Storage" | V2 -> "StorageV2" | BlobOnly -> "BlobStorage" | FilesOnly -> "FileStorage" | BlockBlobOnly -> "BlockBlobStorage"

type StorageAccount =
    { Name : StorageAccountName
      Location : Location
      Sku : Sku
      Kind : StorageAccountKind
      Dependencies : ResourceId list
      EnableHierarchicalNamespace : bool option
      StaticWebsite : {| IndexPage : string; ErrorPage : string option; ContentPath : string |} option
      Tags: Map<string,string>}
    interface IArmResource with
        member this.ResourceName = this.Name.ResourceName
        member this.JsonModel =
            {| storageAccounts.Create(this.Name.ResourceName, this.Location, this.Dependencies, this.Tags) with
                sku = {| name = this.Sku.ArmValue |}
                kind = this.Kind.ArmValue
                properties =
                 match this.EnableHierarchicalNamespace with
                 | Some hnsEnabled -> {| isHnsEnabled = hnsEnabled |} :> obj
                 | _ -> {||} :> obj
            |} :> _
    interface IPostDeploy with
        member this.Run _ =
            this.StaticWebsite
            |> Option.map(fun staticWebsite -> result {
                let! enableStaticResponse = Deploy.Az.enableStaticWebsite this.Name.ResourceName.Value staticWebsite.IndexPage staticWebsite.ErrorPage
                printfn "Deploying content of %s folder to $web container for storage account %s" staticWebsite.ContentPath this.Name.ResourceName.Value
                let! uploadResponse = Deploy.Az.batchUploadStaticWebsite this.Name.ResourceName.Value staticWebsite.ContentPath
                return enableStaticResponse + ", " + uploadResponse
            })

module BlobServices =
    type Container =
        { Name : StorageResourceName
          StorageAccount : ResourceName
          Accessibility : StorageContainerAccess }
        interface IArmResource with
            member this.ResourceName = this.Name.ResourceName
            member this.JsonModel =
                {| containers.Create(this.StorageAccount/"default"/this.Name.ResourceName, dependsOn = [ ResourceId.create this.StorageAccount ]) with
                    properties =
                     {| publicAccess =
                         match this.Accessibility with
                         | Private -> "None"
                         | Container -> "Container"
                         | Blob -> "Blob" |}
                |} :> _

module FileShares =
    type FileShare =
        { Name: StorageResourceName
          ShareQuota: int<Gb> option
          StorageAccount: ResourceName }
        interface IArmResource with
            member this.ResourceName = this.Name.ResourceName
            member this.JsonModel =
                {| fileShares.Create(this.StorageAccount/"default"/this.Name.ResourceName, dependsOn = [ ResourceId.create this.StorageAccount ]) with
                    properties = {| shareQuota = this.ShareQuota |> Option.defaultValue 5120<Gb> |}
                |} :> _

module Queues =
    type Queue =
        { Name : StorageResourceName
          StorageAccount : ResourceName }
        interface IArmResource with
            member this.ResourceName = this.Name.ResourceName
            member this.JsonModel =
                queues.Create(this.StorageAccount/"default"/this.Name.ResourceName, dependsOn = [ ResourceId.create this.StorageAccount ]) :> _

module ManagementPolicies =
    type ManagementPolicy =
        { Rules :
            {| Name : ResourceName
               CoolBlobAfter : int<Days> option
               ArchiveBlobAfter : int<Days> option
               DeleteBlobAfter : int<Days> option
               DeleteSnapshotAfter : int<Days> option
               Filters : string list |} list
          StorageAccount : ResourceName }
        member this.ResourceName = this.StorageAccount/"default"
        interface IArmResource with
            member this.ResourceName = this.ResourceName
            member this.JsonModel =
                {| managementPolicies.Create(this.ResourceName, dependsOn = [ ResourceId.create this.StorageAccount ]) with
                    properties =
                     {| policy =
                         {| rules = [
                             for rule in this.Rules do
                                 {| enabled = true
                                    name = rule.Name.Value
                                    ``type`` = "Lifecycle"
                                    definition =
                                     {| actions =
                                         {| baseBlob =
                                             {| tierToCool = rule.CoolBlobAfter |> Option.map (fun days -> {| daysAfterModificationGreaterThan = days |} |> box) |> Option.toObj
                                                tierToArchive = rule.ArchiveBlobAfter |> Option.map (fun days -> {| daysAfterModificationGreaterThan = days |} |> box) |> Option.toObj
                                                delete = rule.DeleteBlobAfter |> Option.map (fun days -> {| daysAfterModificationGreaterThan = days |} |> box) |> Option.toObj |}
                                            snapshot =
                                             rule.DeleteSnapshotAfter
                                             |> Option.map (fun days -> {| delete = {| daysAfterCreationGreaterThan = days |} |} |> box)
                                             |> Option.toObj
                                         |}
                                        filters =
                                         {| blobTypes = [ "blockBlob" ]
                                            prefixMatch = rule.Filters |}
                                     |}
                                 |}
                             ]
                         |}
                     |}
                |} :> _