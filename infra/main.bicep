targetScope = 'subscription'

@description('Environment name for tagging')
@minLength(1)
@maxLength(64)
param environmentName string

@description('Primary location for all resources')
param location string

@description('Username of the person deploying the resources, for tagging purposes')
param username string = ''

param agentExists bool = false
param webuiExists bool = false

param foundryProjectEndpoint string = ''
param foundryModel string = ''
param todoAgentName string = ''
param todoAgentVersion string = ''
param mcpTodoFqdn string = ''

#disable-next-line no-unused-params
param principalId string = ''
#disable-next-line no-unused-params
param principalType string = ''

var tags = username == '' ? {
  'azd-env-name': environmentName
} : {
  'azd-env-name': environmentName
  'azd-username': username
}

resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' existing = {
  name: 'rg-${environmentName}'
}

module resources 'resources.bicep' = {
  scope: rg
  name: 'app-tier-resources'
  params: {
    location: location
    tags: tags
    agentExists: agentExists
    webuiExists: webuiExists
    foundryProjectEndpoint: foundryProjectEndpoint
    foundryModel: foundryModel
    todoAgentName: todoAgentName
    todoAgentVersion: todoAgentVersion
    mcpTodoFqdn: mcpTodoFqdn
  }
}

output AZURE_TENANT_ID string = subscription().tenantId
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AGENT_FQDN string = resources.outputs.AGENT_FQDN
output WEBUI_FQDN string = resources.outputs.WEBUI_FQDN
