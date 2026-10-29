# Tagging Test Traffic in Production: AI DevOps Implementation Guide

A complete technical guide on how automated test, evaluation, and synthetic traffic is tagged in production to prevent false alerts, protect metrics, and gate CI/CD deployments.

---

## 1. Why Test Traffic Must Be Tagged

In production AI systems, running tests against production endpoints (synthetic monitoring, post-deployment smoke tests, canary checks, or automated evaluations) is necessary to ensure the system is working.

However, **test traffic must be tagged end-to-end** so that:
1. **Production alerts ignore it:** Automated eval test cases (like case #7 deliberately triggering bulk deletions) don't page on-call engineers at 2:00 AM.
2. **CI/CD pipelines can isolate it:** Post-deployment gates can query telemetry for *only that specific test run* to decide whether to promote or roll back.
3. **Business analytics and cost metrics remain clean:** Test scripts (e.g. database seed scripts deleting all records) don't skew real user usage statistics or customer SLOs.

---

## 2. End-to-End Architecture

```
[ CI/CD Pipeline / Synthetic Test ]
        │  Sends: Header "x-test-run: ci-10452"
        │     OR: Header "baggage: test.run_id=ci-10452,synthetic=true"
        ▼
[ API Gateway / Agent / Server Middleware ]
        │  Extracts header ➔ Injects into OpenTelemetry Span / Activity:
        │  Activity.Current?.SetTag("test.run_id", "ci-10452")
        ▼
[ Downstream Services (MCP Server, Tools, DB) ]
        │  Propagates via W3C Trace Context & Baggage
        ▼
[ Application Insights / Log Analytics ]
        │  Stored as: customDimensions["test.run_id"] = "ci-10452"
        ▼
┌───────────────────────────────┴───────────────────────────────┐
│                                                               │
▼                                                               ▼
[ Production Alert Rules ]                     [ CI/CD Pipeline Check ]
Filter: isempty(customDimensions["test.run_id"]) Filter: customDimensions["test.run_id"] == "ci-10452"
Result: Ignores test traffic; NO false alarms!  Result: Passes/Fails deployment gate!
```

---

## 3. Emitting Tags from Tests & CI/CD

There are two primary ways to tag outgoing test requests:
1. **Custom HTTP Header** (`x-test-run`): Simple, supported everywhere.
2. **W3C Baggage** (`baggage`): The OpenTelemetry standard, which automatically propagates across distributed microservices.

### A. Bash / cURL Smoke Test Script (`smoke-test.sh`)

```bash
#!/usr/bin/env bash
set -euo pipefail

# Generate or pass a unique Run ID from CI
RUN_ID="${GITHUB_RUN_ID:-manual-$(date +%s)}"
API_URL="https://agent.mycompany.com/api/chat"

echo "Running post-deploy smoke test with Run ID: $RUN_ID"

# Pass both custom header and W3C Baggage
curl -X POST "$API_URL" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TEST_USER_TOKEN" \
  -H "x-test-run: $RUN_ID" \
  -H "baggage: test.run_id=$RUN_ID,synthetic=true" \
  -d '{
    "message": "List my high priority tasks"
  }'
```

---

### B. Python / Pytest / Evaluation Script

```python
import os
import requests
import uuid

# Unique test run ID from GitHub Actions or local run
RUN_ID = os.getenv("GITHUB_RUN_ID", f"local-{uuid.uuid4().hex[:8]}")

headers = {
    "Content-Type": "application/json",
    "x-test-run": RUN_ID,
    # Standard W3C baggage format
    "baggage": f"test.run_id={RUN_ID},synthetic=true",
    # Standard App Insights synthetic user-agent
    "User-Agent": f"SyntheticMonitor/1.0 (RunId={RUN_ID})"
}

response = requests.post(
    "https://agent.mycompany.com/api/chat",
    headers=headers,
    json={"message": "Clean up completed items"}
)
assert response.status_code == 200
```

---

### C. .NET / C# HttpClient

```csharp
var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? $"test-{Guid.NewGuid():N}";

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("x-test-run", runId);
client.DefaultRequestHeaders.Add("baggage", $"test.run_id={runId},synthetic=true");

var response = await client.PostAsJsonAsync("https://agent.mycompany.com/api/chat", new 
{
    message = "List my todos"
});
```

---

## 4. Extracting & Stamping the Tag on the Server (.NET / C#)

When the request hits your ASP.NET Core service or MCP Server, middleware extracts the header and attaches it to the active **OpenTelemetry Activity (Span)**.

In `Program.cs`:

```csharp
var app = builder.Build();

// Custom Middleware: Tag incoming test traffic onto OpenTelemetry spans
app.Use(async (context, next) =>
{
    // 1. Check for custom header or W3C baggage
    string? testRunId = context.Request.Headers["x-test-run"].FirstOrDefault();
    
    if (string.IsNullOrEmpty(testRunId))
    {
        // Fallback: parse baggage header if present
        var baggage = context.Request.Headers["baggage"].FirstOrDefault();
        if (!string.IsNullOrEmpty(baggage) && baggage.Contains("test.run_id="))
        {
            testRunId = baggage.Split(',')
                               .FirstOrDefault(b => b.Trim().StartsWith("test.run_id="))
                               ?.Split('=')[1];
        }
    }

    // 2. If present, stamp it onto the current OpenTelemetry Activity
    if (!string.IsNullOrEmpty(testRunId))
    {
        var currentActivity = System.Diagnostics.Activity.Current;
        if (currentActivity != null)
        {
            // This lands directly in App Insights customDimensions["test.run_id"]
            currentActivity.SetTag("test.run_id", testRunId);
            currentActivity.SetTag("app.synthetic", "true");
        }
    }

    await next();
});
```

---

## 5. Production KQL Alert Queries (Excluding Test Traffic)

In [docs/kql/agent-quality.kql](../kql/agent-quality.kql), production alerts filter out test runs using `isempty(tostring(customDimensions["test.run_id"]))`:

```kusto
// Production Alert: Bulk Destructive Turns (excludes test runs)
let window = 15m;
let agentOps = requests
    | where timestamp > ago(window) and cloud_RoleName == "mcp-todo" and name startswith "POST /mcp"
    | where operation_ParentId != operation_Id
    // CRITICAL: Filter out any test or synthetic runs
    | where isempty(tostring(customDimensions["test.run_id"]))
    | distinct operation_Id;

requests
| where timestamp > ago(window) and cloud_RoleName == "mcp-todo" and name startswith "tools/call"
| where operation_Id in (agentOps)
| extend tool = tostring(customDimensions["gen_ai.tool.name"])
| summarize deletes = countif(tool == "delete_todo_item"),
            updates = countif(tool == "update_todo_item")
            by operation_Id
| where deletes > 2 or updates > 2
```

---

## 6. CI/CD Deployment Gate (GitHub Actions)

In GitHub Actions or Azure DevOps, this step runs after deployment:
1. Executes synthetic tests tagged with `$GITHUB_RUN_ID`.
2. Waits 90s for telemetry ingestion buffer.
3. Queries Application Insights targeting **only that `$GITHUB_RUN_ID`**.
4. Fails the release pipeline if spans did not arrive or if errors/loops occurred.

```yaml
name: Deploy and Verify Quality Signals

on:
  push:
    branches: [ main ]

jobs:
  deploy-and-verify:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout Code
        uses: actions/checkout@v4

      - name: Azure Login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      # 1. Deploy the Application
      - name: Deploy App to Azure
        run: |
          echo "Deploying application..."
          # az containerapp update / dotnet publish / func deploy

      # 2. Run Synthetic Smoke Tests (Tagged with Run ID)
      - name: Execute Tagged Test Suite
        env:
          RUN_ID: ${{ github.run_id }}
          AGENT_ENDPOINT: "https://agent-dev.azurewebsites.net"
        run: |
          echo "Executing test suite with GITHUB_RUN_ID: $RUN_ID"
          
          # Run tests sending the x-test-run header
          curl -s -f -X POST "$AGENT_ENDPOINT/chat" \
            -H "Content-Type: application/json" \
            -H "x-test-run: $RUN_ID" \
            -d '{"prompt": "Show my completed tasks and delete item 12"}' || true

      # 3. Wait for Application Insights Ingestion (typically 1-3 minutes)
      - name: Verify Telemetry Ingestion & Quality Signals
        env:
          APP_INSIGHTS_NAME: "appi-6a3q2sputpvay"
          RESOURCE_GROUP: "rg-dev"
          RUN_ID: ${{ github.run_id }}
        run: |
          echo "Waiting 90s for telemetry buffer..."
          sleep 90

          echo "Checking telemetry for run $RUN_ID..."

          # Check A: Verify that spans actually arrived (Heartbeat)
          COUNT=$(az monitor app-insights query \
            --app "$APP_INSIGHTS_NAME" \
            -g "$RESOURCE_GROUP" \
            --offset 15m \
            --analytics-query "requests | where customDimensions['test.run_id'] == '$RUN_ID' | count" \
            --query "tables[0].rows[0][0]" -o tsv)

          echo "Spans received for this run: $COUNT"
          if [ "$COUNT" -eq 0 ]; then
            echo "::error::CRITICAL: Zero telemetry spans arrived for test run $RUN_ID. Telemetry is broken!"
            exit 1
          fi

          # Check B: Verify Quality Signal (e.g. No tool failures in this test run)
          FAILURES=$(az monitor app-insights query \
            --app "$APP_INSIGHTS_NAME" \
            -g "$RESOURCE_GROUP" \
            --offset 15m \
            --analytics-query "requests | where customDimensions['test.run_id'] == '$RUN_ID' and success == false | count" \
            --query "tables[0].rows[0][0]" -o tsv)

          if [ "$FAILURES" -gt 0 ]; then
            echo "::error::Quality Gate Failed: $FAILURES tool failures detected in test run $RUN_ID."
            exit 1
          fi

          echo "Quality Gate PASSED: Telemetry verified and all signals healthy!"
```

---

## 7. Comparison Matrix: Methods to Segregate Traffic

| Technique | How It Works | Strengths | Weaknesses |
| :--- | :--- | :--- | :--- |
| **IP Whitelisting** | Filter out runner IP addresses | Simple initial setup | ❌ **Flawed for cloud/DevOps**: CI runners have dynamic IPs. |
| **Time Windows** | "Ignore 02:00–02:15 AM" | No code changes | ❌ **Flawed**: Delayed test runs trigger real production incidents. |
| **Inferred Call Shape** | e.g., missing parent trace ID | No client changes | ⚠️ **Fragile**: Breaks as soon as call topology changes. |
| **Custom Header (`x-test-run`)** | Test client adds `x-test-run: id` | Easy to add to cURL / scripts | ⚠️ Only persists if downstream microservices explicitly forward it. |
| **W3C Baggage (`baggage`)** | Standard OTel header | ✅ **Gold standard**: Propagates automatically across microservices. | Requires OTel SDK configured to propagate baggage context. |
| **Dedicated Test Identity** | Tests use dedicated App Registration / Service Principal | ✅ **Secure**: Cannot be spoofed by real users in request headers. | Requires setting up RBAC & separate auth credentials. |

---

## 8. Interview Talking Points

* **"How do you ensure automated evaluations in production don't trigger alerts?"**
  > *"We tag test traffic explicitly using W3C baggage (`test.run_id`) and a custom header (`x-test-run`). Middleware stamps this value directly onto OpenTelemetry spans as `customDimensions['test.run_id']`. Production alert rules filter this out with `where isempty(tostring(customDimensions['test.run_id']))`, preventing false alerts while keeping test data available for CI/CD pipeline health checks."*

* **"Why not just filter by IP or time window?"**
  > *"Filtering by IP is brittle because CI/CD runners use dynamic cloud IPs. Filtering by time window fails when tests run late or take longer than expected. Explicit in-band context propagation using W3C standards is deterministic and reliable."*
