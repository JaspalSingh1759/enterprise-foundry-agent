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

// Reference existing Managed Identity created in Stage 1
resource mcpTodoIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${abbrs.managedIdentityUserAssignedIdentities}mcp-todo-${resourceToken}'
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
            value: mcpTodoIdentity.properties.clientId
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
        mcpTodoIdentity.id
      ]
    }
    registries: [
      {
        server: containerRegistry.properties.loginServer
        identity: mcpTodoIdentity.id
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
        mcpTodoIdentity.id
      ]
    }
    registries: [
      {
        server: containerRegistry.properties.loginServer
        identity: mcpTodoIdentity.id
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
