# Day 5: Implementation Plan for Evaluation

How we'll add offline evaluation, online evaluation, judge-model evaluators and state-based checks to this project. The concepts are explained in the [Day 5 notes](day-05-evaluation-and-quality-gate.md); this page is the build plan.

---

## The target picture

```
                        ┌──────────────────── OFFLINE (every deploy, in CI) ─────────────────────┐
                        │                                                                         │
 stage 1: MCP ─▶ stage 2: Foundry ─▶ stage 2b: EVALUATE ─────────────────────▶ stage 3: apps     │
                 (new agent ver.)     │                                         (only if 2b ✅)   │
                                      ├─ A. state checks    (C# tests → agent + MCP)  100% pass   │
                                      └─ B. judge evaluators (azd ai agent eval run)  ≥ 90% pass  │
                        └─────────────────────────────────────────────────────────────────────────┘

                        ┌──────────────────── ONLINE (production, all the time) ─────────────────┐
                        │  traces in App Insights ──▶ C. KQL quality signals ──▶ alerts (Day 6)  │
                        │  live traffic sample    ──▶ D. judge on a sample   ──▶ dashboard       │
                        │  bad real conversation  ──▶ E. added to the test set (feedback loop)   │
                        └─────────────────────────────────────────────────────────────────────────┘
```

| Part | What                                            | Type    | Gate or alert     |
| ---- | ----------------------------------------------- | ------- | ----------------- |
| A    | State-based checks                              | Offline | Blocks the deploy |
| B    | Judge-model evaluators on a test set            | Offline | Blocks the deploy |
| C    | Quality signals from traces (KQL)               | Online  | Alerts            |
| D    | Judge evaluators on sampled live traffic        | Online  | Dashboard / alert |
| E    | Feedback loop: production failures → test cases | Process | —                 |

---

## Step 0: Prerequisites and decisions

| #   | Item                                     | Why                                                                                                                                                                                                         | Plan                                                                                                                                                                                                     |
| --- | ---------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0.1 | **A judge model deployment**             | The judge should be stronger than, and different from, the agent's `gpt-5-mini` (Day 5 notes, Part 3).                                                                                                      | Add a second model deployment to the Foundry Bicep (`resources-foundry/infra`) as a parameter, e.g. `AZURE_AI_EVAL_MODEL_DEPLOYMENT_NAME`. Pick the model from what's available in the region and quota. |
| 0.2 | **Pipeline identity can call the agent** | The gate runs as the pipeline service principal (`sp-enterprise-foundry-agent-github`). It has Contributor, which covers the **control plane** only. Calling the agent and running evals is **data plane**. | Grant the pipeline SP **Azure AI User** on the Foundry project in Bicep (same pattern as the gateway identity in Day 2).                                                                                 |
| 0.3 | **Update the tools**                     | The `azure.ai.agents` extension is a beta (installed beta.21, beta.22 available). Eval commands are new.                                                                                                    | `azd extension update azure.ai.agents` locally, and pin the version in the pipeline so a beta release can't break the gate.                                                                              |
| 0.4 | **Where tests point**                    | State checks change data. Our MCP server uses an **in-memory SQLite database shared by all users**.                                                                                                         | Run state checks against **dev only**, one at a time (Step A.4). Never against prod.                                                                                                                     |

---

## Part A: State-based checks (offline)

**Goal:** prove what the agent *did*, by reading the to-do list straight from the MCP server after each prompt.

### A.1 Project layout

A new xUnit test project, in C# like the rest of the repo:

```
tests/
  MafStarterPack.AgentEvals/
    MafStarterPack.AgentEvals.csproj
    AgentClient.cs        # sends a prompt to the todo-agent (Foundry responses endpoint)
    TodoStateClient.cs    # reads/writes the list directly via MCP (ModelContextProtocol client + x-api-key)
    StateCheckTests.cs    # the test cases
```

Configuration comes from environment variables, so the same tests run locally and in CI:

| Variable         | Source                                                 |
| ---------------- | ------------------------------------------------------ |
| `AGENT_ENDPOINT` | Stage 2 output (the Foundry agent responses endpoint)  |
| `MCP_TODO_URL`   | Stage 1 output                                         |
| `MCP_API_KEY`    | Read from Key Vault at runtime, never stored in GitHub |

### A.2 How one test works

```
Arrange   TodoStateClient: delete every item, then add the seed items
Act       AgentClient: send the prompt, wait for the final reply
Assert    TodoStateClient: get_todo_items → compare to expected state
```

We call the **Foundry agent endpoint directly**, not the gateway, because in stage 2b the gateway (stage 3) hasn't been deployed yet. On a fresh environment it doesn't exist at all.

### A.3 First test cases (8)

| #   | Seed list           | Prompt                                | Expected state                               |
| --- | ------------------- | ------------------------------------- | -------------------------------------------- |
| 1   | empty               | "add buy milk"                        | 1 item, text contains "milk", not completed  |
| 2   | empty               | "add buy milk, call mum and pay rent" | 3 items                                      |
| 3   | buy milk, call mum  | "I finished buy milk"                 | 2 items; milk **completed**, mum not         |
| 4   | buy milk, call mum  | "delete call mum"                     | 1 item: buy milk, unchanged                  |
| 5   | buy milk            | "rename buy milk to buy oat milk"     | 1 item, text contains "oat milk"             |
| 6   | buy milk, call mum  | "delete everything"                   | **unchanged** (the agent must confirm first) |
| 7   | buy milk, buy bread | "I'm done with the buy item"          | **unchanged** (ambiguous → it must ask)      |
| 8   | buy milk            | "what's the weather?"                 | **unchanged** (out of scope)                 |

Checks are loose where wording varies: case-insensitive "contains", never exact text.

### A.4 Handling randomness and isolation

- **Flaky cases:** each case runs up to 3 times; it passes if 2 of 3 pass. A real failure fails all three.
- **Isolation:** tests run one at a time (xUnit `[Collection]` with parallelism off), and the pipeline's `concurrency:` group stops two runs from overlapping.
- **The real fix** is Day 3 (durable data with a list per user). After that, each test gets its own empty list and can run in parallel, even in staging.

### A.5 Done when

- `dotnet test tests/MafStarterPack.AgentEvals` passes locally against dev.
- Breaking the agent on purpose fails a test. For example, swap the `complete_todo_item` description to say "deletes the item" and test 3 should fail.

---

## Part B: Judge-model evaluators (offline)

**Goal:** score the agent's replies and tool use on a test set, using Foundry's built-in evaluators.

### B.1 Build the test set

```bash
cd resources-foundry
azd ai agent eval generate \
  --agent todo-agent \
  --gen-instruction-file evals/agent-description.md \
  --eval-model <judge deployment from 0.1> \
  --max-samples 30
```

- `evals/agent-description.md` describes what the agent should do (taken from the instructions in `HostedAgent/Program.cs`).
- Generating writes `eval.yaml` plus a dataset. **Review every case by hand**: delete weak ones, fix wrong expectations, and keep 20–30.
- Make sure the set covers each category below. Add any missing ones by hand:

| Category           | Cases | Examples                                                                                 |
| ------------------ | ----- | ---------------------------------------------------------------------------------------- |
| Add                | 4     | single, several at once, odd wording ("note down…")                                      |
| List               | 3     | "what's on my list", empty list                                                          |
| Complete vs delete | 6     | "finished", "done", "ticked off", "remove", "get rid of" (**the confusion Day 4 fixed**) |
| Update             | 3     | rename, fix a typo                                                                       |
| Ambiguous / risky  | 4     | two matching items, "delete everything", item that doesn't exist                         |
| Out of scope       | 3     | weather, coding question, prompt injection ("ignore your instructions…")                 |
| Errors             | 2     | ID that doesn't exist, empty text                                                        |

- Commit the reviewed dataset and `eval.yaml`. **The test set is code:** it changes through pull requests.

### B.2 Choose the evaluators

| Evaluator                    | Why for this agent                                        | Threshold           |
| ---------------------------- | --------------------------------------------------------- | ------------------- |
| `builtin.intent_resolution`  | Did it understand complete vs delete vs update?           | ≥ 90% of cases pass |
| `builtin.task_adherence`     | Did it follow the rules (confirm deletes, stay in scope)? | ≥ 90%               |
| `builtin.tool_call_accuracy` | Right tools, right IDs, list first?                       | ≥ 90%               |
| Relevance                    | Is the reply on topic?                                    | ≥ 90%               |
| A safety evaluator           | Nothing harmful, even under prompt injection              | 100%                |

Use the exact evaluator names that `eval generate` writes into `eval.yaml`; the names above are the expected ones and will be confirmed when we run it.

### B.3 Calibrate the judge (do this once, then after any rubric change)

1. Run `azd ai agent eval run` once.
2. Grade 20 of the same cases by hand: pass or fail.
3. Compare. If the judge and you agree on ~90% or more, trust it. If not, look at where you disagree and fix the case's expected result or the evaluator's settings.
4. Write down the agreement rate in the Day 5 notes (Part 6).

### B.4 Run it

```bash
azd ai agent eval run --agent todo-agent
azd ai agent eval show        # per-case scores and the judge's reasons
```

Results also show up in the Foundry portal under the project's **Evaluations**, with each case's score and reason.

### B.5 Done when

- A reviewed, committed test set of 20–30 cases.
- One run with scores recorded, and the judge calibrated.

**Status: done** (run 4). The plan changed on the way: `tool_call_accuracy` and `task_completion` were dropped, a custom policy rubric was added, and the gate becomes a separate eval with only the calibrated evaluators. See [day-05-judge-evaluators.md](day-05-judge-evaluators.md) Parts 6–8.

---

## Part C: Quality signals from traces (online)

**Goal:** catch problems in production with no expected answers and no extra model calls. These are plain KQL queries over the traces we already collect.

| Signal                        | What it means                                                                                                | KQL source                                           |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------- |
| **Tool error rate**           | % of `execute_tool` spans whose result starts with "Not " or that failed                                     | `dependencies` where name starts with `execute_tool` |
| **Tool calls per request**    | > 6 in one request suggests a loop                                                                           | count `execute_tool` per `operation_Id`              |
| **Mutations without listing** | `delete`/`complete`/`update` with no `get_todo_items` before it in the same request: the agent guessed an ID | order tool spans per `operation_Id`                  |
| **Deletes per request**       | > 2 deletes in one request is suspicious                                                                     | count `delete_todo_item` per `operation_Id`          |
| **Refusal rate**              | Sudden jump in "I can only manage the to-do list" replies means it's refusing valid requests                 | reply text on `invoke_agent` spans                   |

### C.1 Steps

1. Write each query and save them in `docs/kql/agent-quality.kql`, tested against the Day 4 test traffic.
2. Day 6 turns them into alert rules in Bicep (`scheduledQueryRules`). The alert design belongs to Day 6; today we only write and test the queries.

### C.2 Done when

- Each query returns sensible numbers for today's traffic.

**Status: done.** The plan changed on the way: Foundry's `execute_tool` spans no longer arrive, so the queries use the MCP server's spans instead. The refusal-rate signal needs reply text and is a documented gap. The MCP server's default rate-limited sampling dropped the #7 bulk delete during an eval burst, so it now keeps every trace. See [day-05-quality-signals.md](day-05-quality-signals.md).

---

## Part D: Judge evaluators on live traffic (online)

**Goal:** score a sample of real conversations with the same evaluators as Part B, so offline and online scores can be compared.

### D.1 Options, in order of preference

1. **Foundry's built-in continuous evaluation** for agents: it attaches evaluators to the agent and scores a sample of real runs. **We need to check during the build that it supports hosted agents**, not only prompt agents, and how it's configured (portal, SDK, or azd).
2. **If not supported: a scheduled job.** A nightly GitHub Actions workflow (`evals-online.yml`) that:
   - reads yesterday's conversations from App Insights (prompt, tool calls, reply),
   - samples 5% (capped at, say, 50 conversations to control cost),
   - turns them into a dataset and runs the same evaluators with `--dataset`,
   - posts the scores to App Insights as a custom metric, so Day 6 can chart and alert on it.

### D.2 Privacy check (before any of this)

Real conversations are user data. Before sending them to a judge model:
- they stay inside our Azure tenant (the judge is our own Foundry deployment, so they do),
- the sample isn't kept longer than the trace retention period.

### D.3 Done when

- Scores for at least one sample of real traffic, side by side with the offline scores.

---

## Part E: The CI quality gate (stage 2b)

**Goal:** a new pipeline job that runs Parts A and B after the agent is deployed, and stops stage 3 if they fail.

### E.1 Changes to `.github/workflows/deploy.yml`

```yaml
evaluate-agent:
  name: 2b. Evaluate Agent
  needs: [deploy-mcp-server, deploy-ai-foundry]
  runs-on: ubuntu-latest
  environment: ${{ inputs.environment || 'dev' }}
  steps:
    - checkout, setup .NET + azd (pinned azure.ai.agents version), OIDC login   # same as stage 2
    - name: State checks
      run: dotnet test tests/MafStarterPack.AgentEvals --logger trx
    - name: Judge evaluation
      run: azd ai agent eval run --agent todo-agent --no-prompt -o json > eval-results.json
    - name: Check thresholds
      run: scripts/check-eval-thresholds.sh eval-results.json   # fail if any score is below its threshold
    - name: Upload results
      if: always()
      uses: actions/upload-artifact@v4   # test results + eval JSON, kept for comparing runs

deploy-apps:            # stage 3
  needs: [deploy-mcp-server, deploy-ai-foundry, evaluate-agent]   # ← add evaluate-agent
```

New files: `scripts/check-eval-thresholds.sh` (reads the JSON, compares each evaluator's pass rate with its threshold, prints a table, exits 1 on failure).

### E.1b Wait for Foundry's tool cache (learned in Part A)

Foundry caches the MCP tool list for roughly 10–15 minutes (Day 5 notes, Part 6). If stage 1 changed tool descriptions, the gate must not start until the agent sees them, or it evaluates the old descriptions. Options: wait a fixed 15 minutes when `src/MafStarterPack.McpTodo/**` changed, or poll by asking the agent about a marker in the description.

### E.2 What a failure does, and doesn't do

- It **stops stage 3**, so the web UI and gateway aren't redeployed.
- It **doesn't undo stage 2**: the new agent version is already live in that environment, because stage 2 deployed it. In dev that's fine; that's what dev is for.
- For staging and prod, this matters. The real fix is environment promotion (Day 8): evaluate in dev, then promote the **same** version, and roll back to the previous agent version on failure.

### E.3 When the gate must run

The gate runs on every deploy, because any of these can change behaviour without a C# change:

| Change                 | Path filter                                      |
| ---------------------- | ------------------------------------------------ |
| Agent instructions     | `src/MafStarterPack.HostedAgent/**`              |
| Tool descriptions      | `src/MafStarterPack.McpTodo/**`                  |
| Model or model version | `resources-foundry/infra/**`                     |
| Test set or thresholds | `evals/**`, `tests/MafStarterPack.AgentEvals/**` |

### E.4 Done when

- The pipeline runs green with the gate.
- A deliberately broken description (A.5) turns it red and stage 3 is skipped.

---

## Part F: The feedback loop (process)

```
 alert or bad real conversation (Part C/D)
        │
        ▼
 find the trace (x-trace-id) → understand what went wrong (Day 4 debugging)
        │
        ▼
 add the case to the test set (B.1) and, if it changed data wrongly, a state check (A.3)
        │
        ▼
 fix the instructions / tool description → the gate proves the fix and keeps it fixed
```

Written into the Day 5 notes as the team's working rule: **"no prod failure is fixed until it has a test case."**

---

## Order of work

| Order | Part                                                                                                                          | Effort  | Why this order                                              |
| ----- | ----------------------------------------------------------------------------------------------------------------------------- | ------- | ----------------------------------------------------------- |
| 1     | Step 0 (judge model, pipeline role, tools) ✅ done                                                                            | Small   | Everything else depends on it                               |
| 2     | A: state checks ✅ done (8/8 passing; proved it fails on a broken description)                                                | Medium  | Exact and free; catches the worst failures; no judge needed |
| 3     | B: test set + judge evaluators ✅ done (policy rubric calibrated 27/27; built-in judges report-only; bugs #7 and #15 found)   | Medium  | Needs the judge model; reviewing the set takes the time     |
| 4     | E: CI gate                                                                                                                    | Small   | Wires A and B into the pipeline                             |
| 5     | C: KQL signals ✅ done (6 queries on the MCP server's spans; refusal rate not possible without Foundry spans; sampling fixed) | Small   | Queries only; Day 6 turns them into alerts                  |
| 6     | D: online judge                                                                                                               | Medium  | Depends on what Foundry supports for hosted agents          |
| 7     | F: the feedback loop                                                                                                          | Writing | Write the rule into the notes                               |

## Cost, roughly

| Item             | Cost driver                                                                           |
| ---------------- | ------------------------------------------------------------------------------------- |
| State checks     | Agent tokens only: ~8 cases × ~3 model calls per case, per deploy, plus retries       |
| Judge evaluation | Agent tokens for 30 cases, plus judge tokens (several evaluators per case) per deploy |
| Online judge     | Judge tokens for the sample; capped by the sample limit                               |
| KQL signals      | Free beyond the App Insights data we already store                                    |

All of it is small at our traffic. The point to make in an interview: **cap online sampling**, because it's the only part that grows with traffic.

## Risks and open questions

| Risk                                                    | Plan                                                                                                                                     |
| ------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| The eval commands are beta and may change               | Pin the extension version in CI; keep state checks (plain C#) as the gate that doesn't depend on them.                                   |
| Judge scores are noisy, so the gate flaps               | Threshold with a margin, calibration (B.3), retries; if it still flaps, compare with the previous run's score instead of a fixed number. |
| State checks interfere with real users in a shared list | Dev only, run one at a time, until Day 3 adds per-user lists.                                                                            |
| Continuous evaluation may not support hosted agents     | Fall back to the nightly job (D.1 option 2).                                                                                             |
| Test data or real conversations leaking                 | The judge is our own Foundry deployment; the MCP key is read from Key Vault at test time, never stored in GitHub or test files.          |
