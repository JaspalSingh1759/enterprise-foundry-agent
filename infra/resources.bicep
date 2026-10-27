@description('The location used for all deployed resources')
param location string = resourceGroup().location

@description('Tags that will be applied to all resources')
param tags object = {}

param agentExists bool = false
param webuiExists bool = false

param foundryProjectEndpoint string = ''
param foundryModel string = ''
param todoAgentName string = ''
param todoAgentVersion string = ''
param mcpTodoFqdn string = ''

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = uniqueString(subscription().id, resourceGroup().id, location)

// Reference existing Container Apps Environment created in Stage 1
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: '${abbrs.appManagedEnvironments}${resourceToken}'
}

// Reference existing Container Registry created in Stage 1
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: '${abbrs.containerRegistryRegistries}${resourceToken}'
}

// Reference existing Log Analytics / App Insights created in Stage 1
resource monitoring 'Microsoft.Insights/components@2020-02-02' existing = {
  name: '${abbrs.insightsComponents}${resourceToken}'
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var azureAIUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

// Each app runs as its own identity, holding only the roles it needs.
resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${abbrs.managedIdentityUserAssignedIdentities}agent-${resourceToken}'
  location: location
  tags: tags
}

resource webuiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${abbrs.managedIdentityUserAssignedIdentities}webui-${resourceToken}'
  location: location
  tags: tags
}

// Both apps pull their images from the shared registry
resource agentAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry
  name: guid(containerRegistry.id, agentIdentity.id, acrPullRoleId)
  properties: {
    principalId: agentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

resource webuiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry
  name: guid(containerRegistry.id, webuiIdentity.id, acrPullRoleId)
  properties: {
    principalId: webuiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

// Only the agent gateway calls Foundry (the project's Responses API, as todo-agent).
// Azure AI User, shown as "Foundry User" in the portal, covers that. The webui never calls Azure.
var foundryAccountName = empty(foundryProjectEndpoint) ? '' : split(split(foundryProjectEndpoint, '//')[1], '.')[0]

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (!empty(foundryAccountName)) {
  name: foundryAccountName
}

resource agentFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryAccountName)) {
  scope: foundryAccount
  name: guid(resourceGroup().id, foundryAccountName, agentIdentity.id, azureAIUserRoleId)
  properties: {
    principalId: agentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)
  }
}

// Fetch existing images if they exist
module agentFetchLatestImage './modules/fetch-container-image.bicep' = {
  name: 'agent-fetch-image'
  params: {
    exists: agentExists
    name: 'agent'
  }
}

module webuiFetchLatestImage './modules/fetch-container-image.bicep' = {
  name: 'webui-fetch-image'
  params: {
    exists: webuiExists
    name: 'webui'
  }
}

// Deploy Agent Gateway Container App
module agentApp 'br/public:avm/res/app/container-app:0.8.0' = {
  name: 'agent'
  dependsOn: [
    agentAcrPull
  ]
  params: {
    name: 'agent'
    ingressTargetPort: 8080
    ingressExternal: true
    scaleMinReplicas: 1
    scaleMaxReplicas: 5
    containers: [
      {
        image: agentFetchLatestImage.outputs.?containers[?0].?image ?? 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
        name: 'main'
        resources: {
          cpu: json('0.5')
          memory: '1.0Gi'
        }
        env: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: monitoring.properties.ConnectionString
          }
          {
            name: 'PORT'
            value: '8080'
          }
          {
            name: 'ASPNETCORE_HTTP_PORTS'
            value: '8080'
          }
          {
            name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
            value: 'true'
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: agentIdentity.properties.clientId
          }
          {
            name: 'AZURE_TENANT_ID'
            value: subscription().tenantId
          }
          {
            name: 'Foundry__Project__Endpoint'
            value: foundryProjectEndpoint
          }
          {
            name: 'Foundry__Project__Model'
            value: foundryModel
          }
          {
            name: 'Foundry__Project__Agent__Name'
            value: todoAgentName
          }
          {
            name: 'Foundry__Project__Agent__Version'
            value: todoAgentVersion
          }
          {
            name: 'AZURE_RESOURCE_MCP_TODO_FQDN'
            value: mcpTodoFqdn
          }
        ]
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        agentIdentity.id
      ]
    }
    registries: [
      {
        server: containerRegistry.properties.loginServer
        identity: agentIdentity.id
      }
    ]
    environmentResourceId: containerAppsEnvironment.id
    location: location
    tags: union(tags, { 'azd-service-name': 'agent' })
  }
}

// Deploy WebUI Container App
module webuiApp 'br/public:avm/res/app/container-app:0.8.0' = {
  name: 'webui'
  dependsOn: [
    webuiAcrPull
  ]
  params: {
    name: 'webui'
    ingressTargetPort: 8080
    ingressExternal: true
    scaleMinReplicas: 1
    scaleMaxReplicas: 5
    containers: [
      {
        image: webuiFetchLatestImage.outputs.?containers[?0].?image ?? 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
        name: 'main'
        resources: {
          cpu: json('0.5')
          memory: '1.0Gi'
        }
        env: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: monitoring.properties.ConnectionString
          }
          {
            name: 'PORT'
            value: '8080'
          }
          {
            name: 'ASPNETCORE_HTTP_PORTS'
            value: '8080'
          }
          {
            name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
            value: 'true'
          }
          {
            name: 'services__agent__http__0'
            value: 'https://${agentApp.outputs.fqdn}'
          }
          {
            name: 'services__agent__https__0'
            value: 'https://${agentApp.outputs.fqdn}'
          }
        ]
      }
    ]
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        webuiIdentity.id
      ]
    }
    registries: [
      {
        server: containerRegistry.properties.loginServer
        identity: webuiIdentity.id
      }
    ]
    environmentResourceId: containerAppsEnvironment.id
    location: location
    tags: union(tags, { 'azd-service-name': 'webui' })
  }
}

output AGENT_FQDN string = agentApp.outputs.fqdn
output WEBUI_FQDN string = webuiApp.outputs.fqdn
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.properties.loginServer
output AZURE_AGENT_IDENTITY_NAME string = agentIdentity.name
output AZURE_WEBUI_IDENTITY_NAME string = webuiIdentity.name
