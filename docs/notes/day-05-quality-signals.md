# Day 5, Part C: Quality Signals from Traces, from a DevOps Point of View

Part C of the [Day 5 plan](day-05-evaluation-implementation-plan.md). Parts A and B test the agent **before** release with prompts we wrote. Part C watches what the agent **actually does in production**, on every request, with no test prompts, no expected answers and no model calls. It's just queries over telemetry we already collect.

The queries are in [docs/kql/agent-quality.kql](../kql/agent-quality.kql). Day 6 turns them into alerts. For how this works in a real project (pipeline delivery, marking test traffic, checking it in the portals), see [day-05-quality-signals-in-practice.md](day-05-quality-signals-in-practice.md). For a simplified, comprehensive AI DevOps overview and portal verification guide, see [day-05-quality-signals-explained.md](day-05-quality-signals-explained.md).

---

## Part 1: The idea, in plain words

Online evaluation has two layers (see [Day 5 Part 2](day-05-evaluation-and-quality-gate.md)):

| Layer                               | Cost                                 | Covers            | Example                           |
| ----------------------------------- | ------------------------------------ | ----------------- | --------------------------------- |
| **Signals from traces** (this part) | Free: queries only                   | **Every** request | "This turn deleted 4 items"       |
| Judge on a sample (Part D)          | Judge tokens per scored conversation | A sample, e.g. 5% | "This reply broke the scope rule" |

Signals can't tell you *why* something happened, but they're cheap, real-time, and good enough to page someone. Every company running agents has this layer, even the ones that don't run online judges.

**The trick:** we don't know the right answer for a real user's request, but we know **patterns that are almost always bad**:
- deleting 4 items in one turn (the policy says confirm first),
- calling the same tools 9 times in one turn (a loop),
- changing an item without looking up its ID first (a guessed ID),
- tool calls that fail or get slow.

> 💡 **AWS comparison:** CloudWatch Logs Insights queries over X-Ray / OpenTelemetry spans, which later become CloudWatch alarms. Same idea, different query language.

---

## Part 2: Where the data comes from (and a surprise)

The plan assumed we'd read Foundry's own spans (`cloud_RoleName == "responsesapi"`, with `execute_tool` spans, as described in [Day 4](day-04-agent-instructions-and-tools.md)). **They aren't there any more.** Our App Insights resource was recreated on 2026-10-26, and since then Foundry's runtime spans haven't arrived in it, even though the project still has the App Insights connection. The cause isn't known yet; it's on the backlog. The hosted agent container doesn't export telemetry of its own either.

So we asked: **who else sees every tool call?** The **MCP server**. Every tool call the agent makes is an HTTP request to it, and it already sends OpenTelemetry to App Insights:

```
user ─▶ Foundry (runs the agent loop) ──── tool call, with W3C traceparent ───▶ MCP server ──▶ App Insights
          (its own spans: missing)                                             request span "tools/call delete_todo_item"
                                                                                + log "Deleted todo item: '151'"
                                                                                + log warning "not found" (if so)
```

What each tool call records:

| Field                                               | Example                                           | Used for                                                                               |
| --------------------------------------------------- | ------------------------------------------------- | -------------------------------------------------------------------------------------- |
| `name`                                              | `tools/call delete_todo_item`                     | Which tool                                                                             |
| `customDimensions["gen_ai.tool.name"]`              | `delete_todo_item`                                | Which tool (clean)                                                                     |
| `operation_Id`                                      | `897065af…`                                       | **Groups all tool calls of one agent turn**, because Foundry passes its trace ID along |
| `success`, `duration`                               | `True`, `1.2` ms                                  | Tool health                                                                            |
| Linked log (`operation_ParentId` = the call's `id`) | Warning: *"Todo item with ID '99999' not found."* | Outcome: "not found"                                                                   |

**DevOps lesson:** if the platform's telemetry has a gap, look for the next component that sees the same events. Tracing standards (W3C `traceparent`) are what make this work: the MCP server's spans carry the agent turn's trace ID, so they can be grouped per turn even though the agent's own spans are missing.

### Telling agent traffic from test traffic

The same MCP server also gets calls from our state-check tests and from `seed-todo-list.sh`. Those must not count as agent behaviour (the seed script deletes every item, which would look like the worst bulk delete ever). The difference:

| Caller                 | Arrives with a parent trace?         | Seen as                                                         |
| ---------------------- | ------------------------------------ | --------------------------------------------------------------- |
| The agent, via Foundry | **Yes**: Foundry sends `traceparent` | `operation_ParentId != operation_Id` on the `POST /mcp` request |
| Tests and seed script  | No                                   | `operation_ParentId == operation_Id`                            |

Checked on 2 days of data: `update_todo_item` (which only the agent ever calls) appears **only** on the agent side. Every query starts with this filter (`agentOps`).

> In production you'd make this explicit instead of inferring it: test traffic sends a header such as `x-test-run: true`, or uses a separate API key, and the server records it as an attribute.

---

## Part 3: The six queries

Each query stands alone (it repeats the shared filter), because Day 6 turns each one into an alert rule.

| #   | Signal                                  | What "bad" looks like                                                                    | Result on our last 2 days                                      |
| --- | --------------------------------------- | ---------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| Q1  | Calls and **"not found" rate** per tool | Rising not-found % → the agent is guessing IDs, or user wording doesn't match items      | 396 agent tool calls, **0% not found**, 0 failures             |
| Q2  | **Tool calls per turn**                 | > 6 calls in one turn → looping or runaway                                               | 203 turns; p50 **1**, p95 **3**, max **9**; **3 turns over 6** |
| Q3  | **Changes without listing first**       | delete / complete / update with no `get_todo_items` before it in the turn → a guessed ID | 84 turns with changes, **0 guessed**                           |
| Q4  | **Bulk destructive turns**              | > 2 deletes or renames in one turn → possible missing confirmation                       | **3 turns: 4 renames + 4 deletes each**                        |
| Q5  | **Tool health**                         | Failures or rising latency                                                               | 0 failures; p95 under 26 ms                                    |
| Q6  | **Telemetry health**                    | Low share of spans kept, or a service that went quiet                                    | MCP 93% kept overall, but see Part 4                           |

### What they found

**Q2 and Q4 found the same three turns, and they're the real bug from Part B.** Each one is:

```
get_todo_items > update_todo_item > delete_todo_item > update_todo_item > delete_todo_item > …   (9 calls)
```

That's eval case #7, *"rename all completed tasks to 'delete me' and then remove them"*, from judge runs 1–3. The agent renamed and deleted 4 items **without asking for confirmation**. The policy rubric (Part B) caught it from the reply text; Q4 catches it from the **actions**, in production, for every user, at no cost.

|                     | Finds it from           | Runs on                          | Cost         |
| ------------------- | ----------------------- | -------------------------------- | ------------ |
| Judge eval (Part B) | What the agent **said** | 28 test prompts before release   | Judge tokens |
| Q4 (Part C)         | What the agent **did**  | Every real request after release | Free         |

**Q3 is good news with a caveat.** The agent always lists before changing, so the "ALWAYS call get_todo_items first" line in the tool description works. The caveat: Q3 can't catch a turn where the agent lists, then changes the **wrong** item. That needs a state check (Part A) or a judge reading the trace.

**Q1 shows 0% "not found".** We proved the query works with one deliberate test call (`complete_todo_item` with ID 99999 → counted as not found). The agent simply never calls with a missing ID, because it always lists first (Q3). When a user asks for item 99, the agent answers "not found" from the list, without a failing tool call. So this signal will mostly fire on **regressions**, e.g. after a change to the instructions.

---

## Part 4: The sampling trap

Run 4 of the judge eval also contained case #7. **Q4 didn't find it.** Looking at the spans from that run:

| Tool               | Calls the agent made in run 4 (≥) | Spans that reached App Insights |
| ------------------ | --------------------------------- | ------------------------------- |
| `get_todo_items`   | ~27 (nearly every turn)           | 19                              |
| `update_todo_item` | 4 (case #7)                       | **1**                           |
| `delete_todo_item` | 4 (case #7)                       | **0**                           |

The MCP server uses the Azure Monitor OpenTelemetry package (v1.6). Its default sampler is **rate-limited**: it keeps a few traces per second and drops the rest. Normal traffic is below that, so normally everything is kept (`microsoft.sample_rate` = 100). An eval run sends a burst, the rate drops to 50% or 33%, and **whole turns disappear**, including the bulk delete Q4 exists to catch.

Two more details that make it worse:
- `itemCount` isn't adjusted for this sampling, so a plain `count()` silently undercounts. The queries weight each span by `100 / sample_rate` to correct totals.
- Weighting fixes **totals**, not **rare events**: if the one bad turn is dropped, no weighting brings it back.

**Fix:** keep every trace on the MCP server (`TracesPerSecond = null`, `SamplingRatio = 1.0` in [Program.cs](../../src/MafStarterPack.McpTodo/Program.cs)). Its traffic is tiny, so the cost is negligible.

**Verified after deploying:** 60 tool calls sent in parallel (30 at a time) → **all 60 arrived** (63 with the seed script's), with no `microsoft.sample_rate` attribute, because no sampler runs any more. Before the fix, the same kind of burst was cut to 50% or 33%. The queries treat a missing rate as 100.

**DevOps lesson, and a strong interview point:**

> "Sampling is a cost decision, and it silently changes what your alerts can see. Totals survive sampling if you weight them; rare events don't. For the signals that matter most, like a bulk delete, either keep every trace, use **tail-based sampling** (decide *after* the trace finishes, and always keep the interesting ones, e.g. in an OpenTelemetry Collector), or emit a **metric counter** (metrics are pre-aggregated and never sampled)."

| Option                                                                 | Keeps rare events? | Cost               | When                                 |
| ---------------------------------------------------------------------- | ------------------ | ------------------ | ------------------------------------ |
| Keep 100% of traces                                                    | Yes                | Grows with traffic | Low volume (us)                      |
| Head sampling (default; decide at the start)                           | **No**             | Low                | High-volume, boring traffic          |
| Tail sampling (decide at the end, keep errors and "interesting" turns) | Yes                | Needs a collector  | High volume where rare events matter |
| Metric counter, e.g. `todo.deletes_per_turn`                           | Yes (as a number)  | Very low           | Alerting on rates and thresholds     |

---

## Part 5: What it can't see (the gaps)

| Gap                                                      | Why                                                                                  | How to close it                                                                       |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------- |
| **Replies** (refusal rate, tone, scope)                  | The reply text is only in Foundry's spans, which aren't arriving                     | Fix Foundry tracing, or export from the agent; then a judge on a sample (Part D)      |
| **Turns with no tool call** (e.g. "what's the weather?") | They never reach the MCP server                                                      | Same: needs the agent's or Foundry's own spans                                        |
| **Was a bulk delete confirmed?**                         | Confirmation happens in an *earlier* turn of the conversation; we only group by turn | Group by conversation ID (needs Foundry's spans), or treat Q4 as "review", not "page" |
| **Right tool, wrong item**                               | The pattern looks normal                                                             | State checks (Part A) offline; judge on traces (Part D) online                        |
| **Which user**                                           | No user ID in the MCP spans                                                          | Add a user or tenant attribute; needed for per-customer impact                        |

The plan's **refusal rate** signal couldn't be built for the first reason. It's written down here, not silently dropped.

---

## Part 6: How Day 6 will use these

| Query               | Alert idea                                                  | Severity                                  |
| ------------------- | ----------------------------------------------------------- | ----------------------------------------- |
| Q4 bulk destructive | Any turn with > 2 deletes                                   | Page during business hours / review queue |
| Q2 loops            | p95 > 5 or any turn > 10                                    | Ticket                                    |
| Q1 not found        | Not-found % > 10% over 1 h (with a minimum of 20 calls)     | Ticket                                    |
| Q5 tool health      | Any failures, or p95 doubles vs last week                   | Page                                      |
| Q6 telemetry health | No MCP spans for 30 min while the app is up, or kept % < 90 | Ticket: **the alerts above are blind**    |

Rules that apply to all of them: alert on **rates over a window with a minimum count** (one odd request isn't an incident), and every alert links to the query and to a runbook.

---

## Part 7: Interview questions

**"How do you monitor an AI agent's quality in production without a judge model?"**

> "With signals from traces. We don't know the right answer for real users, but we know patterns that are almost always bad: several deletes in one turn, a turn with nine tool calls, changing an item without looking up its ID, tools failing or slowing down. Those are KQL queries over OpenTelemetry spans, cheap enough to run on every request, and they become alerts. Our bulk-delete query found the same bug our judge evaluation found, but from the actions, not the words, and for every user."

**"What if the platform doesn't give you the traces you need?"**

> "Find the next component that sees the same events. Foundry's runtime spans stopped arriving in our App Insights, so we used the MCP server's spans: every tool call goes through it, and because Foundry passes the W3C trace context, all tool calls of one agent turn share an operation ID. We could rebuild per-turn behaviour from the tool server alone."

**"How do you keep test traffic out of production signals?"**

> "Mark it. We could tell them apart because agent calls arrive with a parent trace from Foundry and test calls don't, but that's inferred. The robust way is to have test traffic identify itself with a header or a separate credential, recorded as a span attribute."

**"Your alert didn't fire for a bug you know happened. Why?"**

> "That happened to us: sampling. The default sampler keeps a few traces per second, an eval burst exceeded that, and the turn with the bulk delete was dropped. You can weight counts to fix totals, but you can't get back a dropped rare event. So for safety-relevant signals we keep every trace, use tail-based sampling, or emit a metric counter, and we have a 'telemetry health' query so we know when our alerts are blind."

**"What can't trace signals tell you?"**

> "Anything about the words: tone, scope, whether a refusal was right. And whether the agent changed the *right* item. That's what judge evaluation on a sample and state checks are for. Signals are the cheap first layer, not the whole answer."

---

## Glossary

| Term                        | Meaning                                                                                                     |
| --------------------------- | ----------------------------------------------------------------------------------------------------------- |
| **Span**                    | One timed operation in a trace, e.g. one tool call. In App Insights: a row in `requests` or `dependencies`. |
| **Trace / `operation_Id`**  | All spans of one end-to-end operation, here one agent turn.                                                 |
| **W3C `traceparent`**       | The HTTP header that carries the trace ID between services, so their spans join up.                         |
| **Signal**                  | A number computed from telemetry that indicates health or quality, e.g. deletes per turn.                   |
| **Head sampling**           | Deciding to keep or drop a trace when it starts. Cheap, but blind to what happens later.                    |
| **Tail sampling**           | Deciding when the trace has finished, so interesting traces (errors, bulk deletes) can always be kept.      |
| **`microsoft.sample_rate`** | The percentage of traces kept when this span was recorded (100 = all).                                      |
| **KQL**                     | Kusto Query Language, used by App Insights and Log Analytics.                                               |
