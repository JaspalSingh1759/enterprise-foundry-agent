@description('The location used for all deployed resources')
param location string = resourceGroup().location

@description('Tags that will be applied to all resources')
param tags object = {}

param mcpTodoExists bool

@description('Id of the user or app to assign application roles')
param principalId string

@description('Principal type of user or app')
param principalType string

@secure()
@description('API key callers must send in the x-api-key header. Set by scripts/pre-provision.sh.')
param mcpApiKey string

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = uniqueString(subscription().id, resourceGroup().id, location)

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var mcpApiKeySecretName = 'mcp-api-key'

// Key Vault holding the MCP API key. RBAC mode: access is granted with role assignments, not access policies.
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${abbrs.keyVaultVaults}${resourceToken}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource mcpApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: mcpApiKeySecretName
  properties: {
    value: mcpApiKey
    contentType: 'API key for the mcp-todo server'
  }
}

// mcp-todo reads the key through a Container Apps Key Vault reference. Scoped to this one secret, not the vault.
resource mcpTodoKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: mcpApiKeySecret
  name: guid(mcpApiKeySecret.id, 'mcp-todo', keyVaultSecretsUserRoleId)
  properties: {
    principalId: mcpTodoIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// The deploying user or pipeline reads the existing key on re-provision, so it isn't rotated every run
resource deployerKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: keyVault
  name: guid(keyVault.id, principalId, keyVaultSecretsOfficerRoleId)
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
  }
}

// Monitor application with Azure Monitor
module monitoring 'br/public:avm/ptn/azd/monitoring:0.1.0' = {
  name: 'monitoring'
  params: {
    logAnalyticsName: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    applicationInsightsName: '${abbrs.insightsComponents}${resourceToken}'
    applicationInsightsDashboardName: '${abbrs.portalDashboards}${resourceToken}'
    location: location
    tags: tags
  }
}

// Container registry
module containerRegistry 'br/public:avm/res/container-registry/registry:0.1.1' = {
  name: 'registry'
  params: {
    name: '${abbrs.containerRegistryRegistries}${resourceToken}'
    location: location
    tags: tags
    publicNetworkAccess: 'Enabled'
    roleAssignments: [
      {
        principalId: mcpTodoIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
      }
    ]
  }
}

// Container apps environment
module containerAppsEnvironment 'br/public:avm/res/app/managed-environment:0.4.5' = {
  name: 'container-apps-environment'
  params: {
    logAnalyticsWorkspaceResourceId: monitoring.outputs.logAnalyticsWorkspaceResourceId
    name: '${abbrs.appManagedEnvironments}${resourceToken}'
    location: location
    zoneRedundant: false
  }
}

// User assigned identity
module mcpTodoIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.2.1' = {
  name: 'mcp-todo-identity'
  params: {
    name: '${abbrs.managedIdentityUserAssignedIdentities}mcp-todo-${resourceToken}'
    location: location
  }
}

// Azure Container Apps
module mcpTodoFetchLatestImage './modules/fetch-container-image.bicep' = {
  name: 'mcp-todo-fetch-image'
  params: {
    exists: mcpTodoExists
    name: 'mcp-todo'
  }
}

module mcpTodo 'br/public:avm/res/app/container-app:0.8.0' = {
  name: 'mcp-todo'
  dependsOn: [
    mcpApiKeySecret
    mcpTodoKeyVaultAccess
  ]
  params: {
    name: 'mcp-todo'
    ingressTargetPort: 8080
    scaleMinReplicas: 1
    scaleMaxReplicas: 10
    secrets: {
      secureList: [
        {
          name: mcpApiKeySecretName
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${mcpApiKeySecretName}'
          identity: mcpTodoIdentity.outputs.resourceId
        }
      ]
    }
    containers: [
      {
        image: mcpTodoFetchLatestImage.outputs.?containers[?0].?image ?? 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
        name: 'main'
        resources: {
          cpu: json('0.5')
          memory: '1.0Gi'
        }
        env: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: monitoring.outputs.applicationInsightsConnectionString
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: mcpTodoIdentity.outputs.clientId
          }
          {
            name: 'PORT'
            value: '8080'
          }
          {
            name: 'Mcp__ApiKey'
            secretRef: mcpApiKeySecretName
          }
        ]
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        mcpTodoIdentity.outputs.resourceId
      ]
    }
    registries: [
      {
        server: containerRegistry.outputs.loginServer
        identity: mcpTodoIdentity.outputs.resourceId
      }
    ]
    environmentResourceId: containerAppsEnvironment.outputs.resourceId
    location: location
    tags: union(tags, { 'azd-service-name': 'mcp-todo' })
  }
}

// Outputs
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.outputs.loginServer
output AZURE_RESOURCE_MCP_TODO_ID string = mcpTodo.outputs.resourceId
output AZURE_RESOURCE_MCP_TODO_NAME string = mcpTodo.outputs.name
output AZURE_RESOURCE_MCP_TODO_FQDN string = mcpTodo.outputs.fqdn
output AZURE_KEY_VAULT_NAME string = keyVault.name
output AZURE_KEY_VAULT_ENDPOINT string = keyVault.properties.vaultUri
