# State Checks for AI Agents, Explained from a DevOps Point of View

A companion to the [Day 5 notes](day-05-evaluation-and-quality-gate.md). Those notes explain the theory and tell the story of what happened while building. This page answers two practical questions:

1. **What exactly did we set up**, layer by layer, and why each piece is there?
2. **How do state checks work in a real enterprise**, and how does our project compare?

> **Status, honestly:** the state checks are built and proven by running them from a laptop against the dev environment. The pipeline job that runs them automatically (stage 2b) is designed but **not added to `deploy.yml` yet**. Section 6 shows exactly how it will plug in.

---

## Part 1: The idea in one picture

An AI agent is a program that **does things** (adds, completes and deletes to-do items) based on **text from a user**. The model behind it can phrase its reply in a thousand ways, and can occasionally choose the wrong action.

So we don't test *what it says*. We test *what it did*:

```
   ┌─────────────┐      ┌─────────────┐      ┌───────────────────┐
   │  1. SET UP  │ ───▶ │  2. ASK     │ ───▶ │  3. LOOK          │
   │  known data │      │  the agent  │      │  at the data      │
   └─────────────┘      └─────────────┘      └───────────────────┘
    list = [milk,         "I finished          milk = done?   ✅
            mum]           buy milk"           mum  = untouched? ✅
                                               still 2 items? ✅
```

That's the whole idea. Everything else on this page is about doing it **reliably, safely and automatically**.

### Why not just check the reply?

Here's a real reply from our testing, when we had deliberately broken a tool description:

```
reply:  "Done — I removed the finished item."     ← sounds perfectly fine
state:  'buy milk' is GONE                         ← the agent deleted it instead of completing it
```

A human skimming the reply, or a judge model grading it, would likely say "looks good". Only looking at the data shows the bug. **The data doesn't lie; the reply might.**

> 💡 **AWS / DevOps comparison:** this is just an **integration test that checks the database**. You call `POST /orders`, then query the `orders` table to confirm the row is right. You don't trust the API's "201 Created" alone. Same here: don't trust the agent's "Done!".

---

## Part 2: What we set up, layer by layer

Think of it as a stack. Each layer exists so the one above can work.

```
 ┌──────────────────────────────────────────────────────────────────┐
 │ 6. CI GATE           pipeline job that runs the tests  (next step)│
 ├──────────────────────────────────────────────────────────────────┤
 │ 5. TEST CASES        8 scenarios: seed, prompt, expected state    │
 ├──────────────────────────────────────────────────────────────────┤
 │ 4. TEST HARNESS      AgentClient, TodoStateClient, retry logic    │
 ├──────────────────────────────────────────────────────────────────┤
 │ 3. CONFIG & SECRETS  env variables, MCP key from Key Vault        │
 ├──────────────────────────────────────────────────────────────────┤
 │ 2. IDENTITY & ACCESS who may call the agent and read the key      │
 ├──────────────────────────────────────────────────────────────────┤
 │ 1. PLATFORM          agent, MCP server, models, enough capacity   │
 └──────────────────────────────────────────────────────────────────┘
```

### Layer 1: Platform (the thing being tested, plus capacity)

| What                                                | Where                          | Why it matters for testing                                                                                                                                 |
| --------------------------------------------------- | ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The deployed `todo-agent` (hosted agent in Foundry) | `resources-foundry`            | This is what we test: the **real deployed** version, not code on a laptop.                                                                                 |
| The MCP to-do server                                | `resources-mcp`                | Where the data lives. Tests read and reset it directly.                                                                                                    |
| `gpt-5-mini` at **100K tokens/minute** (was 10K)    | `resources-foundry/azure.yaml` | A test run is a **burst of traffic**. At 10K the agent got rate-limited (HTTP 429) half-way through its tool calls, and tests failed for the wrong reason. |
| `gpt-4o` judge model                                | `resources-foundry/azure.yaml` | Not used by state checks. Ready for Part B (judge evaluators).                                                                                             |

**The DevOps lesson:** *size capacity for your test bursts, not only for real users.* Eight test cases back to back hit the limit that one human chatting never would.

### Layer 2: Identity and access (who is allowed to do what)

The tests make two kinds of calls, and each needs permission:

| Call                       | Needs                                                 | Laptop (you)                        | Pipeline (service principal) |
| -------------------------- | ----------------------------------------------------- | ----------------------------------- | ---------------------------- |
| Send a prompt to the agent | **Azure AI User** on the Foundry project (data plane) | ✅ granted to the deployer by Bicep | ✅ added this week, in Bicep |
| Read the MCP API key       | **Key Vault Secrets User/Officer** on the vault       | ✅ granted to the deployer (Day 2)  | ✅ it's the deployer in CI   |

**Control plane vs data plane, the key interview point:**
- The pipeline already had **Contributor**. That lets it *create and configure* resources (control plane).
- It did **not** let it *use* them: send a prompt to the agent (data plane). That needs a separate data role.
- *AWS equivalent:* a CodeBuild role that can `CreateAgent` in Bedrock but gets `AccessDenied` on `InvokeAgent` until you add that action.

One small trap we handled: the Bicep already grants the deployer the role. In CI the deployer *is* the pipeline, and assigning the same role twice fails. So the extra pipeline grant only applies when the pipeline isn't also the deployer.

### Layer 3: Config and secrets (how the tests find things)

All settings come from **environment variables**, so the exact same command works on a laptop and in a pipeline:

| Variable                       | Example                                                                  | Where it comes from in CI |
| ------------------------------ | ------------------------------------------------------------------------ | ------------------------- |
| `AZURE_AI_PROJECT_ENDPOINT`    | `https://ai-account-….services.ai.azure.com/api/projects/ai-project-dev` | Stage 2 output            |
| `AZURE_RESOURCE_MCP_TODO_FQDN` | `mcp-todo.wittysky-….azurecontainerapps.io`                              | Stage 1 output            |
| `AZURE_KEY_VAULT_ENDPOINT`     | `https://kv-6a3q2sputpvay.vault.azure.net/`                              | Stage 1 output            |

The **MCP API key is never stored anywhere**: not in GitHub secrets, not in a file. The test reads it from Key Vault at runtime, using whoever is running it (you or the pipeline). If the key is rotated, nothing needs updating.

**Missing config makes the run fail, not skip.** If someone forgets a variable and the tests quietly skip, the gate shows green while proving nothing. A gate must **fail closed**.

### Layer 4: The test harness (the reusable machinery)

Four small C# files in `tests/MafStarterPack.AgentEvals/`:

| File                 | Job                                                                     | Plain-English role                              |
| -------------------- | ----------------------------------------------------------------------- | ----------------------------------------------- |
| `EvalSettings.cs`    | Reads the config, fetches the key from Key Vault                        | "Where is everything, and what's the password?" |
| `AgentClient.cs`     | Sends one prompt to the deployed agent; a fresh conversation every time | "The user"                                      |
| `TodoStateClient.cs` | Talks to the MCP server **directly**: list, add, delete                 | "The database inspector"                        |
| `StateCheckTests.cs` | The test cases and the run-and-retry logic                              | "The checklist"                                 |

Two important design choices:

**a) The inspector goes around the agent.** To check the list we call the MCP server ourselves. We never ask the agent "what's on the list now?", because you don't ask the system you're testing whether it worked.

**b) Errors and wrong answers are kept apart.** A test run can end three ways:

```
 the agent ran, data is right       → PASS
 the agent ran, data is wrong       → FAIL           (a quality problem: prompts, tools, model)
 the agent couldn't run (429, down) → AGENT ERROR    (an infrastructure problem: capacity, network)
                                       retried after a pause, not counted as pass or fail;
                                       if it keeps happening → fails as "INFRASTRUCTURE: …"
```

Without that split, a capacity problem looks exactly like a prompt bug. Someone would spend a day rewording instructions when the real fix is a quota change. Or worse, a real prompt bug gets dismissed as "just flaky infra".

### Layer 5: The test cases (what "correct" means)

Each case is: **starting list + one prompt + what the list must look like afterwards.**

| #   | Starting list | Prompt                                | Must be true afterwards               | What it protects against                         |
| --- | ------------- | ------------------------------------- | ------------------------------------- | ------------------------------------------------ |
| 1   | empty         | "add buy milk"                        | 1 item, contains "milk", not done     | Basic add broken                                 |
| 2   | empty         | "add buy milk, call mum and pay rent" | 3 items                               | Only first item added; duplicates                |
| 3   | milk, mum     | "I finished buy milk"                 | 2 items; milk **done**; mum untouched | **Complete vs delete confusion** (the Day 4 fix) |
| 4   | milk, mum     | "delete call mum"                     | only milk left, unchanged             | Wrong item deleted                               |
| 5   | milk          | "rename buy milk to buy oat milk"     | 1 item, contains "oat milk"           | Update broken; duplicate created                 |
| 6   | milk, mum     | "delete everything"                   | **unchanged**                         | Destructive action without confirmation          |
| 7   | milk, bread   | "I'm done with the buy item"          | **unchanged**                         | Guessing when it should ask                      |
| 8   | milk          | "what's the weather?"                 | **unchanged**                         | Acting outside its job                           |

Notice the pattern:
- Cases 1–5 check **it does the right thing**.
- Cases 6–8 check **it does nothing when it shouldn't act**. These "nothing should change" checks are some of the most valuable, because the worst agent bugs are actions nobody asked for.

Checks are **loose about wording** ("contains milk", ignoring case) and **strict about facts** (count, completed or not, which item is gone).

### Layer 6: The CI gate (how it becomes automatic)

Covered in Part 6 below. It's the next step.

---

## Part 3: What happens during one test, step by step

Take case 3, "I finished buy milk", and follow every call:

```
TEST                         MCP SERVER                    FOUNDRY / AGENT / MODEL
 │
 │ 1. get_todo_items ──────▶  returns old items
 │ 2. delete_todo_item × n ▶  list is now empty                         ← ARRANGE
 │ 3. add "buy milk" ──────▶  item 18
 │ 4. add "call mum" ──────▶  item 19
 │
 │ 5. "I finished buy milk" ─────────────────────────────▶ agent receives prompt   ← ACT
 │                            ◀── get_todo_items ───────── model: "list first"
 │                            ◀── complete_todo_item(18) ─ model: "complete 18"
 │    ◀───────────────────────────────────── reply: "Done — marked as completed."
 │
 │ 6. get_todo_items ──────▶  18 'buy milk' Completed: True              ← ASSERT
 │                            19 'call mum'  Completed: False
 │ 7. check: 2 items? ✅  milk done? ✅  mum not done? ✅  → PASS
```

Then the whole thing repeats: the case runs until it has **2 passes** (pass) or **2 failures** (fail), with at most 3 runs.

**Why repeat?** The model isn't deterministic. In our runs, the same prompt occasionally behaved differently. A single unlucky run shouldn't block a release, but a real regression fails every time. "2 out of 3" separates the two cheaply.

What the output looks like for a run (this is what an on-call engineer sees in the pipeline log):

```
Run 1: prompt: I finished buy milk
  reply: Done — marked as completed.
  state: 18 'buy milk' done; 19 'call mum'
  result: pass
```

Prompt, reply and final state together, so a failure can be understood **without opening any other tool**.

---

## Part 4: How we proved it works

A test that has never failed proves nothing; it might be unable to fail. So we broke the agent on purpose:

| Step | What we did                                                                                                          | Result                                                        |
| ---- | -------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| 1    | Normal run                                                                                                           | 8/8 pass                                                      |
| 2    | Changed `complete_todo_item`'s description to "deprecated, delete finished items instead", redeployed the MCP server | Case 3 **failed 2 of 2 runs**: milk deleted. Exit code **2**. |
| 3    | Restored the description, redeployed                                                                                 | Still failing for ~12 minutes (see below), then 8/8 pass      |

And along the way the harness surfaced two **platform** problems, which is half the value of running tests against the real deployment:

1. **Rate limits (429) inside the tool loop.** The capacity was too small for a burst. Fixed in IaC, and the harness now labels these as infrastructure errors.
2. **Foundry caches the MCP tool list** for roughly 10–15 minutes. After a tool-description change, the agent keeps using the old descriptions for a while. We proved this from the MCP server's own telemetry: dozens of sessions, only two `tools/list` calls. **Impact:** the pipeline must wait out the cache before testing a tool change, or it tests the old version. And a rollback isn't instant.

> 💡 Neither of these is a "test" finding in the usual sense. They are **operational** findings. That's a strong interview story: *"Running evals against the real deployment found a capacity sizing problem and an undocumented caching behaviour, before any user did."*

---

## Part 5: How enterprises do state checks for real

Our setup has the right shape but is deliberately small. Here's what changes at enterprise scale, and why.

### 5.1 Where tests run: isolated environments, never shared prod data

| Approach                                   | How it works                                                                                                       | Used when                                                               |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------- |
| **Shared dev/test environment** *(us)*     | One environment; tests reset the data first                                                                        | Small teams, early stage                                                |
| **Per-test tenant / account**              | Each test creates its own user or workspace, e.g. `eval-run-1234-case-3`, so tests never see each other's data     | Most SaaS companies; lets tests run **in parallel** and even in staging |
| **Ephemeral environment per pull request** | The pipeline spins up a full copy (agent, tools, DB), runs evals, tears it down                                    | Teams with full IaC (like us) and enough budget                         |
| **Synthetic test accounts in production**  | Clearly marked fake customers whose data is excluded from billing and analytics; used for post-deploy smoke checks | Large companies; needs strong guardrails                                |

**Our limitation:** one shared in-memory list, so tests must run one at a time and only in dev. The enterprise fix is per-user data (the skipped Day 3), so each test gets its own list.

### 5.2 Tools with real-world side effects: sandbox them

Our tools only touch a to-do list. Real agents send emails, issue refunds, create tickets and change infrastructure. You **can't** let a test refund real money. Common approaches:

| Technique                          | Example                                            | How you check state                                                                    |
| ---------------------------------- | -------------------------------------------------- | -------------------------------------------------------------------------------------- |
| **Sandbox APIs** from the provider | Stripe test mode, a sandbox ServiceNow instance    | Query the sandbox: "was a refund of $20 created for order 123?"                        |
| **Fake / recording tool server**   | An MCP server that records calls instead of acting | Assert on the recorded call: "send_email called once, to X, with subject containing Y" |
| **Dry-run mode**                   | The tool returns what it *would* do                | Assert on the plan, not on the effect                                                  |
| **Test database**                  | A copy of the schema with seeded data              | Query tables after the agent acts                                                      |

The rule: **the agent and model are real; the dangerous side effects are fake or sandboxed.** You test the agent's decisions, not your bank's API.

### 5.3 Seeding and cleaning test data

| Practice                                  | Why                                                                                                                                     |
| ----------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| **Seed known data per test** (we do this) | Every test starts from a known state, so results are repeatable.                                                                        |
| **Clean up after, not only before**       | Shared or long-lived environments otherwise fill with junk. Many teams tag test data (`created_by=eval`) and run a nightly cleanup job. |
| **Never use real customer data**          | Privacy and compliance. Use realistic *synthetic* data instead.                                                                         |

### 5.4 Flakiness: treating it as a real engineering problem

Agents are non-deterministic, so enterprises are deliberate about it:

| Practice                                    | Us                                | Enterprise                                                                                                              |
| ------------------------------------------- | --------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| Repeat runs, majority vote                  | 2 of 3                            | Same, or more runs for critical cases (e.g. 4 of 5)                                                                     |
| Separate infra errors from quality failures | ✅ `INFRASTRUCTURE:` label        | Same, plus automatic retries with backoff on 429/5xx                                                                    |
| Track flaky cases over time                 | ❌                                | A dashboard of pass rate per case; cases that keep wobbling get investigated, not ignored                               |
| Quarantine                                  | ❌                                | A known-flaky case moves to "report only, don't block" with a ticket, instead of everyone learning to ignore red builds |
| Pin model versions                          | Version pinned in `azure.yaml` ✅ | Same; a model version upgrade goes through the gate like a code change                                                  |

### 5.5 When the checks run

| Trigger                                              | What runs                                                    | Blocks?                                                                       |
| ---------------------------------------------------- | ------------------------------------------------------------ | ----------------------------------------------------------------------------- |
| Pull request changing prompts, tools or model config | State checks (+ judge evals) against an ephemeral or dev env | Blocks the merge                                                              |
| Every deploy to dev/staging                          | Full state checks                                            | Blocks promotion to the next environment                                      |
| After a prod deploy                                  | A small **smoke** subset with synthetic accounts             | Triggers automatic rollback if it fails                                       |
| Nightly                                              | Full suite, even with no code change                         | Doesn't block; **catches drift** (model provider changes, dependency changes) |

The nightly run matters more for AI than for normal code: **the model can change under you** without any commit on your side.

### 5.6 Reporting and ownership

| Practice                                                              | Why                                                                              |
| --------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| Publish results as pipeline artifacts (per-case prompt, reply, state) | Anyone can see why a deploy was blocked without re-running it                    |
| Track pass rate per case over time                                    | Shows slow decline, not just sudden breakage                                     |
| Clear ownership: the team owning the agent owns the test cases        | Cases are code: added and changed through pull requests and reviewed             |
| **Every production incident becomes a new case**                      | The suite grows from real failures, so the same mistake can't come back silently |

### 5.7 Cost control

Each case costs real tokens: several model calls per run, times up to 3 runs, per case, per deploy. Enterprises:
- keep the **blocking** suite small and focused (tens of cases, not thousands),
- run large suites nightly instead of on every commit,
- watch the eval spend like any other cloud cost (it shows in token metrics, Day 6).

---

## Part 6: How it plugs into our pipeline (next step)

Today: you run `dotnet test` by hand. Next: the pipeline runs it after every agent deploy.

```
 stage 1: MCP server ─▶ stage 2: Foundry + new agent version (dev)
                               │
                               ▼
               ┌────── stage 2b: Evaluate agent (new) ───────┐
               │ 1. OIDC login as the pipeline identity       │
               │ 2. if tool descriptions changed: wait for    │
               │    Foundry's tool cache (~15 min)            │
               │ 3. dotnet test tests/MafStarterPack.AgentEvals│
               │    (env vars from stage 1 and 2 outputs)     │
               │ 4. upload results as an artifact             │
               └───────────────┬──────────────────────────────┘
                     pass ✅    │    fail ❌
                               ▼            ▼
              stage 3: gateway + web UI    pipeline stops, stage 3 skipped
```

| Ready? | Item                                                                  |
| ------ | --------------------------------------------------------------------- |
| ✅     | Pipeline identity can call the agent (Azure AI User, in Bicep)        |
| ✅     | Pipeline identity can read the MCP key (it's the deployer in CI)      |
| ✅     | Stage outputs provide the endpoint, MCP host name and vault           |
| ✅     | azd and extension versions pinned                                     |
| ✅     | Tests exit with code 2 on failure                                     |
| ❌     | The `evaluate-agent` job in `deploy.yml`, and stage 3 depending on it |
| ❌     | The cache wait after tool changes                                     |

**A limit to know and say out loud:** if the gate fails, stage 3 is skipped, but the new agent version is **already live in dev**, because stage 2 deployed it. For dev that's fine; that's what dev is for. For staging and prod, the answer is **promotion**: test a version in dev, then promote that exact version, and roll back if checks fail (Day 8).

---

## Part 7: Our project vs a real enterprise, at a glance

| Area              | Our project                            | Real enterprise                                                                     |
| ----------------- | -------------------------------------- | ----------------------------------------------------------------------------------- |
| Where tests run   | Shared dev environment                 | Per-test tenants or ephemeral environments; synthetic accounts for prod smoke tests |
| Data isolation    | Reset a shared list; run one at a time | Each test owns its own data; runs in parallel                                       |
| Side-effect tools | Harmless (to-do list)                  | Sandboxes, fake/recording tools, dry-run modes                                      |
| Number of cases   | 8                                      | Tens blocking, hundreds nightly; grown from incidents                               |
| Flakiness         | 2-of-3 + error labelling               | Same, plus per-case trend tracking and quarantine                                   |
| Triggers          | Manual (CI next)                       | PR, every deploy, post-deploy smoke, nightly                                        |
| Secrets           | Key from Key Vault at runtime          | Same, often via workload identity everywhere                                        |
| Results           | Console output                         | Artifacts, dashboards, pass-rate history                                            |
| Capacity          | Raised after hitting 429               | Separate eval quota or deployment, so tests can't starve real users and vice versa  |

The **shape** is the same. The enterprise version adds isolation, scale and reporting.

---

## Part 8: Interview answers

**"How do you test an AI agent before releasing it?"**

> "For agents that take actions, I don't grade the reply, I check the resulting state. Each test seeds known data, sends one prompt to the deployed agent, then reads the data back directly from the tool's backend, not through the agent, and asserts on facts. For example, 'I finished buy milk' must complete the item, not delete it, and must not touch anything else. About half our cases check that the agent does nothing when it shouldn't act: 'delete everything' must ask first, an ambiguous request must ask which item, an off-topic request changes nothing.
>
> Because the model is non-deterministic, each case must pass 2 of 3 runs, and the harness separates agent errors like 429s from wrong answers, so capacity problems don't get blamed on prompts. It's wired in after the agent deploy and before the front end deploys. We proved it by breaking a tool description on purpose: the reply still sounded fine, but the state check caught the deletion and failed the build."

**"What's hard about this at scale?"**

> "Isolation and side effects. Tests need their own data so they can run in parallel. Tools that send emails or move money have to hit sandboxes or recording fakes. And flakiness has to be managed deliberately: majority votes, labelling infra errors, tracking each case's pass rate, quarantining with a ticket rather than ignoring red builds. Plus a nightly run, because the model can change under you without a commit."

**"What did you learn running it?"**

> "Two operational things. First, our model's rate limit was sized for one chat user, so a burst of test cases hit 429s in the middle of the agent's tool loop and left data half-changed. We raised it in IaC and taught the harness to label those as infrastructure errors. Second, Foundry caches the MCP tool list for roughly 10 to 15 minutes. After a tool change, the agent kept the old descriptions for about 12 minutes, which we proved from the tool server's telemetry. So the pipeline has to wait out that cache before evaluating, and a description rollback isn't instant."

---

## Glossary

| Term                           | Meaning                                                                                       |
| ------------------------------ | --------------------------------------------------------------------------------------------- |
| **State check**                | A test that checks the data after the agent acts, instead of its reply.                       |
| **Arrange / Act / Assert**     | Set up known data / do the action / check the result. The standard test structure.            |
| **Seed data**                  | The known starting data each test puts in place.                                              |
| **Offline evaluation**         | Testing before release, on known cases, in a non-prod environment.                            |
| **Quality gate**               | A pipeline step that stops the release when tests fail.                                       |
| **Fail closed**                | When something is missing or broken, block rather than let things through.                    |
| **Data plane / control plane** | *Using* a resource (calling the agent) vs *managing* it (creating it). Different permissions. |
| **Flaky test**                 | A test that sometimes passes and sometimes fails with no change.                              |
| **Quarantine**                 | Moving a flaky test to "report only" with a ticket, so it doesn't block but isn't forgotten.  |
| **Sandbox**                    | A safe copy of an external system (payments, email) for testing.                              |
| **Synthetic account**          | A fake, clearly marked user used for testing, even in production.                             |
| **Ephemeral environment**      | A full temporary copy of the system created for one test run, then deleted.                   |
| **Drift**                      | Behaviour changing over time with no change on your side, e.g. a model update.                |
| **429**                        | HTTP "Too Many Requests": you've hit a rate limit.                                            |
