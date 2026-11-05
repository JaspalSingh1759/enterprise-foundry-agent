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

param disputesApiExists bool = false
@description('Client ID of the disputes-api Entra app registration (scripts/entra/create-disputes-app.sh)')
param disputesApiClientId string = ''
param mcpBankingPrincipalId string = ''
@description('Client ID of the dispute console Entra app registration (scripts/entra/create-disputes-console-app.sh)')
param disputesConsoleClientId string = ''

param principalId string = ''
param principalType string = ''

@description('Who gets the alert emails (Day 12)')
param alertEmail string = ''

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
    disputesApiExists: disputesApiExists
    disputesApiClientId: disputesApiClientId
    disputesConsoleClientId: disputesConsoleClientId
    mcpBankingPrincipalId: mcpBankingPrincipalId
    principalId: principalId
    principalType: principalType
    alertEmail: alertEmail
  }
}

output AZURE_TENANT_ID string = subscription().tenantId
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AGENT_FQDN string = resources.outputs.AGENT_FQDN
output WEBUI_FQDN string = resources.outputs.WEBUI_FQDN
output DISPUTES_API_FQDN string = resources.outputs.DISPUTES_API_FQDN
