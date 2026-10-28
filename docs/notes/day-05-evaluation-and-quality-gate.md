# Day 5: Evaluation and a Quality Gate in CI

Notes for Day 5 of the [prep plan](../ai-platform-engineer-prep-plan.md) ("Evaluation and a quality gate in CI").

**Today's plan:**
1. **Learn** the three ideas every evaluation setup is built on: offline vs online evaluation, judge-model evaluators, and state-based checks. *(This page, Parts 1–4.)*
2. **Build** a test set, state-check tests, and a pipeline step that blocks a bad deploy. *(Filled in as we build it.)*

---

## Part 1: Why agents need a different kind of testing

### Normal code vs an agent

With normal code, a unit test is simple: same input, same output, every time.

```
add(2, 3)  →  5      assert result == 5     ✅ always passes or always fails
```

An agent breaks this in three ways:

| Problem | Example in our project |
|---|---|
| **The output is text, and many answers are correct** | "Done, item 4 is complete." and "Marked 'buy milk' as finished ✅" are both right. `assert reply == "..."` would fail one of them. |
| **The same input can give different outputs** | Run "I finished buy milk" 10 times and the wording changes every time. Rarely, the model might even pick a different tool. |
| **It can break without any code change** | Someone edits a tool description (Day 4), Microsoft updates the model version, or the deployment is switched to a cheaper model. Our C# code is the same, but behaviour changes. |

So instead of "pass or fail on one run", agent testing asks: **"over many test cases, how often does it do the right thing, and is that rate going down?"** That number is the **pass rate**, and it's what we'll put a gate on.

### What "correct" means for an agent

An answer can be wrong in different ways, so we measure more than one thing:

| Question | Example of failing it | How we check it |
|---|---|---|
| Did it do the **right action**? | Deleted "buy milk" when the user said "I finished buy milk". | State check (Part 4), tool-call check |
| Did it call the **right tools, with the right arguments**? | Called `complete_todo_item(id=7)` without listing first, guessed the ID. | Tool-call check from traces (Day 4, Part 6) |
| Is the **reply** right and useful? | Says "Done!" but the tool returned an error. Or answers the question it was not asked. | Judge model (Part 3) |
| Is it **safe**? | Helps with an off-topic or harmful request. | Judge model with a safety evaluator |

> 💡 **AWS comparison:** this is like the difference between a **unit test** and a **canary** in CodeDeploy. You don't check one exact output; you check that error rates stay below a threshold. Evaluation does the same for answer quality.

---

## Part 2: Offline vs online evaluation

The two names only describe **when, and on what data**, you score the agent.

### Offline evaluation: before release, on a fixed test set

You keep a file of test cases (a **test set** or **golden dataset**). Each case is a prompt plus what a correct result looks like. Before you ship, you run every case against the new version and score the results.

```
 test set (25 prompts)                     new agent version
 ┌──────────────────────────────┐          (staging / dev)
 │ "add buy milk"               │ ──────────▶  run each prompt ──▶ score each result
 │ "I finished buy milk"        │                                       │
 │ "delete everything"          │                                       ▼
 │ "what's the weather?"        │                     pass rate 24/25 = 96%  ≥ 90%?
 │ ...                          │                          ✅ deploy continues
 └──────────────────────────────┘                          ❌ pipeline fails
```

- **When:** in the CI/CD pipeline, after deploying to dev/staging, **before** production.
- **Data:** cases you wrote and checked by hand. You know the right answer for each.
- **Good at:** catching a **regression**: "this prompt change made it worse". It is repeatable, so you can compare version 3 against version 4 on the same cases.
- **Weak at:** it only knows the cases you thought of. Real users say things you never imagined.

> 💡 **AWS / DevOps comparison:** offline evaluation is the **integration test stage** in CodePipeline. Same place in the pipeline, same job: block a bad build before prod.

### Online evaluation: after release, on real traffic

You score what's happening in production, continuously. Because real traffic has no "expected answer", you can't compare to a known right answer. Instead you use:

- **Judge models on a sample** of real conversations (e.g. score 5% of conversations every hour). Foundry can run evaluators on live agent traffic like this.
- **Signals from the traces** that need no judge at all: tool error rate, the number of tool calls per request (a loop shows up as 10+), "Not found" results, latency, tokens.
- **User feedback:** thumbs up/down in the UI, or "the user asked the same thing again" (a sign the first answer was wrong).

```
 real users ──▶ agent (prod) ──▶ traces in App Insights
                                        │
                     ┌──────────────────┼──────────────────┐
                     ▼                  ▼                  ▼
              judge 5% sample    tool error rate     thumbs down %
                     │                  │                  │
                     └──────▶ dashboard + alert (Day 6) ◀──┘
                              "quality score dropped 15% today"
```

- **When:** all the time, in production.
- **Data:** real conversations. No expected answers.
- **Good at:** catching what offline tests miss: new kinds of questions, **drift** (quality slowly getting worse after a model update), and problems only real data shows.
- **Weak at:** it finds problems **after** users have already seen them. And sending real conversations to a judge model costs money, so you only sample.

> 💡 **AWS / DevOps comparison:** online evaluation is **CloudWatch alarms and synthetic canaries on production**. You don't stop the deploy; you notice and roll back.

### Side by side

| | Offline | Online |
|---|---|---|
| When | Before release (CI/CD) | After release (production) |
| Data | Fixed test set you wrote | Real user traffic |
| Expected answer known? | Yes | No |
| Catches | Regressions from your changes | New question types, drift, real-world failures |
| Action when it fails | Block the deploy | Alert, investigate, roll back |
| Cost | Small, fixed (25 cases per deploy) | Grows with traffic, so you sample |
| DevOps equivalent | Integration test stage | Monitoring and alerts |

### How they feed each other (the loop interviewers like)

```
   online finds a bad real conversation
            │
            ▼
   add it to the offline test set (with the right answer)
            │
            ▼
   fix the prompt / tool description
            │
            ▼
   offline eval proves the fix, and keeps it fixed forever
```

**Every production failure becomes a new test case.** This is exactly how you'd turn a production incident into a regression test for normal code. The test set grows from 25 cases to hundreds over time, and each one is a real mistake that can never come back silently.

---

## Part 3: Judge-model evaluators ("LLM-as-a-judge")

### The idea

You can't `assert` on free text, but another model can read it and grade it, just like a teacher marking an essay with a rubric.

```
 ┌───────────────────────────────────────────────────────────────┐
 │  JUDGE PROMPT (the evaluator)                                 │
 │                                                               │
 │  You are grading a to-do assistant.                           │
 │  User asked:     "I finished buy milk"                        │
 │  Tools called:   get_todo_items, complete_todo_item(id=4)     │
 │  Agent replied:  "Done, marked item 4 'buy milk' as complete."│
 │                                                               │
 │  Did the agent do what the user asked?                        │
 │  Score 1 (not at all) to 5 (fully). Explain why.              │
 └───────────────────────────────────────────────────────────────┘
                              │
                              ▼
            judge model ──▶  { "score": 5, "reason": "Correctly completed
                                the item instead of deleting it..." }
                              │
                              ▼
                     score ≥ 3 → pass for this case
```

The **evaluator** is that prompt plus the code that parses the score. You run it for every test case, and the **pass rate** is how many cases scored at or above the threshold.

### Common built-in evaluators

You rarely write the judge prompt yourself. Foundry ships ready-made evaluators. These are the ones that matter for an agent like ours:

| Evaluator | Question it answers | Example failure it catches |
|---|---|---|
| **Intent resolution** | Did the agent understand what the user wanted? | User said "I finished X", agent thought they wanted to add X. |
| **Task adherence** | Did it follow its instructions and finish the task? | Deleted without confirming when the instructions say to confirm "delete everything". |
| **Tool call accuracy** | Were the right tools called with the right arguments? | Called `delete_todo_item` instead of `complete_todo_item`. Guessed an ID. |
| **Relevance** | Does the reply answer the question asked? | Lists the whole todo list when asked to add one item. |
| **Coherence / fluency** | Is the reply clear and readable? | Rambling, contradictory reply. |
| **Safety** (violence, hate, self-harm, etc.) | Is anything harmful in the reply? | Agent produced harmful content when pushed. |

The first three are the **agent-specific** ones. Relevance, coherence and safety apply to any chatbot.

### Why trust a model to grade a model?

Interviewers will ask this. Honest answer: **you trust it only after you've checked it**.

| Weakness | What you do about it |
|---|---|
| The judge can be wrong, too. | **Calibrate:** grade 20–30 cases by hand, run the judge on the same cases, and check they mostly agree. If not, fix the rubric. |
| Scores vary from run to run. | Use a strong model as judge, temperature 0 where possible, a clear 1–5 rubric with an example per score, and ask for the reason **before** the score. |
| Judges tend to like long answers and answers that sound confident. | Rubric says "length does not matter; a short correct answer scores 5". |
| A judge grading its own model's output may go easy on it. | Use a different (usually larger) model as the judge than the agent's model. Our agent uses `gpt-5-mini`; the judge should be a stronger model. |
| It costs tokens: every case is an extra model call, sometimes several. | Fine for 25 cases per deploy. In production, sample (e.g. 5%), don't judge everything. |

> 💡 **The key point for the interview:** a judge model is a **measuring instrument**, and like any instrument you calibrate it before you trust its readings.

### The weakness that matters most for our agent

A judge only reads **text**. It sees the agent's reply "Done, item 4 is complete." It can't see whether item 4 is **actually** complete in the database. If the tool silently failed but the agent claimed success, a judge reading the reply would give it 5/5.

That's the gap state-based checks close.

> 💡 **AWS comparison:** Amazon Bedrock Evaluations has the same idea under the name "LLM-as-a-judge", with built-in metrics like correctness, completeness and harmfulness.

---

## Part 4: State-based checks

### The idea

**Don't grade what the agent *said*. Check what the agent *did*.**

Our agent's whole job is to change a to-do list. So the most reliable test is: set up the list, send the prompt, then **read the list directly from the MCP server** and check it's in the state you expect.

```
 1. ARRANGE   reset the list → add "buy milk" (ID 1), "call mum" (ID 2)       ── directly via MCP
 2. ACT       send "I finished buy milk" to the agent                         ── via the gateway
 3. ASSERT    call get_todo_items directly on the MCP server:
                 item 1 "buy milk"   Completed: True   ✅  (completed, not deleted)
                 item 2 "call mum"   Completed: False  ✅  (untouched)
                 item count == 2                       ✅  (nothing deleted, nothing added)
```

The agent's reply text doesn't matter at all here. This is the agent equivalent of an **integration test that checks the database** after calling an API.

### What state checks catch that judges miss

| Prompt | Agent's reply | Judge model says | State check says |
|---|---|---|---|
| "I finished buy milk" | "Done!" | ✅ 5/5, sounds right | ❌ item was **deleted**, not completed |
| "Add buy milk" | "Added buy milk." | ✅ 5/5 | ❌ two items were added (it called the tool twice) |
| "Delete call mum" | "Deleted call mum." | ✅ 5/5 | ❌ wrong item deleted: ID 1 instead of ID 2 |
| "Delete everything" | "Are you sure? This will delete 2 items." | ✅ 5/5 | ✅ list unchanged, as the instructions require |

The first three are the exact silent failures Day 4 worried about. Only the state check sees them.

### Strengths and limits

| Strengths | Limits |
|---|---|
| **Exact:** pass/fail, no judgment involved. | Only works when the agent **changes something you can read** (a database, a ticket, a file). |
| **Free:** no judge model, no extra tokens. | Says nothing about the reply's quality: a correct action with a rude or confusing reply still passes. |
| **Can't be fooled** by a confident-sounding reply. | Needs **test isolation**: a clean starting state for each test (see below). |
| Easy to explain: it's just an integration test. | Can't handle "many correct answers" well: if the user says "add milk", is "milk" or "Milk" correct? Write the check loosely (case-insensitive, contains). |

### The isolation problem in our project (a real DevOps issue)

State checks need a known starting state. Our MCP server keeps the list **in memory** and **shared by everyone**. That causes two problems:

1. Tests running at the same time would see each other's items.
2. Running tests against **production** would change real users' data.

For today: run state checks only against the **dev** environment, one test at a time, and clear the list before each test. The proper fix is Day 3 (durable data), which was skipped: a real database with a separate list per user or per test, so each test gets its own empty list. **Mention this trade-off in the interview**; it shows you've thought about it.

---

## Part 5: How the three fit together

None of the three is enough alone. A real setup uses all of them, each for what it's good at:

| Check | Answers | Where it runs | Cost |
|---|---|---|---|
| **State check** | Did it *do* the right thing? | Offline (CI, against dev) | Free, exact |
| **Tool-call check** (from traces) | Did it take the right *steps*? | Offline and online | Free (just a KQL query) |
| **Judge model** | Did it *say* the right thing, safely? | Offline (every case) and online (sampled) | Tokens per case |
| **Trace signals** (error rate, loops) | Is something broken *right now*? | Online | Free |
| **User feedback** | Do users think it was right? | Online | Free, but slow and sparse |

### What the pipeline gate looks like

```
 stage 1: MCP tier ─▶ stage 2: Foundry tier (new agent version in dev)
                                     │
                                     ▼
                     ┌─────────── stage 2b: EVALUATION GATE ──────────┐
                     │  run 25 test cases against the new version     │
                     │   • state checks         must be 100%          │
                     │   • judge pass rate      must be ≥ 90%         │
                     │   • tool-call accuracy   must be ≥ 90%         │
                     └────────────────────┬───────────────────────────┘
                              pass ✅      │       ❌ fail
                                 ▼                  ▼
                       stage 3: app tier      pipeline stops;
                                              prod keeps the old version
```

Why the different thresholds? State checks are exact, so any failure is a real bug: 100%. Judges are noisy, so a single case flipping between 4 and 2 shouldn't block a deploy: allow a small margin, and compare to the last version's score rather than a fixed number when you can.

---

## Part 6: What we built

The step-by-step plan is in [day-05-evaluation-implementation-plan.md](day-05-evaluation-implementation-plan.md).

- [x] Step 0: judge model, pipeline role, tool versions.
- [x] Part A: state-check tests that talk to the agent and to the MCP server directly.
- [ ] Part B: test set and judge evaluators (`azd ai agent eval`).
- [ ] Part E: pipeline step after stage 2 that fails the deploy when checks fail.

### Step 0: Getting the platform ready

Before writing any test, the platform needed three things. None of them is "test code"; they're the infrastructure the tests stand on.

**1. A judge model: `gpt-4o`, as code.** Added as a second model deployment in `resources-foundry/azure.yaml`, next to the agent's `gpt-5-mini`.
- It's listed **second** on purpose: azd takes the *first* deployment as `AZURE_AI_MODEL_DEPLOYMENT_NAME`, the agent's own model. Reordering the list would silently switch the agent to gpt-4o.
- GlobalStandard deployments are **pay per token**, so an idle judge costs nothing. Capacity is only a rate limit (see the 429 story below).
- *AWS equivalent:* enabling a second Bedrock model for your account, except here it's declared in IaC.

**2. The pipeline's identity can call the agent.** The pipeline service principal had Contributor and RBAC Administrator, which are **control plane** only: it could *create* the Foundry project, but not *talk to* the agent. Calling an agent is **data plane**, which needs **Azure AI User**.
- Added to Bicep (`resources-foundry/infra/core/ai/ai-project.bicep`) as an optional `pipelinePrincipalId`, fed from `AZURE_PIPELINE_PRINCIPAL_ID`.
- **The trap:** the Bicep already gives Azure AI User to *whoever deploys* (`principalId`). In CI the deployer *is* the pipeline, so a second assignment of the same role to the same principal would fail with "role assignment already exists". The new grant has a condition: only if `pipelinePrincipalId != principalId`.
- **Gotcha:** the portal and `az role assignment list` now show this role as **"Foundry User"** (Microsoft renamed the display name). The role ID, `53ca6127-…`, is unchanged. Search by the ID, not the name.
- *AWS equivalent:* a CodeBuild role that can deploy a Bedrock agent but not `InvokeAgent` until you add that action.

**3. Updated, pinned tools.** azd 1.36 → 1.37.1, and the agents extension beta.21 → beta.22 (the new extension refused to run on the old azd). In `deploy.yml` both versions are now **pinned** (`AZD_VERSION`, `AZD_AGENTS_EXTENSION_VERSION`). A beta extension can change behaviour any week, and you don't want your quality gate to change without a commit.

**A stale-state trap, again.** The first `azd provision` after adding gpt-4o **didn't create it**. The azd env still held `AI_PROJECT_DEPLOYMENTS` from an earlier run (only gpt-5-mini), and the extension fills that value from `azure.yaml` **only when it's empty**. The fix was `azd env unset AI_PROJECT_DEPLOYMENTS --force`, then provision again. CI isn't affected, because it starts from a fresh env each run. The lesson is the same as the ACR connection on Day 2: **values cached in `.azure/<env>/.env` can override your IaC**. When an IaC change "doesn't apply", check the env file first.

### Part A: How the state checks work

```
tests/MafStarterPack.AgentEvals/
  EvalSettings.cs      all config from environment variables; MCP key read from Key Vault
  AgentClient.cs       sends one prompt to the deployed todo-agent (new conversation every time)
  TodoStateClient.cs   talks to the MCP server directly: list, add, delete
  StateCheckTests.cs   the 8 cases, plus the run/retry logic
  README.md            how to run it, which roles you need
```

Each case is **Arrange, Act, Assert**:

```
Arrange  TodoStateClient → MCP server:  delete every item, then add the seed items
Act      AgentClient     → Foundry:     "I finished buy milk"   (the agent does its own MCP calls)
Assert   TodoStateClient → MCP server:  get_todo_items → is 'buy milk' completed, and nothing deleted?
```

**Design decisions, from a DevOps point of view:**

| Decision | Why |
|---|---|
| **Call Foundry directly, not the gateway** | The gate runs after stage 2, before stage 3 deploys the gateway. On a fresh environment the gateway doesn't exist yet. Same library as the gateway (`ProjectOpenAIClient`), so it's the same path minus one hop. |
| **Assert on the list, never on the reply text** | Many replies are correct. The list's final state is a fact. |
| **Loose text checks** ("contains milk", case-insensitive) | The model may write "Buy milk" or "buy milk". Check meaning, not spelling. |
| **All settings from environment variables** | The same command runs on a laptop and in GitHub Actions. That's [12-factor](https://12factor.net/config) config, same as the apps. |
| **Missing settings fail the run; they don't skip it** | A skipped gate looks green but proves nothing. Fail closed, like the MCP API key on Day 2. |
| **MCP key read from Key Vault at test time** | No secret in GitHub, in a file, or in test code. The test runs as you locally, or as the pipeline in CI; both already have vault access. |
| **Each case runs until 2 passes or 2 fails** (max 3 runs) | The model is not deterministic. One wobble shouldn't fail a deploy; a real regression fails every run. |
| **An agent error is not a quality result** | See the 429 story below. Errors are retried after a pause and, if they persist, the test fails with an `INFRASTRUCTURE:` label, so on-call knows it's capacity, not prompts. |
| **Tests run one at a time** (`xunit.runner.json`) | One shared in-memory list. Two tests at once would wipe each other's data. |
| **Exit code 2 on failure** | That's what makes a pipeline step go red. Nothing else is needed for the gate. |

**Running it:**

```bash
export AZURE_AI_PROJECT_ENDPOINT=$(cd resources-foundry && azd env get-value AZURE_AI_PROJECT_ENDPOINT)
export AZURE_RESOURCE_MCP_TODO_FQDN=$(cd resources-foundry && azd env get-value AZURE_RESOURCE_MCP_TODO_FQDN)
export AZURE_KEY_VAULT_ENDPOINT=$(cd resources-foundry && azd env get-value AZURE_KEY_VAULT_ENDPOINT)
dotnet test --project tests/MafStarterPack.AgentEvals --output Detailed
```

`--output Detailed` prints, for every run, the prompt, the agent's reply, and the list's final state. A failed case shows exactly what the agent did, without opening App Insights.

(xunit v4 runs on Microsoft.Testing.Platform, the new .NET test runner. .NET 10's `dotnet test` needs `"test": { "runner": "Microsoft.Testing.Platform" }` in `global.json` to use it.)

### What the first runs taught us

#### 1. The first failures were rate limits, not wrong answers

The first full run: **7 of 8 passed**. "Rename" failed 2 of 3 runs: the list still said 'buy milk'. It looked like the model ignoring the rename. Logging the response content showed the real cause:

```
ErrorContent  code=server_error
msg=Model deployment rate limit exceeded. Your requests to gpt-5-mini ... have exceeded token rate limit.
```

- `gpt-5-mini` was deployed with **capacity 10 = 10,000 tokens per minute**.
- One agent turn makes 2–3 model calls, and **each call resends the instructions and all 5 tool definitions** (Day 4). gpt-5-mini's hidden reasoning tokens count too. Eight test cases back to back went straight through 10K TPM.
- Foundry hit the 429 **in the middle of the tool loop**, stopped, and returned an HTTP 200 with an error item inside. The list was left half-done.

**The fixes:**
1. **Capacity 10 → 100** in `azure.yaml` (quota allowed 1000). Pay-per-token, so no idle cost. The final run had zero 429s.
2. **The tests tell errors apart from wrong answers** (`AgentRunFailedException`). Before this, a capacity problem looked exactly like a prompt regression.

> 💡 **Interview point:** *"Our first eval failures were actually 429s. The agent's tool loop got rate-limited half-way and left the data half-changed. So the eval harness has to separate 'the agent ran and was wrong' from 'the agent couldn't run'. Otherwise a capacity problem gets blamed on the prompt, or worse, a prompt regression gets dismissed as flakiness. And it showed our TPM was sized for one chat user, not for a test burst."* This also applies to production: **429s inside a tool loop can leave real data half-changed.** That's an alert for Day 6.

#### 2. Breaking it on purpose: the gate works

To prove the gate catches a real regression, we changed the `complete_todo_item` description to *"Deprecated, do not use… remove it with delete_todo_item instead"* and redeployed the MCP tier:

```
Run 1: prompt: I finished buy milk
  reply: Done — I removed the finished item.        ← sounds fine; a judge would score it well
  state: 12 'call mum'                               ← 'buy milk' was DELETED
  result: FAIL
Run 2: ... same ...
Test run summary: Failed!   exit code 2
```

The reply sounds reasonable; only the state check sees the damage. Then we restored the description, redeployed, and the suite went back to 8/8.

#### 3. The surprise: Foundry caches the MCP tool list

The restore didn't work straight away. The MCP server served the fixed description immediately (checked with a direct `tools/list` call), but **the agent kept deleting for about 12 more minutes**, even after deploying a brand-new agent version. Asking the agent to describe its own tool proved it was still seeing the broken text.

App Insights explained it. On the MCP server, over ~15 minutes there were dozens of `initialize` calls but only a handful of `tools/list` calls:

| Time (UTC) | What happened |
|---|---|
| 03:38 | Foundry fetches `tools/list` (good descriptions) |
| ~03:53 | Broken description deployed. **Tests still pass**: Foundry is using its cached good list |
| 03:55 | Foundry fetches `tools/list` again, gets the **broken** list. Deletes start |
| 03:58 | Good description restored on the MCP server |
| 03:59–04:10 | Agent **still deletes**, including on a new agent version |
| ~04:11 | Cache refreshed, agent sees the good description. Suite: 8/8 |

So Foundry **caches the MCP tool list for roughly 10–15 minutes** (our measurement; the exact time isn't documented), keyed to the MCP server rather than the agent version. This corrects the Day 4 notes, which assumed the list was fetched on every request.

**What this means for the pipeline:**
- **After stage 1 changes tool descriptions, the gate must wait for the cache** (or check that the agent sees the new description) before evaluating. Otherwise it tests the *old* descriptions, and a broken change can pass the gate. That's exactly what our first run after breaking it did.
- **Rolling back a bad description takes up to ~15 minutes to reach users.** For an urgent fix, removing the tool from `allowed_tools` in the agent (stage 2) is faster.
- **How we found it is the lesson:** we had the MCP server's own request telemetry (`tools/list` vs `tools/call` counts) and could compare what the server served with what the model saw. Without traces on the tool server, this would have looked like a random flaky test.

> 💡 **Interview point:** *"We deliberately broke a tool description to prove the gate catches it, and it did. Then the rollback didn't take effect for about 12 minutes, because Foundry caches the MCP tool list. We proved that from the MCP server's own telemetry: dozens of sessions but only two `tools/list` calls. So the gate has to wait out the cache after a tool change, and a description rollback isn't instant."*

#### 4. Things the state checks can't see (Part B's job)

Several passing runs had replies like *"Done — I deleted the item."* The instructions ask for the item's ID and text in every reply, so the state was right but the reply broke the rules. State checks don't look at replies; that's what the judge-model evaluators (Part B) are for.

---

## Part 7: Interview drill

**"How do you know the agent's answers are correct in production?"**

> "Three layers. Before release, every deploy runs an offline evaluation in the pipeline: a test set of around 25 cases, scored three ways. State checks look at what the agent actually changed, for example that 'I finished buy milk' *completed* the item rather than deleting it. Tool-call checks read the traces to confirm the right tools ran in the right order. And a judge model grades the reply for intent, task adherence and safety. If the pass rate drops below the threshold, the deploy stops and prod keeps the old version.
>
> After release, I can't know the right answer for real traffic, so I watch signals instead: a judge scores a sample of real conversations, and alerts fire on tool error rate, tool-call loops, and thumbs-down rate. When something bad turns up in production, it becomes a new case in the offline test set, so the same mistake can't come back silently.
>
> And I calibrate the judge against hand-graded cases before trusting it, because the judge is a model too."

**Follow-ups to be ready for:**

| Question | Short answer |
|---|---|
| "Why not just use a judge for everything?" | It only reads text. It can't see that the agent deleted instead of completed if the reply says "Done". State checks can. |
| "Why not just state checks?" | They say nothing about the reply's quality or safety, and they only work when there's state to read. |
| "The model gives different answers each run. Isn't your gate flaky?" | Score many cases and gate on a rate, not on one answer. Use thresholds with a margin, or compare to the previous version's score. Run flaky cases 3 times and take the majority. |
| "What triggers a re-evaluation besides a code change?" | A prompt or tool description change, a model version update, switching models, or a new MCP tool. All of them change behaviour without changing C# code, so all of them must go through the gate. |
| "How do you evaluate in prod without the right answers?" | Judges on a sample, trace signals, user feedback, and production failures turned into offline cases. |

---

## Glossary

| Term | Meaning |
|---|---|
| **Evaluation (eval)** | Scoring an agent's behaviour on many cases, rather than one pass/fail test. |
| **Test set / golden dataset** | The fixed list of prompts and expected results used for offline evaluation. |
| **Offline evaluation** | Scoring before release, on the test set. Runs in CI/CD. |
| **Online evaluation** | Scoring after release, on real traffic. Runs continuously, usually on a sample. |
| **Evaluator** | Something that scores one result: a judge prompt, a state check, or a rule. |
| **Judge model / LLM-as-a-judge** | A model asked to grade another model's output against a rubric. |
| **Rubric** | The grading instructions given to a judge: what a 1, 3 or 5 looks like. |
| **Calibration** | Checking that a judge's scores agree with human grades before trusting it. |
| **State-based check** | Checking the system's state (our to-do list) after the agent acts, instead of its reply. |
| **Pass rate** | The share of test cases that passed. What the pipeline gate checks. |
| **Regression** | Something that used to work and now doesn't, after a change. |
| **Drift** | Quality slowly changing over time with no change on your side, e.g. after a model update or as user questions change. |
| **Quality gate** | A pipeline step that stops the deploy when the pass rate is below the threshold. |
