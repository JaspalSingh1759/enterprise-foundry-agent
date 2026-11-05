// AI gateway (Day 15): API Management (Basic v2) in front of the Foundry project's Responses API, with a token
// limit and token metrics per calling team. A one-day experiment, deployed and deleted by scripts/gateway/*.sh,
// so it's separate from the azd template.
//
// Why Basic v2: llm-token-limit isn't available on the Consumption tier. Basic v2 deploys in minutes and bills
// by the hour, so a day costs a few dollars if it's deleted the same day.

param location string = resourceGroup().location
param resourceToken string

@description('Client ID of disputes-api\'s managed identity: the "disputes" team')
param disputesCallerId string

@description('The Foundry project endpoint, e.g. https://<account>.services.ai.azure.com/api/projects/<project>')
param foundryProjectEndpoint string

param publisherEmail string = 'platform-team@maple-bank.example'

var azureAIUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var foundryAccountName = split(split(foundryProjectEndpoint, '//')[1], '.')[0]
var foundryProjectName = last(split(foundryProjectEndpoint, '/'))

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName

  resource project 'projects' existing = {
    name: foundryProjectName
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: 'appi-${resourceToken}'
}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: 'apim-${resourceToken}'
  location: location
  tags: { purpose: 'day-15-ai-gateway', 'delete-after': 'same day' }
  sku: { name: 'BasicV2', capacity: 1 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: 'Maple Bank platform team'
  }
}

// The gateway calls Foundry as itself. In production, the callers would LOSE their own Foundry role, so the gateway
// is the only way in (plus private networking); here disputes-api keeps its role so switching back is instant.
resource gatewayFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundryAccount::project
  name: guid(foundryAccount::project.id, apim.id, azureAIUser)
  properties: {
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUser)
  }
}

resource tenantIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'tenant-id'
  properties: { displayName: 'tenant-id', value: tenant().tenantId }
}

resource disputesCallerValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'disputes-caller-id'
  properties: { displayName: 'disputes-caller-id', value: disputesCallerId }
}

resource backend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'foundry-project'
  properties: {
    url: foundryProjectEndpoint
    protocol: 'http'
    description: 'Foundry project (Responses API with agent_reference)'
  }
}

// Token metrics and request telemetry go to the same App Insights as everything else (Day 12).
resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'appinsights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsights.id
    credentials: { connectionString: appInsights.properties.ConnectionString }
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'foundry'
  properties: {
    displayName: 'Foundry Responses API'
    path: 'foundry'
    protocols: ['https']
    subscriptionRequired: false // callers authenticate with Entra tokens, validated in the policy
  }

  resource responses 'operations' = {
    name: 'create-response'
    properties: {
      displayName: 'Create response (run an agent turn)'
      method: 'POST'
      urlTemplate: '/openai/v1/responses'
    }
  }

  resource policy 'policies' = {
    name: 'policy'
    properties: {
      format: 'rawxml'
      value: loadTextContent('foundry-policy.xml')
    }
    dependsOn: [tenantIdValue, disputesCallerValue, backend]
  }

  resource diagnostics 'diagnostics' = {
    name: 'applicationinsights'
    properties: {
      loggerId: logger.id
      alwaysLog: 'allErrors'
      sampling: { samplingType: 'fixed', percentage: 100 }
      metrics: true // required for llm-emit-token-metric
      logClientIp: false
      verbosity: 'information'
      // No request or response bodies: they hold claim text and customer data.
    }
  }
}

output gatewayUrl string = '${apim.properties.gatewayUrl}/foundry'
output apimName string = apim.name
