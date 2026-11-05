# Day 15: an AI gateway with API Management

A one-day experiment: API Management (Basic v2) in front of the Foundry Responses API, with a **token limit per
team** and **token metrics per team and agent**. Deployed, tested and deleted the same day. The most important
output is the write-up of **what a gateway can and can't see** when the model calls happen inside Foundry.

---

## 1. Day 15 in one paragraph

When ten teams share one model quota, one team's batch job can use it all up and break everyone else, and finance
asks "which team spent what?". The standard answer is an **AI gateway**: every call goes through one place that
knows **who** is calling, **limits** their tokens, and **counts** them. We put API Management in front of Foundry,
identified callers by their Entra token (no keys), gave the production team ("disputes") a large budget and the
sandbox a small one, sent token metrics to App Insights, and proved it: the sandbox got a **429** after 5,000
tokens a minute while production investigations kept flowing.

**30-second interview pitch:**

> "I put API Management in front of our Foundry agents as an AI gateway. It validates each caller's Entra token,
> maps the app ID to a team, and applies `llm-token-limit` per team: a tokens-per-minute rate and a daily quota.
> `llm-emit-token-metric` sends tokens per team and agent to App Insights for cost allocation, and the gateway calls
> Foundry with its own managed identity. The important finding was the boundary: with Foundry agents, the gateway sees
> one request per agent *turn* with its total tokens, not the individual model and tool calls inside it. So it's
> right for budgets and chargeback, but it can't stop a runaway turn halfway, and content checks there miss
> injection that arrives through tool results."

---

## 2. The architecture

```
            BEFORE (Days 8–12)                               WITH THE GATEWAY (Day 15)

 disputes-api ──MI token──► Foundry          disputes-api ──MI token──► API Management ──gateway MI──► Foundry
                                             az CLI (sandbox) ─token──►  1. validate Entra token
                                                                         2. app ID → team
                                                                         3. llm-token-limit per team
                                                                         4. llm-emit-token-metric
                                                                         5. swap in its own token
```

**disputes-api needed no code change.** It already used the project endpoint from configuration and a token for
`https://ai.azure.com`. We changed one setting (`Foundry__ProjectEndpoint` → the gateway URL), and the gateway
validates that same token.

| File | What |
| --- | --- |
| [infra/gateway/gateway.bicep](../../infra/gateway/gateway.bicep) | API Management Basic v2, backend, API + operation, named values, App Insights logger, gateway's role on Foundry |
| [infra/gateway/foundry-policy.xml](../../infra/gateway/foundry-policy.xml) | The policy: token validation, team mapping, limits, metrics, managed identity |
| [scripts/gateway/deploy.sh](../../scripts/gateway/deploy.sh) | Deploys; `--switch` points disputes-api at it |
| [scripts/gateway/teardown.sh](../../scripts/gateway/teardown.sh) | Switches back, removes the role, deletes **and purges** API Management |

**Why separate from the azd template:** it's a one-day experiment that bills by the hour. Two scripts make
"deploy" and "remove completely" one command each, and the main template stays clean.

---

## 3. The policy, step by step

```xml
<validate-azure-ad-token tenant-id="{{tenant-id}}" …>        ← 1. who? (Entra token, no subscription keys)
    <client-application-ids> disputes-api MI, Azure CLI </…>
    <audiences> https://ai.azure.com </audiences>
<set-variable name="team" … appid == disputes MI ? "disputes" : "sandbox" />   ← 2. which team?
<choose>
  <when team == disputes>  <llm-token-limit tokens-per-minute="200000" token-quota="5000000" Daily />
  <otherwise>              <llm-token-limit tokens-per-minute="5000"   token-quota="50000"   Daily />
</choose>                                                     ← 3. budget per team
<llm-emit-token-metric> Team, Agent </llm-emit-token-metric>  ← 4. who used what
<authentication-managed-identity resource="https://ai.azure.com" />   ← 5. call Foundry as the gateway
<forward-request timeout="300" />                             ← agent turns take up to a minute
```

**Design choices worth explaining:**

| Choice | Why |
| --- | --- |
| **Entra token, not subscription keys** | No secrets to store, rotate or leak. The caller's identity (app ID) *is* the cost-allocation key |
| **Counter key = team** | One budget per team, whatever number of replicas or instances it runs |
| **Rate + quota** | The rate stops bursts (429 + `Retry-After`); the daily quota caps total spend (403) |
| **`estimate-prompt-tokens="false"`** | Counts the *real* usage from the response. The trade-off: the request that crosses the limit still goes through; the next one is blocked |
| **The gateway's own managed identity to Foundry** | Only the gateway needs a role on Foundry. In production, callers *lose* their direct role, so the gateway can't be bypassed |
| **No retry in the gateway** | An agent turn is several model and tool calls; repeating it doubles cost. The caller already retries on 429 |
| **No bodies in the logs** | Requests carry claim text and customer data |
| **Headers prefixed `x-gateway-`** | Found during testing: Foundry already sends `x-ratelimit-*` headers (the model deployment's quota), and ours were mixing with them |

**Why Basic v2:** `llm-token-limit` isn't available on the Consumption tier (it is on Developer, Basic, Basic v2,
Standard, Standard v2, Premium, Premium v2). Basic v2 deploys in minutes and bills by the hour.

---

## 4. What we tested

| Test | Result |
| --- | --- |
| No token | **401** from the gateway; Foundry never called |
| Sandbox call 1 (intake agent) | 200, **3,241** tokens, `x-quota-remaining` 46,759 |
| Sandbox call 2 | 200, **3,044** tokens: allowed although only 1,759 were left this minute (no estimation, so the overshoot is detected afterwards) |
| Sandbox call 3 | **429 "Token limit is exceeded. Try again in 6 seconds."** + `Retry-After: 6` |
| Full investigation via disputes-api ("disputes" team) | **AwaitingApproval, credit $170.91, 45 s**: all three agents through the gateway, unaffected by the sandbox being blocked |
| Token metrics | `azure.ai_gateway.client.token.usage` in App Insights with **Team** and **Agent** dimensions (§5) |

**That's the whole point in one test:** the sandbox was throttled at the same time as production ran normally.
One team can't use up another team's quota.

**Two behaviours to know (both are in the docs):**

- **Limits are enforced after the fact.** Without prompt estimation, the gateway learns a request's size from the
  response. Concurrent requests can overshoot the limit before it blocks.
- **The remaining-quota header is an estimate.** Ours went *up* between calls (43,715 → 46,956). Don't bill from
  headers; bill from metrics.

---

## 5. Cost allocation

### 5.1 What the gateway measured

The metric `azure.ai_gateway.client.token.usage` arrives in App Insights (`AppMetrics`) with our dimensions **Team**
and **Agent**, plus **`gen_ai.token.type`**: input, output, reasoning, cached and cache-write tokens.

| Team | Agent | Input | Output (of which reasoning) | Cached input | Total |
| --- | --- | --- | --- | --- | --- |
| disputes | intake-agent | 1,116 | 1,464 (1,280) | 0 | **2,580** |
| disputes | evidence-agent | 8,645 | 1,299 (640) | 1,408 | **9,944** |
| disputes | policy-agent | 3,870 | 1,012 (576) | 1,664 | **4,882** |
| sandbox | intake-agent ×2 | 2,159 | 4,126 (3,776) | 0 | **6,285** |

**One investigation (CASE-0004) = 17,406 tokens ≈ $0.008** (gpt-5-nano for intake, gpt-5-mini for the others, at
the eval harness prices). That matches Day 8's "about one cent per case", now measured **per team** at the gateway.

**Things the numbers show:**

- **Reasoning tokens dominate nano's output**: 1,280 of intake's 1,464 output tokens are "thinking". A cheaper model
  isn't always cheaper per task. Day 8's eval chose nano on measured cost, not list price.
- **The evidence agent is the cost driver** (≈ 57% of tokens): it reads the most data. That's where to optimise.
- **Cached input tokens appear** (evidence, policy): repeated instruction prefixes are cached, which lowers cost.

```kusto
// Tokens per team and agent (the chargeback query)
AppMetrics
| where Name == 'azure.ai_gateway.client.token.usage'
| extend Team = tostring(Properties.Team), Agent = tostring(Properties.Agent), Type = tostring(Properties['gen_ai.token.type'])
| where Type in ('input_tokens', 'output_tokens')
| summarize Tokens = sum(Sum) by Team, Agent, Type
```

**From tokens to money:** the gateway counts tokens, not dollars, and doesn't know each model's price. Cost per team
= tokens by model × price, computed in KQL or in the finance export. For chargeback, the team dimension is what
matters. The Azure bill alone can't split one shared model deployment between teams.

---

## 6. What the gateway can and can't see (the key trade-off)

With Foundry **agents**, the model calls happen **inside** Foundry. Our gateway sits in front of the **agent API**,
not the model.

```
 caller ──► GATEWAY ──► Foundry Agent Service ─────────────────────────────────────────┐
              sees:     │  model call 1 → tool call → model call 2 → tool call → … → answer │
              1 request │  ◄──────────────── gateway sees NONE of this ────────────────► │
              + total   └──────────────────────────────────────────────────────────────┘
              tokens
```

| The gateway CAN | The gateway CAN'T |
| --- | --- |
| Identify the caller and team | See individual model calls or tool calls |
| Limit tokens per team, **between turns** | Stop a runaway turn **halfway** (a tool loop finishes, then the *next* turn is blocked) |
| Count tokens per team and agent (the response's `usage` is the turn's total) | Know which tool used how much, or per-model-call latency |
| Apply a daily quota and return 429/403 | **Load-balance across model deployments**: the agent definition picks the deployment, inside Foundry |
| Log requests (without bodies) | Run content safety on **tool results**: injection through a receipt or policy text never passes the gateway |
| Block or allow whole agents per caller | Use **semantic caching**: agent turns with live tool data aren't cacheable |

**What fills the gaps:**

- **Inside the turn:** Foundry's own traces (Day 12): `invoke_agent`, `chat`, `execute_tool` spans with tokens per
  model call. Our `agent-tool-loop` alert covers the runaway-turn case.
- **Model-level gateway:** route the *agents' model calls* through API Management. Foundry can use an API
  Management-fronted model connection / AI gateway integration (partly **preview**, so check current docs). Then
  per-model-call limits, load balancing across regions or PTU + pay-as-you-go spillover, and model-level content
  checks all work.
- **Where each one belongs:**
  - Gateway in front of *agents*: per-team budgets, chargeback, access per agent.
  - Gateway in front of *models*: quota protection, load balancing, model-level policies.
  - Large platforms use both.

**429s from two places.** A 429 can now come from the **gateway** (team budget) or from **Foundry** (model
deployment quota). disputes-api treats both the same way (wait and retry), which is fine. But on-call must tell them
apart: the `x-gateway-*` headers and the gateway's request logs show which one it was.

---

## 7. Production differences

| Ours (one day) | Production |
| --- | --- |
| Callers keep their direct Foundry role | Remove it; only the gateway has the role; private endpoints, so Foundry isn't reachable except through the gateway |
| Two teams in a `choose` | Teams as **API Management products** or a lookup table (named values / a cache), onboarded by pipeline |
| Limits hard-coded | Limits per team as configuration, reviewed like code |
| One region, Basic v2 | Standard v2 / Premium with zones, multiple regions; limits are per gateway, so plan for that |
| Setting changed with `az containerapp update` | A Bicep parameter (`azd provision` resets our manual change) |
| Token metrics only | Plus a monthly chargeback report and budget alerts per team |

---

## 8. Interview questions

**"How do you allocate AI costs to teams and stop one team using up the quota?"** (the Day 15 drill)
> Put an AI gateway in front of the models or agents, and identify every caller by its Entra identity, not a shared
> key. Each team gets a token budget: a per-minute rate against bursts and a daily or monthly quota against overspend.
> The gateway returns 429 or 403 when they're exceeded, so one team's batch job can't starve production. The gateway
> also emits token metrics per team, which turns a shared deployment's bill into chargeback. For high-priority
> traffic, use separate deployments or PTU with spillover. In our setup the gateway sits in front of Foundry agents,
> so it counts per agent turn; for per-model-call control you route the agents' model calls through the gateway too.

**"Can't you just use Azure OpenAI quotas per deployment?"**
> You can, with one deployment per team, but that fragments capacity and wastes quota. A gateway lets teams share
> capacity with fair limits, and gives you identity-based metrics the deployment can't.

**"What can't your gateway do?"**
> See inside agent turns: individual model calls, tool calls, tool results. So it can't stop a runaway turn halfway,
> load-balance model deployments, or check injected content from tool results. Foundry's traces and our alerts cover
> those.

**"Why Entra tokens and not subscription keys?"**
> No secrets. The identity is already the cost key, and removing access is one role assignment. Keys get shared,
> copied into code and forgotten.

---

## 9. AWS comparison

| Azure | AWS |
| --- | --- |
| API Management `llm-token-limit` | API Gateway usage plans count *requests*, not tokens; token limits need a proxy (LiteLLM, a custom Lambda) |
| `llm-emit-token-metric` per team | Bedrock **application inference profiles** with cost-allocation tags per team, plus CloudWatch metrics |
| Gateway in front of Foundry agents | API Gateway in front of Bedrock `InvokeAgent`: the same "sees only the turn" limitation |
| Entra token validation | IAM SigV4 / Cognito JWT authorizer |

---

## 10. Teardown

`scripts/gateway/teardown.sh`, run the same day (2026-11-05):

1. disputes-api's `Foundry__ProjectEndpoint` set back to the direct project endpoint (verified).
2. The gateway's **Azure AI User** role assignment on the project removed (verified gone). A role assignment left
   behind for a deleted identity is an orphan that clutters access reviews.
3. API Management **deleted and purged**. API Management keeps deleted instances for 48 hours (soft delete), still
   holding the name; purging frees it and ends any doubt about billing.

Total running time: about 1 hour. To repeat the experiment: `scripts/gateway/deploy.sh --switch`, then
`teardown.sh`.

> **Interview point:** "Experiments get a teardown script written *before* the deploy, and the resources are tagged
> `delete-after`. Cost control for a platform team is mostly about not leaving things running."
