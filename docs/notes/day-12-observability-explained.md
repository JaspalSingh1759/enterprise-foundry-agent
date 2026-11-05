# Day 12: observability as code

Days 10 and 11 were skipped. Day 12 is deliberately simple but production-shaped: everything is in Bicep, every
alert is built on telemetry this environment really produces, and one alert was fired on purpose and followed end to
end.

---

## 1. Day 12 in one paragraph

On Day 9 we could already *see* a case in App Insights: one trace from disputes-api through the three agents to
mcp-banking and AI Search. But seeing isn't **monitoring**: nobody looks at a dashboard at 3 a.m. Day 12 adds the parts
that make the platform **tell us** when something is wrong. An **action group** (who gets told), **10 alerts** (when
to tell them), a **workbook** (where on-call looks first), and an **SLO** (how good is good enough). All of it lives
in [infra/monitoring.bicep](../../infra/monitoring.bicep), reviewed and deployed like any other code.

**30-second interview pitch:**

> "All telemetry goes to one Log Analytics workspace: our apps through OpenTelemetry and App Insights, Foundry's
> agent traces (agent runs, model calls, tool calls, token counts), and the Container Apps logs. On top of it, in
> Bicep: an action group, log alerts for agent-specific signals (tool loops, tool failure rate, investigation latency,
> manual-handling rate, policy search no-hits, override rate), metric alerts on the Foundry account for 429s, auth
> failures and token spend, and an operations workbook. The SLO is 99% of investigations return a draft within two
> minutes. I tested the setup by removing a role assignment on purpose and following the alert from email to root
> cause to fix."

---

## 2. Where the telemetry comes from (the "one workspace" point)

```
 disputes-api ─┐  OpenTelemetry (ServiceDefaults)
 mcp-banking  ─┼──────────────────────────────► App Insights (appi-…) ─┐
 webui        ─┘                                                       │  workspace-based:
                                                                       ├─► Log Analytics (log-…)
 Foundry Agent Service ── agent traces (responsesapi) ─► App Insights ─┤     ONE place to query
                                                                       │
 Container Apps environment ── console + system logs ─────────────────┘

 Foundry account ── platform metrics (ModelRequests by status code, TotalTokens) ─► Azure Monitor metrics
```

| Table | What's in it | Who writes it |
| --- | --- | --- |
| `AppRequests` | Incoming HTTP requests, including MCP `tools/call get_case` | our three apps |
| `AppDependencies` | Outgoing calls; **Foundry's `invoke_agent`, `chat <model>`, `execute_tool <tool>` spans** | our apps + Foundry (`AppRoleName = responsesapi`) |
| `AppTraces` | Log lines with structured properties (`CaseId`, `Status`, `Seconds`…) | our apps |
| `ContainerAppConsoleLogs_CL` / `SystemLogs_CL` | stdout, restarts, probe failures, scaling | Container Apps |

**The plan said "point both log streams at one workspace".** That was already true: the App Insights resource is
workspace-based on `log-…`, and the Container Apps environment sends its logs to the same workspace. The point to
remember: **check before you build**. One workspace means one KQL query can join a disputes-api request, the agent
spans and the container restart that caused it.

**What Foundry gives us for free** (span attributes on `AppDependencies`, following the OpenTelemetry GenAI
conventions):

| Attribute | Used for |
| --- | --- |
| `gen_ai.agent.name`, `gen_ai.agent.version` | latency and failures per agent *version* |
| `gen_ai.response.id` | grouping tool calls per agent turn (tool-loop alert) |
| `gen_ai.tool.name` | tool call counts and failures |
| `gen_ai.usage.input_tokens` / `output_tokens` | cost per agent |
| `error.type` (e.g. `rate_limit_exceeded`) | why a model call failed |

> **A privacy finding:** the Foundry spans also carry **full message content**: `gen_ai.input.messages`, tool
> arguments and results, so claim text and customer data end up in telemetry. For a bank that's a problem (Day 11's
> "no customer content in traces"). Fixes: turn off content recording in Foundry's tracing settings, or filter the
> attributes before export, and restrict who can read the workspace. Our own new log lines deliberately log **IDs and
> statuses only**.

---

## 3. What we added to the app: three log lines

The alerts need signals that only disputes-api knows. [Program.cs](../../src/MafStarterPack.DisputesApi/Program.cs):

```csharp
// after an investigation
app.Logger.LogInformation("Case {CaseId}: investigation finished as {Status} in {Seconds} s.", …);
// after approve / reject / request-info
app.Logger.LogInformation("Case {CaseId}: decided {Decision} (draft {DraftAction}).", …);
```

**Structured logging** is the key: `{Status}` becomes a queryable property (`Properties.Status`) in `AppTraces`, not
just text inside a message. The audit log (WORM blob) records the same events for compliance; the logs are for
operations. **Two stores with two purposes**: the audit log must be complete and immutable; telemetry can be sampled
and is kept for a shorter time.

---

## 4. The alerts

### 4.1 Log alerts vs metric alerts

| | Log alert (scheduled query) | Metric alert |
| --- | --- | --- |
| Source | KQL over Log Analytics | Azure Monitor platform metrics |
| Good for | Signals only your telemetry knows (tool loops, outcomes, rates you compute) | Signals Azure already counts (429s, tokens, CPU) |
| Speed | Runs every 5–15 min, plus ingestion delay (2–5 min) | About 1 minute |
| Cost | About $1.50/month each at 5-minute frequency | About $0.10/month each |
| How ours fire | The query returns a row → alert | Value above threshold in the window |

**Our pattern for log alerts:** the query itself contains the condition (`| where Failed * 100.0 / Calls > 5`), and
the rule fires when **any row** comes back. The query is readable on its own, and it can be pasted into Log Analytics
to see exactly what the alert saw.

### 4.2 The ten alerts

| Alert | Type | Sev | Fires when | Why this threshold |
| --- | --- | --- | --- | --- |
| `agent-tool-loop` | log | 2 | One agent turn makes > 15 tool calls | Real max is 7 (p95 5); 15 is clearly abnormal |
| `tool-failure-rate` | log | 2 | > 5% of tool calls fail (min 10 calls) in 15 min | Normally 0 failures; minimum volume avoids "1 of 1 failed = 100%" |
| `investigation-latency-p95` | log | 3 | p95 > 120 s over 1 hour (min 3) | Normal ≈ 50 s; 120 s is the SLO line |
| `manual-handling-rate` | log | 2 | > 20% of investigations end in manual handling (min 3) in 1 hour | Some manual handling is healthy; a spike means a bad version or a broken tool |
| `policy-search-problems` | log | 2 | `search_policy` fails or returns 0 passages | Without passages the policy agent can't cite a section |
| `server-errors` | log | **1** | ≥ 3 HTTP 5xx from one app in 15 min | Users affected now |
| `override-rate` | log | 3 | Analysts overrule > 30% of drafts in a day (min 10) | Quality drift; slow signal, so a daily window |
| `foundry-throttling` | metric | 2 | > 5 model requests with **429** in 5 min | Quota too small; we retry, so cases slow down before they fail |
| `foundry-auth-failures` | metric | **1** | Any model request with **401/403** | Identity broken: every investigation will fail |
| `token-spend` | metric | 3 | Total tokens > 2M per hour | Busiest real hour (40-case eval run) ≈ 1M |

**Severity** decides who gets woken up. In production, Sev 0–1 pages on-call through PagerDuty / Opsgenie; Sev 2–3
goes to a team channel or a ticket. Here, all of them email the action group.

**`autoMitigate: true`:** when the condition clears, the alert resolves itself and sends a "Resolved" email, so the
alert list shows what's broken *now*.

### 4.3 How these map to the AI-specific failure modes

| AI-specific failure | Signal |
| --- | --- |
| A prompt change makes the model loop over tools | `agent-tool-loop`, `token-spend` |
| A bad agent version gives bad answers | `manual-handling-rate` (fast), `override-rate` (slow) |
| Quota too small for load | `foundry-throttling`, `investigation-latency-p95` |
| RAG broken (index, alias, role) | `policy-search-problems`, then `manual-handling-rate` |
| Identity or secret broken | `foundry-auth-failures`, `tool-failure-rate` (MCP key out of sync) |

> **Interview:** "Classic monitoring checks if the service is *up*. Agent monitoring must also check if it's *right*
> and *affordable*: tool loops, token spend, how often the output fails code checks, how often humans overrule it."

---

## 5. The workbook

**"Maple Bank disputes: operations"** (Azure portal → Monitor → Workbooks, or the resource group). A time-range picker
and eight tiles:

| Tile | Question it answers |
| --- | --- |
| SLO | Are we meeting 99% within 2 minutes? How much error budget is used? |
| Investigation outcomes | Drafts vs manual handling per hour |
| Agent turn latency | p50 / p95 per agent **and version**: did the new version get slower? |
| Tokens and cost per agent | Which agent costs most? (prices as in the eval harness) |
| Tool calls | Calls, failures, p95 per tool |
| Model call errors | 429s (`rate_limit_exceeded`) over time |
| Quality signals | Override rate, manual-handling rate, policy search no-hits |
| HTTP errors by app | 4xx/5xx per app (MCP's normal `GET /mcp` 405 filtered out) |

From the existing data, the cost tile already shows what Day 8 measured: the **evidence agent is the most expensive**
(most tokens: it reads the most data).

---

## 6. SLO and error budget

**SLI** (what we measure): the share of investigations that end `AwaitingApproval` **within 120 seconds**.
**SLO** (the target): **99% over 28 days**.
**Error budget:** the 1% allowed to be bad. At 1,000 investigations a month, that's **10 slow or failed
investigations**.

```
Error budget used = bad investigations / (total × 1%)
  4 bad out of 1,000 → 4 / 10 = 40% of the budget used
```

**Why the error budget matters:** it turns "should we ship this risky prompt change?" into a rule. Budget left: ship.
Budget spent: freeze changes and fix reliability first.

**Why these choices:**

- **User-centred:** the analyst cares that a usable draft arrives quickly, not that the API returned 200 (a
  manual-handling result also returns 200).
- **120 s:** typical is 50 s, so there's room for 429 retries; beyond 2 minutes the analyst gives up waiting.
- **99%, not 99.9%:** each investigation depends on a model, three agents and four services. Promising more than the
  dependencies offer just burns the budget on things we don't control.

**Next step in production:** multi-window **burn-rate alerts** instead of fixed thresholds. Page if the budget burns
14× faster than allowed over 1 hour (it would run out in about 2 days); ticket if 6× over 6 hours.

---

## 7. The drill: an alert fired on purpose

**Scenario:** someone removes a role assignment (a realistic incident: a cleanup script, a wrong Bicep change, a
manual portal edit). We removed mcp-banking's **Search Index Data Reader** role on AI Search and ran one
investigation (CASE-0003).

### 7.1 Timeline (UTC, 2026-11-05)

| Time | What happened | Where we saw it |
| --- | --- | --- |
| 14:49:54 | Role assignment removed | `az role assignment delete` |
| 14:54:11 | Investigation of CASE-0003 started (after waiting for the removal to take effect) | disputes-api |
| 14:54:41 | Intake and evidence agents ran normally | `invoke_agent` / `execute_tool` spans |
| 14:55:15 | Policy agent called `search_policy` → AI Search **403** → `Policy search failed: HTTP 403` (3 attempts) | mcp-banking `AppTraces` |
| 14:55:41 | Investigation finished as **AwaitingApproval**, draft **deny** | disputes-api response |
| 14:56:14 | Role restored, **with the same assignment ID** so the next `azd provision` doesn't conflict | `az role assignment create --name <same GUID>` |
| **14:59:58** | **`disputes: policy-search-problems` (Sev 2) fired** → email to the action group | Azure Monitor → Alerts |

**Detection time ≈ 4.7 minutes** from the first failure to the alert: about 2–3 minutes of log ingestion plus the
5-minute evaluation schedule. That's typical for log alerts, and it's why "the site is down" signals should be metric
alerts or availability tests instead.

**The on-call path:** alert email → Alerts blade (fired query, row count) → paste the query into Log Analytics → the
mcp-banking traces show `HTTP 403` from `search-…/indexes/maple-knowledge` → 403 from a data plane means **a
permissions problem** → check role assignments on the search service → missing → restore → the alert auto-resolves
when the window is clean.

### 7.2 What the drill found (the valuable part)

1. **The alert worked, but the case wasn't stopped.** The policy agent got "Not available" from search, but still
   produced a confident **deny** that cites sections 4.1 and 3.1. The section numbers came from the **rules engine's**
   result, not from search. The recommendation may even be right (the rules engine is the authority), but the draft
   went to an analyst **without the policy text behind it**, and nothing on the case says so. The design said "fail
   closed"; in practice it **failed open**.
2. **Tool spans said everything was fine.** `execute_tool search_policy` was recorded as **successful**, because the
   tool *returned* a "Not available" message instead of an error. So the `tool-failure-rate` alert couldn't see it.
   Only our own log line caught it. Lesson: **a tool that hides its failures in a normal answer is invisible to
   platform telemetry.**
3. **Manual handling didn't trigger**, so `manual-handling-rate` stayed quiet too.

### 7.3 The fixes (proposed, not implemented yet)

| Fix | Effect |
| --- | --- |
| `search_policy` returns an **MCP error** (`IsError = true`) when search fails | Foundry marks the tool span as failed → `tool-failure-rate` sees it; the agent knows it's a failure |
| `OutputChecks.Policy` requires a **successful** `search_policy` call with passages (the `mcp_call` items include the tool output) | No passages → retry → manual handling: **fail closed** |
| A synthetic check: one known case every 15 minutes | Detects broken RAG even with no real traffic |

> **Interview story (STAR):** "I tested our monitoring by removing a role assignment on purpose. The alert fired in
> under 5 minutes and pointed straight to a 403 from AI Search. But the drill also showed the agent still produced a
> recommendation without the policy text, and the platform's tool telemetry reported success, because our tool
> returned the failure as a normal message. The fix was to make tool failures real errors and add a code check that
> fails closed without policy passages. A drill that only proves the alert fires is half a drill: the point is to find
> what you didn't expect."

---

## 8. What a real platform adds

| Topic | Ours | Production |
| --- | --- | --- |
| Notification | Email | PagerDuty/Opsgenie for Sev 0–1, Teams/Slack for Sev 2–3, with runbook links in each alert |
| SLO alerts | Fixed thresholds | Multi-window burn-rate alerts |
| Per-environment | Same thresholds everywhere | Thresholds as parameters; dev alerts off or email-only |
| Privacy | Our logs: IDs only. Foundry: content recorded | Content recording off, attribute filtering, workspace RBAC, shorter retention |
| Cost | Ingestion not capped | Sampling for traces, a daily cap on the workspace, Basic logs for noisy tables |
| Index freshness | Not monitored | Alert when the indexer last succeeded more than N hours ago (needs Search diagnostic logs) |
| Synthetic checks | None | An availability test that runs a known case every 15 minutes |

---

## 9. Interview questions

**"Design monitoring for an agent platform."**
> Start with one workspace and distributed tracing across the app, the agent runtime and the tools, using the
> OpenTelemetry GenAI conventions so agent, model and tool spans carry name, version and token counts. Then three
> layers of signals: **health** (errors, latency, 429s, auth failures), **cost** (tokens per agent, per tenant, spend
> alerts), and **quality** (tool loops, output-check failures, human override rate, RAG no-hits). Alerts as code with
> severities mapped to routing, a workbook for on-call, and an SLO with an error budget that gates releases. And
> privacy: no customer content in traces.

**"Log alert or metric alert?"**
> Metric alerts for what the platform already counts: fast and cheap. Log alerts for signals only my telemetry knows,
> like tool calls per turn or override rate. They're slower (5–15 minutes plus ingestion), so they're not for "the
> site is down".

**"Define one SLO with its error budget."**
> 99% of investigations return a draft within 2 minutes, over 28 days. The budget is 1%, about 10 investigations at
> 1,000 a month. If we've spent it, we freeze prompt and model changes until reliability is back.

**"How do you know an agent's quality dropped in production, without labels?"**
> Proxy signals: the share of outputs failing code checks (manual handling), the human override rate, tool loops, RAG
> no-hits, and latency or token changes per agent version. Then sample the flagged cases into the eval set.

**"An alert fires at 3 a.m. What do you do?"** (use the drill story, §7)

---

## 10. Code map

| What | File |
| --- | --- |
| Action group, alerts, workbook | [infra/monitoring.bicep](../../infra/monitoring.bicep) |
| Wiring (`ALERT_EMAIL` azd env value) | [infra/resources.bicep](../../infra/resources.bicep), [infra/main.bicep](../../infra/main.bicep), [infra/main.parameters.json](../../infra/main.parameters.json) |
| New log lines | [src/MafStarterPack.DisputesApi/Program.cs](../../src/MafStarterPack.DisputesApi/Program.cs) |
| Telemetry setup (OpenTelemetry → Azure Monitor) | [src/MafStarterPack.ServiceDefaults/Extensions.cs](../../src/MafStarterPack.ServiceDefaults/Extensions.cs) |
