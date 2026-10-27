targetScope = 'subscription'

@description('Environment name for tagging')
@minLength(1)
@maxLength(64)
param environmentName string

@description('Primary location for all resources')
param location string

@description('Username of the person deploying the resources, for tagging purposes')
param username string = ''

param mcpTodoExists bool

@description('Id of the user or app to assign application roles')
param principalId string

@description('Principal type of user or app')
param principalType string

@secure()
@description('API key for the mcp-todo server. Set by scripts/pre-provision.sh.')
param mcpApiKey string

// Tags that should be applied to all resources.
// 
// Note that 'azd-service-name' tags should be applied separately to service host resources.
// Example usage:
//   tags: union(tags, { 'azd-service-name': <service name in azure.yaml> })
var tags = username == '' ? {
  'azd-env-name': environmentName
} : {
  'azd-env-name': environmentName
  'azd-username': username
}

// Organize resources in a resource group
resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

// Deploy the MCP Todo infrastructure resources
module resources 'resources.bicep' = {
  scope: rg
  name: 'resources'
  params: {
    location: location
    tags: tags
    mcpTodoExists: mcpTodoExists
    principalId: principalId
    principalType: principalType
    mcpApiKey: mcpApiKey
  }
}

// Outputs that azd expects
output AZURE_TENANT_ID string = subscription().tenantId

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_RESOURCE_MCP_TODO_ID string = resources.outputs.AZURE_RESOURCE_MCP_TODO_ID
output AZURE_RESOURCE_MCP_TODO_NAME string = resources.outputs.AZURE_RESOURCE_MCP_TODO_NAME
output AZURE_RESOURCE_MCP_TODO_FQDN string = resources.outputs.AZURE_RESOURCE_MCP_TODO_FQDN
output AZURE_KEY_VAULT_NAME string = resources.outputs.AZURE_KEY_VAULT_NAME
output AZURE_KEY_VAULT_ENDPOINT string = resources.outputs.AZURE_KEY_VAULT_ENDPOINT
