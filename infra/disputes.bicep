// disputes-api: Maple Bank's dispute case system (fictional bank, synthetic data), Day 9.
// Its storage lives in the Foundry tier's storage account (Day 7), so no new account: two tables, three
// containers, and an immutable (WORM) audit container. Every identity gets the narrowest role on the narrowest
// scope: a table or a container, never the whole account.

@description('The location used for all deployed resources')
param location string

@description('Tags that will be applied to all resources')
param tags object

param resourceToken string
param abbrs object
param exists bool

param containerAppsEnvironmentId string
param containerRegistryName string
param applicationInsightsConnectionString string

@description('Foundry project endpoint (…/api/projects/<project>); disputes-api runs the dispute agents there')
param foundryProjectEndpoint string

@description('Client ID of the Entra app registration that protects the API (scripts/entra/create-disputes-app.sh). Empty = internal ingress only')
param apiClientId string

@description('Identity of mcp-banking: reads the cases table so agents see new claims')
param mcpBankingPrincipalId string

@description('The developer: reads and writes cases, reads letters and the audit trail (debugging, end-to-end tests)')
param principalId string
param principalType string

var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  azureAIUser: '53ca6127-db72-4b80-b1b0-d745d6d5456d'
  tableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  tableDataReader: '76199698-9eea-4c19-bc75-cec21354c6b6'
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  blobDataReader: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
}

// ── Identity ────────────────────────────────────────────────────────────────────────────────────────────────

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${abbrs.managedIdentityUserAssignedIdentities}disputes-api-${resourceToken}'
  location: location
  tags: tags
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry
  name: guid(containerRegistry.id, identity.id, roles.acrPull)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

// Runs the three prompt agents through the project's Responses API: Azure AI User on the project only.
var foundryAccountName = split(split(foundryProjectEndpoint, '//')[1], '.')[0]
var foundryProjectName = last(split(foundryProjectEndpoint, '/'))

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName

  resource project 'projects' existing = {
    name: foundryProjectName
  }
}

resource foundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundryAccount::project
  name: guid(foundryAccount::project.id, identity.id, roles.azureAIUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.azureAIUser)
  }
}

// ── Storage: tables and containers in the existing account ──────────────────────────────────────────────────

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: '${abbrs.storageStorageAccounts}${resourceToken}'

  resource tableService 'tableServices' existing = {
    name: 'default'

    resource cases 'tables' = {
      name: 'cases'
    }

    resource ledger 'tables' = {
      name: 'ledger'
    }
  }

  resource blobService 'blobServices' existing = {
    name: 'default'

    resource audit 'containers' = {
      name: 'audit'
      properties: { publicAccess: 'None' }

      // WORM: for 1 day after a blob is created, nobody (admins included) can change or delete it; new lines can
      // still be appended. Created UNLOCKED: the policy itself can be removed, so the resource group stays
      // deletable in dev. Production locks it (az storage container immutability-policy lock), which is
      // irreversible, and uses the retention period compliance requires (often 7 years).
      resource worm 'immutabilityPolicies' = {
        name: 'default'
        properties: {
          immutabilityPeriodSinceCreationInDays: 1
          allowProtectedAppendWritesAll: true
        }
      }
    }

    resource letters 'containers' = {
      name: 'letters'
      properties: { publicAccess: 'None' }
    }

    resource attachments 'containers' = {
      name: 'attachments'
      properties: { publicAccess: 'None' }
    }
  }
}

// disputes-api: read/write cases and the ledger; write audit lines, letters and attachments.
resource apiCases 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage::tableService::cases
  name: guid(storage::tableService::cases.id, identity.id, roles.tableDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableDataContributor)
  }
}

resource apiLedger 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage::tableService::ledger
  name: guid(storage::tableService::ledger.id, identity.id, roles.tableDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableDataContributor)
  }
}

resource apiAudit 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage::blobService::audit
  name: guid(storage::blobService::audit.id, identity.id, roles.blobDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
  }
}

resource apiLetters 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage::blobService::letters
  name: guid(storage::blobService::letters.id, identity.id, roles.blobDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
  }
}

resource apiAttachments 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage::blobService::attachments
  name: guid(storage::blobService::attachments.id, identity.id, roles.blobDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
  }
}

// mcp-banking: read cases only, so the agents' get_case sees new claims. It can't change a case.
resource mcpBankingCases 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(mcpBankingPrincipalId)) {
  scope: storage::tableService::cases
  name: guid(storage::tableService::cases.id, mcpBankingPrincipalId, roles.tableDataReader)
  properties: {
    principalId: mcpBankingPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableDataReader)
  }
}

// The developer (or the pipeline, when it deploys): run the end-to-end tests and debug. Reads letters and the
// audit trail; can't write them.
resource devCases 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storage::tableService::cases
  name: guid(storage::tableService::cases.id, principalId, roles.tableDataContributor)
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableDataContributor)
  }
}

resource devLedger 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storage::tableService::ledger
  name: guid(storage::tableService::ledger.id, principalId, roles.tableDataReader)
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableDataReader)
  }
}

resource devAudit 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storage::blobService::audit
  name: guid(storage::blobService::audit.id, principalId, roles.blobDataReader)
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataReader)
  }
}

resource devLetters 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: storage::blobService::letters
  name: guid(storage::blobService::letters.id, principalId, roles.blobDataReader)
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataReader)
  }
}

// ── The Container App ───────────────────────────────────────────────────────────────────────────────────────

module fetchLatestImage './modules/fetch-container-image.bicep' = {
  name: 'disputes-api-fetch-image'
  params: {
    exists: exists
    name: 'disputes-api'
  }
}

// Public only with sign-in configured: without an app registration the API is reachable only inside the
// Container Apps environment, never on the internet without authentication.
var protectedByEntra = !empty(apiClientId)

module app 'br/public:avm/res/app/container-app:0.8.0' = {
  name: 'disputes-api'
  dependsOn: [
    acrPull
  ]
  params: {
    name: 'disputes-api'
    ingressTargetPort: 8080
    ingressExternal: protectedByEntra
    scaleMinReplicas: 0
    scaleMaxReplicas: 2
    containers: [
      {
        image: fetchLatestImage.outputs.?containers[?0].?image ?? 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
        name: 'main'
        resources: {
          cpu: json('0.5')
          memory: '1.0Gi'
        }
        env: [
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
          { name: 'OTEL_SERVICE_NAME', value: 'disputes-api' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
          { name: 'Storage__AccountName', value: storage.name }
        ]
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        identity.id
      ]
    }
    registries: [
      {
        server: containerRegistry.properties.loginServer
        identity: identity.id
      }
    ]
    environmentResourceId: containerAppsEnvironmentId
    location: location
    tags: union(tags, { 'azd-service-name': 'disputes-api' })
  }
}

resource deployedApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: 'disputes-api'
  dependsOn: [
    app
  ]
}

// Built-in authentication (Easy Auth): the platform validates the Entra token before a request reaches the app,
// returns 401 without one, and passes the signed-in user and app roles in X-MS-CLIENT-PRINCIPAL. It strips any
// X-MS-CLIENT-PRINCIPAL a caller sends, which is why the app can trust that header. No login code in the app.
resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (protectedByEntra) {
  parent: deployedApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: apiClientId
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${apiClientId}'
            apiClientId
          ]
        }
      }
    }
  }
}

output fqdn string = app.outputs.fqdn
output identityPrincipalId string = identity.properties.principalId
