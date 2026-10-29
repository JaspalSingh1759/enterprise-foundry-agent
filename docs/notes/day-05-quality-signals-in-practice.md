# Quality Signals from Traces in a Real DevOps Project

A companion to [day-05-quality-signals.md](day-05-quality-signals.md), which covers *what* the six queries are and what they found. This page covers the rest, from an AI DevOps engineer's point of view:

1. How signals are used in a real project (Part 1)
2. How they're delivered through a pipeline (Part 2)
3. How production keeps test traffic apart from user traffic (Part 3)
4. What we built in this project, and what we didn't (Part 4)
5. How to check it all yourself in the Azure portal and the Foundry portal (Part 5)
6. Interview questions (Part 6)

Each part is marked **Industry practice** or **Our project**, so you always know which is which.

---

## Part 1: How quality signals are used in a real project (industry practice)

### The lifecycle

A signal is only useful if it leads to an action. In a real project every signal goes through the same seven steps:

```
① instrument ─▶ ② collect ─▶ ③ query ─▶ ④ alert ─▶ ⑤ dashboard ─▶ ⑥ runbook ─▶ ⑦ feedback
   (code emits     (App Insights /  (KQL in    (rule fires   (trends for     (what the      (bad pattern →
    spans/metrics)  Log Analytics)   git)       on threshold)  the team)       on-call does)  new test case)
```

| Step         | What it means                                                                                | Who usually owns it                                     |
| ------------ | -------------------------------------------------------------------------------------------- | ------------------------------------------------------- |
| ① Instrument | Services emit spans, logs and metrics with useful attributes (tool name, outcome, test flag) | App developers, with standards set by the platform team |
| ② Collect    | Telemetry lands in one place, with sampling and retention decided on purpose                 | **AI DevOps / platform**                                |
| ③ Query      | Each signal is a KQL query, stored in git, reviewed in PRs                                   | **AI DevOps**, with AI engineers for what "bad" means   |
| ④ Alert      | Queries become alert rules with thresholds, windows and severities, deployed as code         | **AI DevOps**                                           |
| ⑤ Dashboard  | Trends over days and weeks: is quality improving or drifting?                                | **AI DevOps**                                           |
| ⑥ Runbook    | For every alert: what it means, how to check it, how to fix it, who to call                  | **AI DevOps** + AI engineers                            |
| ⑦ Feedback   | A real bad turn becomes an offline test case (Part A state check or Part B judge case)       | AI engineers, with the process run by DevOps            |

### Three kinds of signal

| Kind              | Question                         | Examples                                                                | Typical response                             |
| ----------------- | -------------------------------- | ----------------------------------------------------------------------- | -------------------------------------------- |
| **Health**        | Is it working?                   | Tool failures, latency, error rate, "service stopped sending telemetry" | Page on-call                                 |
| **Behaviour**     | Is it doing risky or odd things? | Bulk deletes, loops, changes without looking up an ID                   | Review queue or ticket; page if it's harmful |
| **Quality trend** | Is it getting better or worse?   | "Not found" rate, refusal rate, tool calls per turn over weeks          | Dashboard; investigate drift                 |

### Rules that keep signals useful

1. **Alert on rates over a window, with a minimum count.** "Not-found rate above 10% over 1 hour, with at least 20 calls." One odd request is not an incident.
2. **Compare with a baseline, not only a fixed number.** "p95 tool calls per turn is double last week's" catches drift that a fixed threshold misses.
3. **Every alert has a runbook link.** An alert nobody knows how to handle gets muted.
4. **Watch the watchers.** Have a "telemetry health" signal. A quiet service looks exactly like a healthy one.
5. **Keep test traffic out of user metrics**, but don't throw it away (Part 3).
6. **Decide sampling on purpose.** It silently changes what your alerts can see (the lesson from Part C).

---

## Part 2: Delivering signals through a pipeline (industry practice)

In a mature setup **nothing about monitoring is clicked together in the portal**. Queries, alert rules, action groups, dashboards and sampling settings are all code, deployed by the same pipeline as the app.

### What lives in git

```
docs/kql/agent-quality.kql            the queries (we have this)
infra/monitoring/alerts.bicep         one scheduledQueryRule per signal   (Day 6)
infra/monitoring/action-groups.bicep  who gets notified: email, Teams, PagerDuty   (Day 6)
infra/monitoring/workbook.bicep       the dashboard   (Day 6)
docs/runbooks/bulk-delete.md          what to do when it fires   (Day 6)
```

### A signal as an alert rule (Bicep)

This is how Q4 (bulk destructive turns) becomes an alert. It's the shape Day 6 will use:

```bicep
resource bulkDeleteAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'agent-bulk-destructive-turn'
  location: location
  properties: {
    displayName: 'Agent: bulk destructive turn'
    description: 'A single agent turn deleted or renamed more than 2 items. Runbook: docs/runbooks/bulk-delete.md'
    severity: 2
    enabled: true
    scopes: [ appInsights.id ]
    evaluationFrequency: 'PT15M'      // run every 15 minutes
    windowSize: 'PT15M'               // over the last 15 minutes
    criteria: {
      allOf: [
        {
          query: loadTextContent('../../docs/kql/q4-bulk-destructive.kql')   // the query, from git
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [ actionGroup.id ] }
  }
}
```

Because the query is loaded from the same file that's in git, **a PR that changes the query changes the alert**. Review and history come for free.

### The pipeline steps

```
build ─▶ deploy infra (incl. alert rules) ─▶ deploy app ─▶ post-deploy checks ─▶ promote
                                                              │
                                                              ├─ 1. synthetic traffic, tagged as test
                                                              ├─ 2. state checks + judge eval (Parts A, B)
                                                              ├─ 3. wait for telemetry ingestion (2–5 min)
                                                              ├─ 4. run the signal queries for the test run
                                                              │      - did the expected spans arrive? (telemetry health)
                                                              │      - do the signals look normal? (no loops, no failures)
                                                              └─ 5. fail the stage if not
```

Steps 3 and 4 as a GitHub Actions step:

```yaml
- name: Check quality signals for this test run
  run: |
    # Wait until the test run's spans have arrived (ingestion takes a few minutes).
    for i in $(seq 20); do
      n=$(az monitor app-insights query --app "$APPI" -g "$RG" --offset 1h \
            --analytics-query "requests | where customDimensions['test.run_id'] == '$RUN_ID' | count" \
            --query "tables[0].rows[0][0]" -o tsv)
      [ "$n" -ge "$EXPECTED_MIN_SPANS" ] && break
      sleep 30
    done
    [ "$n" -ge "$EXPECTED_MIN_SPANS" ] || { echo "::error::Only $n spans arrived: telemetry is broken"; exit 1; }

    # Signals for this run must look healthy: no tool failures, no runaway turns.
    bad=$(az monitor app-insights query --app "$APPI" -g "$RG" --offset 1h \
            --analytics-query "$(cat docs/kql/ci-run-health.kql | sed "s/{RUN_ID}/$RUN_ID/")" \
            --query "tables[0].rows[0][0]" -o tsv)
    [ "$bad" -eq 0 ] || { echo "::error::$bad unhealthy turns in this run"; exit 1; }
```

Why check telemetry in the pipeline at all? Because **the most common monitoring failure is silence**: a config change, a new package version or a missing connection string stops telemetry, and every alert goes quiet. A pipeline that checks "the test run's spans arrived" catches that on the day it happens, not weeks later.

### Signals during a rollout

With **canary** or **progressive** releases, a small share of real users gets the new version first, and the same signals compare old against new:

| Signal                  | Old version | Canary | Decision                                         |
| ----------------------- | ----------- | ------ | ------------------------------------------------ |
| Tool failures           | 0.1%        | 0.1%   | ok                                               |
| p95 tool calls per turn | 3           | **7**  | **roll back**: the new instructions make it loop |

That's the online version of a quality gate. It needs one extra attribute on every span: **which version handled it** (e.g. `service.version` or the agent version).

> 💡 **AWS comparison:** CloudWatch Logs Insights queries → CloudWatch alarms (CloudFormation/CDK) → SNS → on-call. Canary analysis through CodeDeploy with CloudWatch alarms as rollback triggers.

---

## Part 3: Marking test traffic in production (industry practice)

### Why it matters

Test traffic (synthetic monitoring, smoke tests after a deploy, evaluation runs) is **not real user behaviour**:
- It **distorts metrics**. Our seed script deletes every item before each run. Counted as agent behaviour, it would look like the worst bulk delete ever.
- It **causes false alerts**. Eval case #7 deliberately triggers a bulk delete.
- It **pollutes business numbers**: active users, usage, cost per customer.

But **don't drop it**. Test traffic is useful for checking that monitoring works ("did the smoke test's spans arrive?").

### The ways to mark it, from weakest to strongest

| Method                   | How                                                                                                   | Survives across services?                | Verdict                                         |
| ------------------------ | ----------------------------------------------------------------------------------------------------- | ---------------------------------------- | ----------------------------------------------- |
| By IP address            | Exclude the CI runner's IPs                                                                           | No; IPs change                           | ❌ Don't. Brittle and invisible                 |
| By time window           | "Ignore 02:00–02:15, that's the nightly test"                                                         | No                                       | ❌ Don't                                        |
| Inferred from call shape | e.g. "no parent trace = direct test call" (**what we do today**)                                      | Partly                                   | ⚠️ Works by luck; breaks when the shape changes |
| **User-Agent**           | `User-Agent: acme-synthetic/1.0`                                                                      | Only to the first service                | ✅ Fine for the entry point                     |
| **Custom header**        | `x-test-run: <run-id>` on the request; the service records it as a span attribute                     | Only if every service forwards it        | ✅ Common                                       |
| **W3C baggage**          | `baggage: test.run_id=<run-id>`, propagated by OpenTelemetry automatically, like `traceparent`        | **Yes**, if every hop propagates baggage | ✅ Best for multi-service                       |
| **Dedicated identity**   | Tests use their own API key, service principal, or test user/tenant; the service records *who* called | Yes; it's part of the auth               | ✅ Strongest; also needed for security audit    |

Most companies combine **two**: a dedicated test identity (can't be faked by accident, and shows up in audit logs) **and** a run ID in baggage or a header (so you can find one specific run).

### What the service does with the mark

```
request arrives ─▶ middleware reads x-test-run / baggage / caller identity
                ─▶ sets span attribute:  test.run_id = "ci-1234"   (or app.synthetic = true)
                ─▶ everything downstream (spans, logs) can be filtered on it
```

Then every user-facing query starts with:

```kusto
| where isempty(tostring(customDimensions["test.run_id"]))     // real users only
```

and every pipeline check uses the opposite:

```kusto
| where tostring(customDimensions["test.run_id"]) == "ci-1234"  // only this test run
```

App Insights also has a built-in column for this, `operation_SyntheticSource`. Its own availability tests fill it in, and the portal can filter on it. Custom OpenTelemetry attributes are more flexible and work the same in any backend, so most teams use them.

### Where test traffic should and shouldn't go

|                                                   | Test traffic included?                         |
| ------------------------------------------------- | ---------------------------------------------- |
| User-facing dashboards, SLOs, business metrics    | **No**                                         |
| Behaviour alerts (bulk delete, loops)             | **No**, or as a separate, lower-severity alert |
| Health alerts (tool failures, latency)            | **Yes**: a broken tool is broken for everyone  |
| Pipeline checks ("did my test run look healthy?") | **Only** test traffic, for that one run        |
| Telemetry health ("are spans arriving?")          | Yes: synthetic traffic is a heartbeat          |

### Important for AI agents: evals are test traffic too

A judge evaluation that runs against a **production** agent sends dozens of prompts through the real agent, and some of them are designed to trigger bad behaviour (#7 bulk delete, #28 prompt injection). If they aren't marked, they'll fire your production alerts. Either run evals against a staging copy, or make sure the eval traffic carries the test mark **all the way through the platform**. With a managed platform like Foundry, check whether it forwards your header or baggage to the tools; don't assume it does.

---

## Part 4: What we built in this project (our project)

### Done

| What                                                                                                | Where                                                      | State                                                                                    |
| --------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| Telemetry from the MCP server (OpenTelemetry → App Insights)                                        | [Program.cs](../../src/MafStarterPack.McpTodo/Program.cs)  | ✅ Already there from Day 1                                                              |
| **Sampling turned off** (keep every trace) after it dropped the #7 bulk delete during an eval burst | Same file, `TracesPerSecond = null`, `SamplingRatio = 1.0` | ✅ Deployed to dev; burst of 60 calls → 60 spans                                         |
| Six signal queries (Q1–Q6)                                                                          | [docs/kql/agent-quality.kql](../kql/agent-quality.kql)     | ✅ Tested against 2 days of real traffic                                                 |
| Separating agent calls from direct test calls                                                       | `agentOps` filter in every query                           | ⚠️ **Inferred**: agent calls arrive with a parent trace from Foundry, direct calls don't |

### How our "test vs agent" split actually works, and its limit

```
                                     traceparent from Foundry?   counted as agent behaviour?
state-check tests ─ direct to MCP ──────────── no ─────────────────────── no   ✅
seed-todo-list.sh ─ direct to MCP ──────────── no ─────────────────────── no   ✅
judge eval run ─ Foundry ─ agent ─ MCP ─────── yes ────────────────────── YES  ⚠️ (it's test traffic!)
real user ─ web UI ─ Foundry ─ agent ─ MCP ─── yes ────────────────────── yes  ✅
```

So our split is really **"direct calls" vs "calls through the agent"**, not **"test" vs "user"**. The three bulk-delete turns that Q4 found were all **eval case #7**, which is test traffic. That's fine in dev, where there are no real users. In production it would be a false alert.

### Not done yet

| Gap                                      | Why it matters                                                    | Planned fix                                                                                                                                                                                                                                  |
| ---------------------------------------- | ----------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **No explicit test mark**                | Eval traffic through the agent looks like user traffic            | Test callers send `x-test-run: <run-id>`; the MCP server records it as `test.run_id`. **Open question:** does Foundry forward custom headers or baggage from the caller to the MCP server? It forwards `traceparent`; the rest needs a test. |
| **Alert rules, action group, dashboard** | Queries alone don't wake anyone up                                | Day 6: Bicep `scheduledQueryRules`, as in Part 2                                                                                                                                                                                             |
| **Pipeline signal check**                | Nothing checks that telemetry still arrives after a deploy        | Part E: add "spans for this run arrived" + "no unhealthy turns" to the post-deploy stage                                                                                                                                                     |
| **Foundry's own spans missing**          | No reply text, so no refusal rate and no turns without tool calls | Investigate why `responsesapi` spans stopped after App Insights was recreated                                                                                                                                                                |
| **No agent version on spans**            | Can't compare old vs new during a rollout                         | Record the agent version as an attribute once Foundry spans are back                                                                                                                                                                         |

---

## Part 5: Checking it yourself in the portals (our project)

Our resources: App Insights **`appi-6a3q2sputpvay`** in resource group **`rg-dev`**, Foundry project **`ai-project-dev`** on account **`ai-account-6a3q2sputpvay`**.

### 5.1 Azure portal: run the signal queries (Logs)

1. Go to [portal.azure.com](https://portal.azure.com) → search **`appi-6a3q2sputpvay`** → open it.
2. Left menu → **Monitoring → Logs**. Close the query gallery if it opens.
3. Set the **time range** at the top (e.g. *Last 48 hours*). The queries also filter on `ago(window)`, so set `let window = 2d;` to match.
4. Paste one query from [agent-quality.kql](../kql/agent-quality.kql), e.g. Q4 → **Run**.

What you should see right now:

| Query                      | Expected result                                                                                        |
| -------------------------- | ------------------------------------------------------------------------------------------------------ |
| Q2 tool calls per turn     | p50 1, p95 3, max 9, `over6` = 3                                                                       |
| Q3 changes without listing | `guessed` = 0                                                                                          |
| Q4 bulk destructive turns  | 3 rows, each `deletes` 4 / `updates` 4, around 05:04, 05:20, 05:39 UTC on 2026-10-28                   |
| Q6 telemetry health        | `mcp-todo` with `lastSeen` close to now; `agent` with an old `lastSeen` (it's only used by the web UI) |

Tip: **Save** each query (*Save → Save as query*, category "Agent quality") so the team can find them in the query gallery.

### 5.2 Azure portal: see one agent turn as a tree (Transaction search)

This is the best way to *see* what a signal means.

1. Copy an `operation_Id` from a Q4 row (e.g. `897065af329ebb8dc70fb9e72fdaebc3`).
2. In the App Insights resource → **Investigate → Transaction search** (in newer portals: **Search**).
3. Paste the ID in the search box, set the time range to cover it, and press Enter.
4. Click any result → **End-to-end transaction details**.

You'll see the turn as a timeline:

```
POST /mcp/                 (parent: Foundry's span, not in our App Insights)
  └ tools/call get_todo_items
POST /mcp/
  └ tools/call update_todo_item      ┐
POST /mcp/                           │ 4 × rename, 4 × delete:
  └ tools/call delete_todo_item      │ eval case #7, no confirmation
  …                                  ┘
```

Click a `tools/call` span → the right-hand panel shows `gen_ai.tool.name`, `mcp.session.id` and `microsoft.sample_rate` (absent since the fix). Click **View all** (or *Traces & events*) to see the linked log lines, e.g. *"Deleted todo item: '151'"*.

### 5.3 Azure portal: the service picture (Application map)

**Investigate → Application map** shows the nodes `webui`, `agent` and `mcp-todo` with call counts and failure rates. You won't see a Foundry node, because its spans don't arrive (the gap in Part 4). That's a quick visual check for "is something missing from the picture?".

### 5.4 Azure portal: watch it live (Live metrics)

**Investigate → Live metrics** shows incoming requests per second, with a delay of about one second. Run the seed script or the state-check tests and watch `mcp-todo` requests spike. Good for checking "is telemetry flowing at all?" without waiting for ingestion. Live metrics is unsampled and separate from the stored data, so it doesn't prove that spans are being *kept*. Use Logs for that (5.6).

### 5.5 Foundry portal: what you can and can't see

1. Go to [ai.azure.com](https://ai.azure.com) → open project **`ai-project-dev`**.
2. **Evaluations**: the judge eval runs from Part B (e.g. `baseline-4`). Open a run → each row shows the prompt, the agent's reply, and every evaluator's score and reason. The row for case #7 shows *"lack of confirmation for a destructive bulk action"*. Compare it with the Q4 result in 5.1: **the same bug, seen from the words (Foundry) and from the actions (App Insights)**.
3. **Tracing** (under *Observe*/*Observability*, depending on the portal version): this lists the agent's traces **if** Foundry's spans reach the connected App Insights. In our project it shows the connection to `appi-6a3q2sputpvay` but **no hosted-agent traces since 2026-10-26**. That's the gap from Part 4, and seeing it here is the quickest way to confirm whether it's fixed later.
4. **Agents → todo-agent**: the deployed versions. The eval rows record which version answered (`todo-agent:6` in run 4).

### 5.6 Verify the sampling fix (Logs)

```kusto
requests
| where timestamp > ago(1d) and cloud_RoleName == "mcp-todo" and name startswith "tools/call"
| summarize spans = count() by rate = tostring(customDimensions["microsoft.sample_rate"]), bin(timestamp, 1h)
| order by timestamp asc
```

Expected: before the redeploy (~16:42 UTC on 2026-10-28), some hours have rate `50` or `33.3`. After it, only an **empty** rate, meaning no sampler. Our real result: on 2026-10-28, hours 03:00–05:00 and 14:00 (the eval runs) have spans at rate `50` and `33.3`. The 16:00 hour has 1 span at `100` (a test call made before the redeploy) and **71 with an empty rate** after it: the 60-call burst, plus the seed script.

### 5.7 Verify the agent/direct split (Logs)

```kusto
let agentOps = requests
    | where timestamp > ago(2d) and cloud_RoleName == "mcp-todo" and name startswith "POST /mcp"
    | where operation_ParentId != operation_Id
    | distinct operation_Id;
requests
| where timestamp > ago(2d) and cloud_RoleName == "mcp-todo" and name startswith "tools/call"
| extend origin = iff(operation_Id in (agentOps), "agent (via Foundry)", "direct (tests/seed)")
| summarize calls = count() by origin, tool = tostring(customDimensions["gen_ai.tool.name"])
| order by origin, calls desc
```

Expected: `update_todo_item` appears **only** under "agent", because no test or seed script ever renames. If it ever shows up under "direct", the split has broken.

### 5.8 The same checks from the command line

Everything above also works without the portal, which is how a pipeline does it:

```bash
az monitor app-insights query --app appi-6a3q2sputpvay -g rg-dev --offset 2d \
  --analytics-query "$(cat query.kql)" -o table
```

---

## Part 6: Interview questions

**"How do you turn trace data into something actionable?"**

> "Every signal goes through the same lifecycle: instrument, collect, query, alert, dashboard, runbook, feedback. The queries live in git, and the alert rules load them from the same files through Bicep, so a reviewed PR changes both. Every alert has a minimum count, a time window and a runbook link. And real bad turns get turned into offline test cases."

**"How do you deploy monitoring?"**

> "As code, in the same pipeline as the app. Alert rules are `scheduledQueryRules` in Bicep, action groups and dashboards too. After deploying, the pipeline sends tagged synthetic traffic and checks two things: that its spans actually arrived, and that the signals for that run look healthy. The first check catches the most common monitoring failure, which is silence."

**"How do you keep test traffic out of production metrics?"**

> "Mark it explicitly. Never by IP or time window. The strongest setup is a dedicated test identity plus a run ID carried in W3C baggage or a header, recorded as a span attribute. User dashboards and behaviour alerts filter it out; health alerts and pipeline checks use it. In our project we could only infer it, because agent calls arrive with a trace parent from Foundry and direct test calls don't. That breaks for eval runs that go through the agent: our bulk-delete signal fired on our own eval case. So the next step is an explicit test mark, and checking whether Foundry forwards it to the tools."

**"How would you check a signal is correct?"**

> "Three ways. Run it against known traffic: our bulk-delete query found exactly the three runs of eval case #7. Trigger it on purpose: one call with a non-existent ID made the not-found query count exactly one. And open the trace in the portal's end-to-end transaction view, to check the spans really show what the query claims."

**"What's the risk with a managed agent platform here?"**

> "You don't control its telemetry. Foundry's runtime spans stopped arriving in our App Insights after it was recreated, and you can't add attributes to them or change their sampling. So we built the signals on the MCP server we own, which sees every tool call, and we monitor the gap itself with a telemetry-health query."

---

## Glossary

| Term                               | Meaning                                                                                                                       |
| ---------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| **Signal**                         | A number from telemetry that shows health, behaviour or quality, e.g. deletes per turn.                                       |
| **Synthetic / test traffic**       | Requests made by tests or monitors, not by real users.                                                                        |
| **W3C baggage**                    | A standard HTTP header for passing key-value pairs (like `test.run_id`) along with a trace, across services.                  |
| **`operation_SyntheticSource`**    | App Insights' built-in column for marking synthetic traffic, used by its availability tests.                                  |
| **`scheduledQueryRules`**          | The Azure resource type for a log-query alert: runs a KQL query on a schedule and fires when it crosses a threshold.          |
| **Action group**                   | Who or what gets notified when an alert fires (email, Teams, webhook, PagerDuty).                                             |
| **Runbook**                        | The written steps for handling one specific alert.                                                                            |
| **Canary**                         | Releasing a new version to a small share of users first, and comparing its signals with the old version before going further. |
| **End-to-end transaction details** | The App Insights view that shows all spans of one `operation_Id` as a timeline.                                               |
