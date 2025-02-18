module Functions

open Expecto
open Farmer
open Farmer.Builders
open Farmer.Arm
open Microsoft.Azure.Management.WebSites
open Microsoft.Azure.Management.WebSites.Models
open Microsoft.Rest
open System
open Farmer.WebApp
open Farmer.Identity

let getResource<'T when 'T :> IArmResource> (data:IArmResource list) = data |> List.choose(function :? 'T as x -> Some x | _ -> None)
/// Client instance needed to get the serializer settings.
let dummyClient = new WebSiteManagementClient (Uri "http://management.azure.com", TokenCredentials "NotNullOrWhiteSpace")
let getResourceAtIndex o = o |> getResourceAtIndex dummyClient.SerializationSettings
let getResources (b:#IBuilder) = b.BuildResources Location.WestEurope

let tests = testList "Functions tests" [
    test "Renames storage account correctly" {
        let f = functions { name "test"; storage_account_name "foo" }
        let resources = getResources f
        let site = resources.[3] :?> Web.Site
        let storage = resources.[1] :?> Storage.StorageAccount

        Expect.contains site.Dependencies (storageAccounts.resourceId "foo") "Storage account has not been added a dependency"
        Expect.equal f.StorageAccountName.ResourceName.Value "foo" "Incorrect storage account name on site"
        Expect.equal storage.Name.ResourceName.Value "foo" "Incorrect storage account name"
    }
    test "Implicitly sets dependency on connection string" {
        let db = sqlDb { name "mySql" }
        let sql = sqlServer { name "test2"; admin_username "isaac"; add_databases [ db ] }
        let f = functions { name "test"; storage_account_name "foo"; setting "db" (sql.ConnectionString db) } :> IBuilder
        let site = f.BuildResources Location.NorthEurope |> List.item 3 :?> Web.Site
        Expect.contains site.Dependencies (ResourceId.create (Sql.databases, ResourceName "test2", ResourceName "mySql")) "Missing dependency"
    }
    test "Works with unmanaged storage account" {
        let externalStorageAccount = ResourceId.create(storageAccounts, ResourceName "foo", "group")
        let functionsBuilder = functions { name "test"; link_to_unmanaged_storage_account externalStorageAccount }
        let f = functionsBuilder :> IBuilder
        let resources = getResources f
        let site = resources |> List.item 2 :?> Web.Site

        Expect.isFalse (resources |> List.exists (fun r -> r.ResourceId.Type = storageAccounts)) "Storage Account should not exist"
        Expect.isFalse (site.Dependencies |> Set.contains externalStorageAccount) "Should not be a dependency"
        Expect.stringContains site.AppSettings.["AzureWebJobsStorage"].Value "foo" "Web Jobs Storage setting should have storage account name"
        Expect.stringContains site.AppSettings.["AzureWebJobsDashboard"].Value "foo" "Web Jobs Dashboard setting should have storage account name"
    }
    test "Handles identity correctly" {
        let f : Site = functions { name "" } |> getResourceAtIndex 0
        Expect.isNull f.Identity "Default managed identity should be null"

        let f : Site = functions { system_identity } |> getResourceAtIndex 3
        Expect.equal f.Identity.Type (Nullable ManagedServiceIdentityType.SystemAssigned) "Should have system identity"
        Expect.isNull f.Identity.UserAssignedIdentities "Should have no user assigned identities"

        let f : Site = functions { system_identity; add_identity (createUserAssignedIdentity "test"); add_identity (createUserAssignedIdentity "test2") } |> getResourceAtIndex 3
        Expect.equal f.Identity.Type (Nullable ManagedServiceIdentityType.SystemAssignedUserAssigned) "Should have system identity"
        Expect.sequenceEqual (f.Identity.UserAssignedIdentities |> Seq.map(fun r -> r.Key)) [ "[resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', 'test2')]"; "[resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', 'test')]" ] "Should have two user assigned identities"

    }

    test "Supports always on" {
        let f:Site = functions { name "" } |> getResourceAtIndex 3
        Expect.equal f.SiteConfig.AlwaysOn (Nullable false) "always on should be false by default"

        let f:Site = functions { always_on } |> getResourceAtIndex 3
        Expect.equal f.SiteConfig.AlwaysOn (Nullable true) "always on should be true"
    }

    test "Supports 32 and 64 bit worker processes" {
        let f:Site = functions { worker_process Bitness.Bits32 } |> getResourceAtIndex 3
        Expect.equal f.SiteConfig.Use32BitWorkerProcess (Nullable true) "Should use 32 bit worker process"

        let f:Site = functions { worker_process Bitness.Bits64 } |> getResourceAtIndex 3
        Expect.equal f.SiteConfig.Use32BitWorkerProcess (Nullable false) "Should not use 32 bit worker process"
    }
    
    test "Managed KV integration works correctly" {
        let sa = storageAccount { name "teststorage" }
        let wa = functions { name "testfunc"; setting "storage" sa.Key; secret_setting "secret"; setting "literal" "value"; link_to_keyvault (ResourceName "testfuncvault") }
        let vault = keyVault { name "testfuncvault"; add_access_policy (AccessPolicy.create (wa.SystemIdentity.PrincipalId, [ KeyVault.Secret.Get ])) }
        let vault = vault |> getResources |> getResource<Vault> |> List.head
        let secrets = wa |> getResources |> getResource<Vaults.Secret>
        let site = wa |> getResources |> getResource<Web.Site> |> List.head

        let expectedSettings = Map [
            "storage", LiteralSetting "@Microsoft.KeyVault(SecretUri=https://testfuncvault.vault.azure.net/secrets/storage)"
            "secret", LiteralSetting "@Microsoft.KeyVault(SecretUri=https://testfuncvault.vault.azure.net/secrets/secret)"
            "literal", LiteralSetting "value"
        ]

        Expect.equal site.Identity.SystemAssigned Enabled "System Identity should be enabled"
        Expect.containsAll site.AppSettings expectedSettings "Incorrect settings"

        Expect.equal wa.CommonWebConfig.Identity.SystemAssigned Enabled "System Identity should be turned on"

        Expect.hasLength secrets 2 "Incorrect number of KV secrets"

        Expect.equal secrets.[0].Name.Value "testfuncvault/secret" "Incorrect secret name"
        Expect.equal secrets.[0].Value (ParameterSecret (SecureParameter "secret")) "Incorrect secret value"
        Expect.sequenceEqual secrets.[0].Dependencies [ vaults.resourceId "testfuncvault" ] "Incorrect secret dependencies"

        Expect.equal secrets.[1].Name.Value "testfuncvault/storage" "Incorrect secret name"
        Expect.equal secrets.[1].Value (ExpressionSecret sa.Key) "Incorrect secret value"
        Expect.sequenceEqual secrets.[1].Dependencies [ vaults.resourceId "testfuncvault"; storageAccounts.resourceId "teststorage" ] "Incorrect secret dependencies"
    }

    test "Supports dotnet-isolated runtime" {
        let f = functions { use_runtime (FunctionsRuntime.DotNetIsolated) }
        let resources = (f :> IBuilder).BuildResources Location.WestEurope
        let site = resources.[3] :?> Web.Site
        Expect.equal site.AppSettings.["FUNCTIONS_WORKER_RUNTIME"] (LiteralSetting "dotnet-isolated") "Should use dotnet-isolated functions runtime"
    }

    test "FunctionsApp supports adding slots" {
        let slot = appSlot { name "warm-up" }
        let site = functions { add_slot slot }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "config should contain slot"

        let slots = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
            |> List.filter (fun s-> s.Type = Arm.Web.slots)

        Expect.hasLength slots 1 "Should only be 1 slot"
    }

    test "Functions App with slot that has system assigned identity adds identity to slot" {
        let slot = appSlot { name "warm-up"; system_identity }
        let site:FunctionsConfig = functions { 
            add_slot slot
        }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "Config should contain slot"

        let slots = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
            |> List.filter (fun s-> s.Type = Arm.Web.slots)
        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"

        let expected = { SystemAssigned = Enabled; UserAssigned = [] }
        Expect.equal (slots.Item 0).Identity expected "Slot should have slot setting"
    }

    test "Functions App with slot adds settings to slot" {
        let slot = appSlot { name "warm-up" }
        let site:FunctionsConfig = functions { 
            add_slot slot 
            setting "setting" "some value"
        }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "Config should contain slot"

        let slots = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
            |> List.filter (fun s-> s.Type = Arm.Web.slots)
        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"

        Expect.isTrue ((slots.Item 0).AppSettings.ContainsKey("setting")) "Slot should have slot setting"
    }

    test "Functions App with slot does not add settings to app service" {
        let slot = appSlot { name "warm-up" }
        let config = functions { 
            add_slot slot 
            setting "setting" "some value"
        }

        let sites = 
            config 
            |> getResources
            |> getResource<Farmer.Arm.Web.Site>
        let slots = sites |> List.filter (fun s-> s.Type = Arm.Web.slots)

        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"
        
        Expect.isFalse (sites.[0].AppSettings.ContainsKey("setting")) "App service should not have any settings"
    }
    
    test "Functions App adds literal settings to slots" {
        let slot = appSlot { name "warm-up" }
        let site:FunctionsConfig = functions { add_slot slot; operating_system Windows }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "Config should contain slot"

        let slots = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
            |> List.filter (fun s-> s.Type = Arm.Web.slots)
        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"

        let settings = (slots.Item 0).AppSettings
        let expectation = [
            "FUNCTIONS_WORKER_RUNTIME"
            "WEBSITE_NODE_DEFAULT_VERSION"
            "FUNCTIONS_EXTENSION_VERSION"
            "AzureWebJobsStorage"
            "AzureWebJobsDashboard"
            "APPINSIGHTS_INSTRUMENTATIONKEY"
            "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING"
            "WEBSITE_CONTENTSHARE"] |> List.map(settings.ContainsKey)
        Expect.allEqual expectation true "Slot should have all literal settings"
    }

    test "Functions App with different settings on slot and service adds both settings to slot" {
        let slot = appSlot { 
            name "warm-up" 
            setting "slot" "slot value"
        }
        let site:FunctionsConfig = functions { 
            add_slot slot 
            setting "appService" "app service value"
        }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "Config should contain slot"

        let slots = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
            |> List.filter (fun s-> s.Type = Arm.Web.slots)
        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"
 
        let settings = (slots.Item 0).AppSettings;
        Expect.isTrue (settings.ContainsKey("slot")) "Slot should have slot setting"
        Expect.isTrue (settings.ContainsKey("appService")) "Slot should have app service setting"
    }
    
    test "Functions App with slot, slot settings override app service setting" {
        let slot = appSlot { 
            name "warm-up" 
            setting "override" "overridden"
        }
        let site:FunctionsConfig = functions { 
            add_slot slot 
            setting "override" "some value"
        }
        Expect.isTrue (site.CommonWebConfig.Slots.ContainsKey "warm-up") "Config should contain slot"

        let sites = 
            site 
            |> getResources
            |> getResource<Arm.Web.Site>
        let slots = sites |> List.filter (fun s-> s.Type = Arm.Web.slots)
        // Default "production" slot is not included as it is created automatically in Azure
        Expect.hasLength slots 1 "Should only be 1 slot"

        let (hasValue, value) = (slots.Item 0).AppSettings.TryGetValue("override");

        Expect.isTrue hasValue "Slot should have app service setting"
        Expect.equal value.Value "overridden" "Slot should have correct app service value"
    }
    
    test "Publish as docker container" {
        let f = functions {
            publish_as (DockerContainer (docker (new Uri("http://www.farmer.io")) "Robert Lewandowski" "do it")) }
        let resources = (f :> IBuilder).BuildResources Location.WestEurope
        let site = resources.[3] :?> Web.Site
        Expect.equal site.AppSettings.["DOCKER_REGISTRY_SERVER_URL"] (LiteralSetting "http://www.farmer.io/") ""
        Expect.equal site.AppSettings.["DOCKER_REGISTRY_SERVER_USERNAME"] (LiteralSetting "Robert Lewandowski") ""
        Expect.equal site.AppSettings.["DOCKER_REGISTRY_SERVER_PASSWORD"] (LiteralSetting "[parameters('Robert Lewandowski-password')]") ""
        Expect.equal site.AppCommandLine (Some "do it") ""
    }

    test "Service plans support Elastic Premium functions" {
        let sp = servicePlan { name "test"; sku WebApp.Sku.EP2; max_elastic_workers 25 }
        let resources = (sp :> IBuilder).BuildResources Location.WestEurope
        let serverFarm = resources.[0] :?> Web.ServerFarm

        Expect.equal serverFarm.Sku (ElasticPremium "EP2") "Incorrect SKU"
        Expect.equal serverFarm.Kind (Some "elastic") "Incorrect Kind"
        Expect.equal serverFarm.MaximumElasticWorkerCount (Some 25) "Incorrect worker count"
    }
]