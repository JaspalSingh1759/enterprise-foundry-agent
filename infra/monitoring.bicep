// Observability as code (Day 12): an action group, the alerts for the dispute platform, and an operations
// workbook. Everything reads ONE Log Analytics workspace: App Insights (our three apps and Foundry's agent traces)
// and the Container Apps logs both land there, so one query can follow a case across all of them.
//
// Two kinds of alert:
// - Log alerts (scheduled KQL) for signals only our telemetry knows: tool loops, tool failures, investigation
//   latency and outcome, policy search no-hits, override rate. They fire when the query returns a row.
// - Metric alerts on the Foundry account for platform signals Azure already counts: 429s (quota), 401/403
//   (identity broken) and token spend. Cheaper and faster than log alerts (about one minute).
//
// Thresholds come from the real numbers in this environment (Day 8 evals and Day 9 runs): an agent turn makes at
// most 7 tool calls (p95 5), an investigation takes about 50 s, and the busiest eval hour used about 1M tokens.

param location string = resourceGroup().location
param tags object = {}
param resourceToken string
param abbrs object

@description('Who gets the alert emails. Empty = alerts still fire and show in the portal, but notify nobody')
param alertEmail string = ''

@description('Hourly token budget across all model deployments; above it, the spend alert fires')
param tokensPerHourAlert int = 2000000

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: 'ai-account-${resourceToken}'
}

// ── Who gets told ──────────────────────────────────────────────────────────────────────────────────────────
// In production: the on-call rotation (PagerDuty / Opsgenie webhook) for Sev 0-1, a team channel for Sev 2-3.
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${abbrs.insightsActionGroups}disputes-${resourceToken}'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'disputes'
    enabled: true
    emailReceivers: empty(alertEmail) ? [] : [
      { name: 'on-call', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

// ── Log alerts ─────────────────────────────────────────────────────────────────────────────────────────────
// Severity: 1 = users affected now, 2 = degraded / needs a look today, 3 = trend to review.
var logAlerts = [
  {
    name: 'agent-tool-loop'
    severity: 2
    description: 'An agent turn made more than 15 tool calls (normal: at most 7). Usually a prompt or tool change that makes the model call tools in circles: cost and latency spike, and the turn may never finish.'
    frequency: 'PT5M'
    window: 'PT15M'
    query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and Name startswith 'execute_tool'
| extend Run = tostring(Properties['gen_ai.response.id']), Agent = tostring(Properties['gen_ai.agent.name'])
| summarize ToolCalls = count() by Run, Agent
| where ToolCalls > 15
'''
  }
  {
    name: 'tool-failure-rate'
    severity: 2
    description: 'More than 5% of the agents\' tool calls failed in 15 minutes. mcp-banking down, its API key out of sync with the Foundry connection, or a broken deployment.'
    frequency: 'PT5M'
    window: 'PT15M'
    query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and Name startswith 'execute_tool'
| summarize Calls = count(), Failed = countif(Success == false)
| where Calls >= 10 and Failed * 100.0 / Calls > 5
'''
  }
  {
    name: 'investigation-latency-p95'
    severity: 3
    description: 'p95 investigation time over 2 minutes in the last hour (normal: about 50 s). Model latency, 429 retries, or more tool calls per turn.'
    frequency: 'PT15M'
    window: 'PT1H'
    query: '''
AppTraces
| where AppRoleName == 'disputes-api' and Message has 'investigation finished'
| summarize Investigations = count(), P95Seconds = percentile(todouble(Properties.Seconds), 95)
| where Investigations >= 3 and P95Seconds > 120
'''
  }
  {
    name: 'manual-handling-rate'
    severity: 2
    description: 'More than 20% of investigations in the last hour ended in manual handling (an agent failed the code checks twice, or a step failed). A bad agent version, a broken tool, or a quota problem.'
    frequency: 'PT15M'
    window: 'PT1H'
    query: '''
AppTraces
| where AppRoleName == 'disputes-api' and Message has 'investigation finished'
| summarize Investigations = count(), Manual = countif(tostring(Properties.Status) == 'NeedsManualHandling')
| where Investigations >= 3 and Manual * 100.0 / Investigations > 20
'''
  }
  {
    name: 'policy-search-problems'
    severity: 2
    description: 'search_policy failed or returned no passages. The policy agent can\'t cite a section, so cases go to manual handling. AI Search down, the index or alias changed, or the role assignment removed.'
    frequency: 'PT5M'
    window: 'PT15M'
    query: '''
AppTraces
| where AppRoleName == 'mcp-banking'
| where (Message has 'policy searched' and toint(Properties.Count) == 0) or Message has 'policy search failed'
| summarize Problems = count()
| where Problems > 0
'''
  }
  {
    name: 'server-errors'
    severity: 1
    description: 'Three or more HTTP 5xx answers from one of our apps in 15 minutes.'
    frequency: 'PT5M'
    window: 'PT15M'
    query: '''
AppRequests
| where AppRoleName in ('disputes-api', 'mcp-banking', 'webui') and toint(ResultCode) >= 500
| summarize Errors = count() by AppRoleName
| where Errors >= 3
'''
  }
  {
    name: 'override-rate'
    severity: 3
    description: 'Analysts overruled more than 30% of the agents\' recommendations in the last day. Quality is drifting: review the overridden cases and add them to the eval set.'
    frequency: 'PT1H'
    window: 'P1D'
    query: '''
AppTraces
| where AppRoleName == 'disputes-api' and Message has 'decided'
| extend Decision = tostring(Properties.Decision), DraftAction = tostring(Properties.DraftAction)
| summarize Decisions = count(), Overrides = countif(Decision == 'rejected' or (Decision == 'info_requested' and DraftAction != 'request_info'))
| where Decisions >= 10 and Overrides * 100.0 / Decisions > 30
'''
  }
]

resource logAlertRules 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for alert in logAlerts: {
  name: 'alert-${alert.name}-${resourceToken}'
  location: location
  tags: tags
  properties: {
    displayName: 'disputes: ${alert.name}'
    description: alert.description
    severity: alert.severity
    enabled: true
    scopes: [workspace.id]
    evaluationFrequency: alert.frequency
    windowSize: alert.window
    criteria: {
      allOf: [
        {
          query: alert.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [actionGroup.id] }
  }
}]

// ── Metric alerts on the Foundry account ───────────────────────────────────────────────────────────────────
var metricAlerts = [
  {
    name: 'foundry-throttling'
    severity: 2
    description: 'More than 5 model requests got HTTP 429 in 5 minutes: the deployment\'s tokens-per-minute quota is too small for the load. disputes-api retries, so cases slow down before they fail.'
    metric: 'ModelRequests'
    statusCodes: ['429']
    threshold: 5
    window: 'PT5M'
  }
  {
    name: 'foundry-auth-failures'
    severity: 1
    description: 'Model requests rejected with 401/403: an identity or role assignment is broken. Every investigation will fail.'
    metric: 'ModelRequests'
    statusCodes: ['401', '403']
    threshold: 0
    window: 'PT5M'
  }
  {
    name: 'token-spend'
    severity: 3
    description: 'Token use above the hourly budget. A loop, a traffic spike, or someone running evals against this environment.'
    metric: 'TotalTokens'
    statusCodes: []
    threshold: tokensPerHourAlert
    window: 'PT1H'
  }
]

resource metricAlertRules 'Microsoft.Insights/metricAlerts@2018-03-01' = [for alert in metricAlerts: {
  name: 'alert-${alert.name}-${resourceToken}'
  location: 'global'
  tags: tags
  properties: {
    description: alert.description
    severity: alert.severity
    enabled: true
    scopes: [foundry.id]
    evaluationFrequency: alert.window == 'PT1H' ? 'PT15M' : 'PT1M'
    windowSize: alert.window
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: alert.metric
          metricName: alert.metric
          metricNamespace: 'Microsoft.CognitiveServices/accounts'
          dimensions: empty(alert.statusCodes) ? [] : [
            { name: 'StatusCode', operator: 'Include', values: alert.statusCodes }
          ]
          operator: 'GreaterThan'
          threshold: alert.threshold
          timeAggregation: 'Total'
        }
      ]
    }
    autoMitigate: true
    actions: [{ actionGroupId: actionGroup.id }]
  }
}]

// ── Operations workbook ────────────────────────────────────────────────────────────────────────────────────
// One page an on-call engineer opens first: is the SLO healthy, which agent is slow or expensive, which tool is
// failing. Model prices match tools/agent-evals/run_dispute_evals.py (USD per 1M tokens).
var tile = {
  version: 'KqlItem/1.0'
  size: 1
  queryType: 0
  resourceType: 'microsoft.operationalinsights/workspaces'
  timeContextFromParameter: 'TimeRange'
  timeContext: { durationMs: 86400000 }
}

var workbook = {
  version: 'Notebook/1.0'
  fallbackResourceIds: [workspace.id]
  items: [
    {
      type: 1
      name: 'title'
      content: { json: '# Maple Bank disputes: operations\nSLO: **99% of investigations return a draft within 2 minutes** (28 days). Alerts: Azure Monitor > Alerts, filter "disputes:".' }
    }
    {
      type: 9
      name: 'parameters'
      content: {
        version: 'KqlParameterItem/1.0'
        parameters: [
          {
            id: 'time-range'
            version: 'KqlParameterItem/1.0'
            name: 'TimeRange'
            type: 4
            isRequired: true
            value: { durationMs: 86400000 }
            typeSettings: {
              selectableValues: [{ durationMs: 3600000 }, { durationMs: 86400000 }, { durationMs: 604800000 }, { durationMs: 2419200000 }]
            }
          }
        ]
      }
    }
    {
      type: 3
      name: 'slo'
      customWidth: '50'
      content: union(tile, {
        title: 'SLO: investigations with a draft within 2 minutes'
        visualization: 'tiles'
        query: '''
AppTraces
| where AppRoleName == 'disputes-api' and Message has 'investigation finished'
| extend Good = tostring(Properties.Status) == 'AwaitingApproval' and todouble(Properties.Seconds) <= 120
| summarize Investigations = count(), Good = countif(Good)
| extend SLI = round(100.0 * Good / Investigations, 2), Target = 99.0
| extend BudgetUsed = strcat(round(100.0 * (Investigations - Good) / (Investigations * 0.01), 0), '%')
| project SLI, Target, Investigations, BudgetUsed
'''
        tileSettings: {
          showBorder: true
          titleContent: { columnMatch: 'SLI', formatter: 12, formatOptions: { palette: 'auto' }, numberFormat: { unit: 1 } }
          leftContent: { columnMatch: 'Investigations', formatter: 12 }
          secondaryContent: { columnMatch: 'BudgetUsed', formatter: 1 }
        }
      })
    }
    {
      type: 3
      name: 'outcomes'
      customWidth: '50'
      content: union(tile, {
        title: 'Investigation outcomes'
        visualization: 'barchart'
        query: '''
AppTraces
| where AppRoleName == 'disputes-api' and Message has 'investigation finished'
| summarize count() by Status = tostring(Properties.Status), bin(TimeGenerated, 1h)
'''
      })
    }
    {
      type: 3
      name: 'agent-latency'
      customWidth: '50'
      content: union(tile, {
        title: 'Agent turn latency (seconds)'
        visualization: 'table'
        query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and Name startswith 'invoke_agent'
| extend Agent = tostring(Properties['gen_ai.agent.name']), Version = tostring(Properties['gen_ai.agent.version'])
| summarize Turns = count(), Failed = countif(Success == false),
            P50 = round(percentile(DurationMs, 50) / 1000, 1), P95 = round(percentile(DurationMs, 95) / 1000, 1)
            by Agent, Version
| order by Agent asc, Version desc
'''
      })
    }
    {
      type: 3
      name: 'cost'
      customWidth: '50'
      content: union(tile, {
        title: 'Tokens and estimated cost per agent (USD)'
        visualization: 'table'
        query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and Name startswith 'chat '
| extend Agent = tostring(Properties['gen_ai.agent.name']), Model = tostring(Properties['gen_ai.response.model']),
         In = tolong(Properties['gen_ai.usage.input_tokens']), Out = tolong(Properties['gen_ai.usage.output_tokens'])
| extend Usd = iff(Model startswith 'gpt-5-nano', In * 0.05 + Out * 0.40, In * 0.25 + Out * 2.00) / 1e6
| summarize ModelCalls = count(), InputTokens = sum(In), OutputTokens = sum(Out), Usd = round(sum(Usd), 4) by Agent, Model
| order by Usd desc
'''
      })
    }
    {
      type: 3
      name: 'tools'
      customWidth: '50'
      content: union(tile, {
        title: 'Tool calls'
        visualization: 'table'
        query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and Name startswith 'execute_tool'
| extend Tool = tostring(Properties['gen_ai.tool.name'])
| summarize Calls = count(), Failed = countif(Success == false), P95Ms = round(percentile(DurationMs, 95), 0) by Tool
| order by Calls desc
'''
      })
    }
    {
      type: 3
      name: 'model-errors'
      customWidth: '50'
      content: union(tile, {
        title: 'Model call errors (429 = quota)'
        visualization: 'barchart'
        query: '''
AppDependencies
| where AppRoleName == 'responsesapi' and (Name startswith 'chat ' or Name startswith 'invoke_agent') and Success == false
| summarize count() by Error = tostring(Properties['error.type']), bin(TimeGenerated, 1h)
'''
      })
    }
    {
      type: 3
      name: 'quality'
      customWidth: '50'
      content: union(tile, {
        title: 'Quality signals'
        visualization: 'table'
        query: '''
let decisions = AppTraces
    | where AppRoleName == 'disputes-api' and Message has 'decided'
    | extend Decision = tostring(Properties.Decision), DraftAction = tostring(Properties.DraftAction)
    | summarize Value = round(100.0 * countif(Decision == 'rejected' or (Decision == 'info_requested' and DraftAction != 'request_info')) / count(), 1)
    | extend Signal = 'Override rate (%)';
let manual = AppTraces
    | where AppRoleName == 'disputes-api' and Message has 'investigation finished'
    | summarize Value = round(100.0 * countif(tostring(Properties.Status) == 'NeedsManualHandling') / count(), 1)
    | extend Signal = 'Manual handling (%)';
let searches = AppTraces
    | where AppRoleName == 'mcp-banking' and Message has 'policy searched'
    | summarize Value = round(100.0 * countif(toint(Properties.Count) == 0) / count(), 1)
    | extend Signal = 'Policy search no-hits (%)';
union decisions, manual, searches
| project Signal, Value
'''
      })
    }
    {
      type: 3
      name: 'errors'
      customWidth: '50'
      content: union(tile, {
        title: 'HTTP errors by app'
        visualization: 'barchart'
        query: '''
AppRequests
| where AppRoleName in ('disputes-api', 'mcp-banking', 'webui') and toint(ResultCode) >= 400
| where not(AppRoleName == 'mcp-banking' and Name == 'GET /mcp') // MCP clients probe for a stream: 405 is normal
| summarize count() by AppRoleName, ResultCode
'''
      })
    }
  ]
}

resource operationsWorkbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'disputes-operations-workbook')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Maple Bank disputes: operations'
    category: 'workbook'
    sourceId: workspace.id
    serializedData: string(workbook)
  }
}

output actionGroupId string = actionGroup.id
output workbookId string = operationsWorkbook.id
