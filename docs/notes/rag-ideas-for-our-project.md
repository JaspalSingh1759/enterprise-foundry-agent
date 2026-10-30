# RAG Ideas: What Real Projects Build, and What We Can Build Here

A menu of ideas for the RAG part of this project: what production teams build around a knowledge base of runbooks and SOPs, explained simply, with a verdict for each on whether it's worth doing here. Use it to choose the scope that went into the build plan. **Chosen scope: all six layers, plus multi-agent, multi-model and multimodal**, built as the Maple Bank dispute assistant in the [Days 6–11 plan](day-06-11-maple-bank-disputes-plan.md). The examples on this page use ops runbooks; the same ideas apply to the bank's policies and SOPs.

**How to read the verdicts:**

| Verdict          | Meaning                                                                       |
| ---------------- | ----------------------------------------------------------------------------- |
| ✅ **Core**      | Already in the Days 6–8 plan, or should be. Every real RAG system has it.     |
| ➕ **Add**       | Cheap to add here and a strong interview talking point.                       |
| 🚀 **Stretch**   | Takes real effort; do it if time allows, it makes the project stand out.      |
| 💬 **Talk only** | Too big or too costly for this project; know how it works and when to use it. |

---

## The big picture: a production RAG system has 6 layers

```
 1. SOURCES       runbooks, SOPs, postmortems, architecture docs, diagrams, screenshots
       │
 2. INGESTION     collect → clean → scan → split into chunks → embed → index      (a data pipeline)
       │
 3. INDEX         Azure AI Search: text + vectors + metadata + security fields
       │
 4. RETRIEVAL     rewrite the question → hybrid search → filter → rerank → top chunks
       │
 5. GENERATION    the agent answers from the chunks, cites them, admits "not found"
       │
 6. OPERATIONS    evaluation gates, monitoring, feedback loop, security, cost, rollback
```

Beginners build layers 3–5. **Production teams spend most of their effort on layers 1, 2 and 6**, and those are the DevOps layers. That's where this project should shine.

---

## Layer 1: Sources: what goes in the knowledge base

### 1.1 Kinds of documents

Real ops knowledge bases have several kinds of documents, and they're used differently:

| Kind                                   | Plain meaning                                           | Example from our project                                              | How the agent should use it                  |
| -------------------------------------- | ------------------------------------------------------- | --------------------------------------------------------------------- | -------------------------------------------- |
| **Runbook**                            | Steps to fix one specific problem or alert              | "Fix 401 from Foundry"                                                | Give the steps, in order, word for word      |
| **SOP** (standard operating procedure) | How we always do a routine task                         | "How to deploy to prod", "How to rotate a secret", "On-call handover" | Give the process, and who must approve       |
| **Postmortem**                         | What went wrong in a past incident and what we learned  | "The 401 incident", "The sampling trap"                               | "This happened before; here's what fixed it" |
| **Architecture doc / ADR**             | How the system is built and why (ADR = decision record) | "Why three azd projects", "Why our own MCP search tool"               | Explain the design                           |
| **Reference**                          | Facts: endpoints, resource names, role IDs, KQL queries | `docs/kql/agent-quality.kql`                                          | Look up exact values                         |

The simple difference to remember: **an SOP is for planned work, a runbook is for when something breaks.**

| Idea                                                                              | Verdict                              |
| --------------------------------------------------------------------------------- | ------------------------------------ |
| Runbooks in `docs/runbooks/` (8–10, from our real incidents)                      | ✅ Core                              |
| SOPs in `docs/sops/` (deploy, rotate a secret, add a new agent, on-call handover) | ➕ Add                               |
| Postmortems in `docs/postmortems/` (our 3 real stories, already written as STAR)  | ➕ Add                               |
| Our existing `docs/notes/*.md` as an "architecture and learning" source           | ➕ Add (good test: long, messy docs) |
| A `doc_type` field so the agent knows a runbook from an SOP                       | ➕ Add                               |

### 1.2 Where real companies' docs live

Production knowledge rarely lives in one Git folder. Typical sources: **Confluence, SharePoint, ServiceNow knowledge base, Git repos, PagerDuty/incident tools, Teams/Slack threads.** Each needs a *connector* that knows how to read it, detect changes and respect permissions.

| Idea                                                                | Verdict      |
| ------------------------------------------------------------------- | ------------ |
| Git folder as the source (docs as code, reviewed in PRs)            | ✅ Core      |
| A second source type, such as a SharePoint library or uploaded PDFs | 🚀 Stretch   |
| Connectors for Confluence/ServiceNow at scale                       | 💬 Talk only |

### 1.3 Content governance: keeping docs good

Bad docs give bad answers, so production teams manage docs like code:

- **A template** for each document kind (Symptoms → Cause → Fix → Verify for runbooks).
- **Front matter:** `owner`, `last_reviewed`, `service`, `severity`, `doc_type`.
- **A linter in the pipeline** that rejects docs missing those fields.
- **Review reminders:** a doc not reviewed in 6 months gets flagged to its owner.

| Idea                                                                          | Verdict                                         |
| ----------------------------------------------------------------------------- | ----------------------------------------------- |
| Templates + front matter + lint gate                                          | ✅ Core                                         |
| Stale-doc report: a scheduled job that lists docs past their review date      | ➕ Add                                          |
| Stale docs automatically added to the owner's to-do list (through todo-agent) | 🚀 Stretch (a fun tie-in to our existing agent) |

---

## Layer 2: Ingestion: getting docs into the index

This is a **data pipeline**, and it's the part platform interviews ask about most.

### 2.1 Triggers: when ingestion runs

| Way                      | How it works                                                    | Used when                                         | Verdict                |
| ------------------------ | --------------------------------------------------------------- | ------------------------------------------------- | ---------------------- |
| **On commit** (our plan) | GitHub Actions runs when `docs/runbooks/**` changes             | Docs live in Git                                  | ✅ Core                |
| **On a schedule**        | The indexer runs every hour by itself                           | Sources that change without a commit (SharePoint) | ➕ Add as a safety net |
| **On an event**          | A file lands in Blob → Event Grid → a Function starts ingestion | Many uploads from many teams                      | 💬 Talk only           |

### 2.2 Steps inside the pipeline

| Step                     | What it does                                                                              | Verdict                                  |
| ------------------------ | ----------------------------------------------------------------------------------------- | ---------------------------------------- |
| Lint                     | Template and front-matter checks                                                          | ✅ Core                                  |
| **Secret scan**          | gitleaks: no passwords or keys reach the index                                            | ✅ Core                                  |
| **PII scrub**            | Remove names, emails and phone numbers (Azure AI Language PII detection, or a regex pass) | ➕ Add (postmortems often contain names) |
| Change detection         | Only re-process files that changed                                                        | ✅ Core (the indexer does it)            |
| **Deletion handling**    | Deleted docs leave the index too                                                          | ✅ Core                                  |
| Parse non-Markdown files | PDFs and Word files → text, keeping headings and tables (Document Intelligence)           | 🚀 Stretch (see Layer 7)                 |
| Deduplicate              | Skip near-identical docs, so search doesn't return the same text 5 times                  | 💬 Talk only                             |
| Failure reporting        | Failed files are listed in the run summary and fail the pipeline                          | ✅ Core                                  |
| **Retrieval test gate**  | The right runbook is found for known questions, or the pipeline fails                     | ✅ Core                                  |

### 2.3 Chunking: how to split documents

Chunking decides what the agent can find, so teams test several ways:

| Way                    | Plain meaning                                                                                                           | Verdict                                   |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| Fixed size + overlap   | Cut every ~2,000 characters, overlap 200                                                                                | ✅ Core (start here)                      |
| **Heading-aware**      | Cut at Markdown headings, so "## Fix" stays one piece                                                                   | ➕ Add (our runbooks have clear headings) |
| **Chunk header**       | Put the doc title and section path at the top of every chunk: "Handle 429 › Fix". The chunk then makes sense on its own | ➕ Add (cheap, big gain)                  |
| **Parent–child**       | Search small chunks (precise), but give the model the whole section (complete)                                          | 🚀 Stretch                                |
| Keep code blocks whole | Never split a command in half                                                                                           | ➕ Add                                    |
| Semantic chunking      | A model decides where topics change                                                                                     | 💬 Talk only                              |

**The DevOps angle:** treat the chunking setting like any other config change. Run the retrieval tests on both settings, compare hit@3, and keep the better one. That's an *offline experiment*, and it's a great interview story.

---

## Layer 3: The index (Azure AI Search)

| Idea                                  | Plain meaning                                                                             | Verdict                                     |
| ------------------------------------- | ----------------------------------------------------------------------------------------- | ------------------------------------------- |
| Index definition as JSON in Git       | Rebuild everything from Git; no portal clicking                                           | ✅ Core                                     |
| **Metadata fields + filters**         | `doc_type`, `service`, `environment`, `severity`, so "prod deploy SOP" searches only SOPs | ➕ Add                                      |
| **Synonym map**                       | Tell search that "429", "throttling" and "rate limit" mean the same                       | ➕ Add (tiny, very effective for ops words) |
| **Freshness boost** (scoring profile) | Recently reviewed docs rank a little higher                                               | ➕ Add                                      |
| **Blue/green index** + switch         | New index next to the old one for big changes; instant rollback                           | ✅ Core                                     |
| Vector compression (quantization)     | Store vectors smaller to cut cost on large indexes                                        | 💬 Talk only (our index is tiny)            |
| Replicas and partitions               | Replicas = more queries per second and uptime SLA; partitions = more storage              | 💬 Talk only (know the difference)          |

**The source of truth is Git, not the index.** If the index is lost, the pipeline rebuilds it. That's why we can delete the search service every night.

---

## Layer 4: Retrieval: finding the right chunks

| Idea                                | Plain meaning                                                                                                                                                                   | Verdict                           |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------- |
| **Hybrid search** + semantic ranker | Keywords + meaning, then a small model re-orders the results                                                                                                                    | ✅ Core                           |
| "No good result" threshold          | Weak matches are dropped, so the agent says "not found"                                                                                                                         | ✅ Core                           |
| **Filters from the question**       | "How do I deploy to **prod**?" → filter `environment = prod`, `doc_type = SOP`                                                                                                  | ➕ Add                            |
| **Query rewriting**                 | A model turns "it's broken again lol, 429s" into a clean search query. Also uses the earlier chat ("what about prod?")                                                          | ➕ Add                            |
| **Agentic retrieval**               | For complex questions, the model splits them into several searches and merges the results. Azure AI Search has this built in as *agentic retrieval / knowledge bases* (preview) | 🚀 Stretch (check preview status) |
| Multi-query / HyDE                  | Search with several versions of the question, or with a guessed answer                                                                                                          | 💬 Talk only                      |
| GraphRAG                            | Build a knowledge graph of entities and links; good for "how do all these services relate?"                                                                                     | 💬 Talk only                      |

---

## Layer 5: Generation: how the agent answers

| Idea                           | Plain meaning                                                                                  | Verdict                             |
| ------------------------------ | ---------------------------------------------------------------------------------------------- | ----------------------------------- |
| **Cite the source**            | Every answer ends with the doc path and section                                                | ✅ Core                             |
| **Say "not found"**            | No runbook → say so; never invent steps                                                        | ✅ Core                             |
| **Copy commands exactly**      | Commands come from the runbook word for word, in a code block; the model never "improves" them | ➕ Add (one instruction + one test) |
| Answer shape per doc type      | Runbook → numbered steps; SOP → steps + who approves; postmortem → what happened + fix         | ➕ Add                              |
| **Warn on stale docs**         | "Note: this runbook was last reviewed 8 months ago"                                            | ➕ Add (uses `last_reviewed`)       |
| **Never execute, only advise** | The ops-agent tells the engineer what to run; running is a separate, approved step             | ✅ Core                             |

---

## Layer 6: Operations: the DevOps layer

### 6.1 Evaluation

| Idea                                  | Plain meaning                                                                                | Verdict                            |
| ------------------------------------- | -------------------------------------------------------------------------------------------- | ---------------------------------- |
| Retrieval tests (hit@3, MRR)          | Did search find the right doc? No model needed; runs on every doc change                     | ✅ Core                            |
| Answer tests (groundedness, citation) | Is the answer backed by the right doc? Judge model; runs on every agent change               | ✅ Core                            |
| "Not found" and injection tests       | Honesty and safety cases                                                                     | ✅ Core                            |
| **Synthetic test questions**          | A model writes 3 questions per runbook; **you review them** before they join the test set    | ➕ Add (how teams scale test sets) |
| **Experiment matrix**                 | Run the retrieval tests across chunk size × hybrid on/off × reranker on/off; pick the winner | 🚀 Stretch (great interview story) |
| Real questions → test set             | Questions users actually asked (and got wrong) become test cases                             | ➕ Add (same loop as Day 5 Part F) |

### 6.2 Monitoring

| Signal                                | Why it matters                              | Verdict |
| ------------------------------------- | ------------------------------------------- | ------- |
| No-hit rate                           | Questions we have no docs for               | ✅ Core |
| Low-score rate                        | Docs exist but are hard to find             | ✅ Core |
| Most-cited / never-cited docs         | What matters, and what's dead               | ✅ Core |
| Index freshness (last good ingestion) | Answers older than docs                     | ✅ Core |
| Latency split: search vs model        | Which part is slow                          | ➕ Add  |
| **Thumbs up/down** from the web UI    | Direct user signal; 👎 answers go to review | ➕ Add  |
| Tokens of context per answer          | Too many chunks = slow and expensive        | ➕ Add  |

### 6.3 The feedback loop (what makes it production-grade)

```
 user asks ──▶ no runbook found ──▶ logged ──▶ weekly report: "top 10 questions with no docs"
                                                   │
                                                   ▼
                       owner writes the runbook ──▶ PR ──▶ ingestion pipeline ──▶ answered next time
```

| Idea                                                                                     | Verdict                                                    |
| ---------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| Weekly "missing docs" KQL report                                                         | ➕ Add                                                     |
| Missing-doc questions added automatically to a "write runbook" to-do list via todo-agent | 🚀 Stretch (shows the two agents doing real work together) |

### 6.4 Security

| Idea                                         | Plain meaning                                                                                                              | Verdict                                   |
| -------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| RBAC only, keys off, read-only app role      | No shared passwords                                                                                                        | ✅ Core                                   |
| Secret scan + PII scrub at ingestion         | Sensitive data never enters the index                                                                                      | ✅ Core / ➕ Add                          |
| **Indirect prompt injection tests**          | A poisoned doc must not control the agent                                                                                  | ✅ Core                                   |
| **Prompt Shields** (Azure AI Content Safety) | A service that detects attacks hidden in documents before the model sees them                                              | ➕ Add (check how it fits a hosted agent) |
| **Security trimming**                        | Each chunk stores which groups may read it; search filters by the user's groups. E.g. security SOPs only for the SRE group | 🚀 Stretch (strong enterprise topic)      |
| Private endpoints for search and storage     | No public network path                                                                                                     | 💬 Talk only (cost)                       |

### 6.5 Reliability and cost

| Idea                                                                        | Verdict      |
| --------------------------------------------------------------------------- | ------------ |
| Index rebuilt from Git in one run (disaster recovery = re-run the pipeline) | ✅ Core      |
| Blue/green index with rollback                                              | ✅ Core      |
| Cost per answer (search + embeddings + model tokens)                        | ➕ Add       |
| Cache answers to frequent questions                                         | 💬 Talk only |
| Second region                                                               | 💬 Talk only |

---

## Layer 7: Multimodal RAG: pictures, diagrams and PDFs

"Multimodal" means the knowledge isn't only text. Real ops docs are full of **architecture diagrams, portal screenshots, scanned PDF SOPs and tables**. A text-only pipeline throws all of that away.

### 7.1 Three ways to handle images

| Way                                       | How it works                                                                                                                                                                                               | Good for                                                        |
| ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| **1. Extract the text (OCR / layout)**    | Document Intelligence reads text, tables and headings from PDFs and images                                                                                                                                 | Scanned SOPs, tables, forms                                     |
| **2. Describe the image (verbalization)** | A vision model (e.g. `gpt-4o`) writes a description: "Diagram: web UI → gateway → Foundry hosted agent → mcp-todo…". The description is embedded like normal text; the image link is kept for the citation | Architecture diagrams, screenshots                              |
| **3. Multimodal embeddings**              | One model turns images and text into vectors in the **same space**, so a text question can find a picture directly (Azure AI Vision multimodal embeddings)                                                 | Photo-heavy content; "find the screenshot that looks like this" |

**Way 2 is what most teams start with for ops docs:** it reuses the whole text pipeline, the descriptions are readable and testable, and the answer can link to the original diagram. Azure AI Search can do it inside the indexer with a skill that calls a vision model (preview features change often; check the docs on the day).

### 7.2 What it could look like here

```
 docs/runbooks/*.md ─┐
 docs/diagrams/*.png ┼─▶ indexer ─┬─ Markdown → split → embed
 docs/sops/*.pdf  ───┘            ├─ PDF      → Document Intelligence → split → embed
                                  └─ PNG      → gpt-4o describes it → embed the description (+ keep the image URL)
```

| Idea                                                                                                                          | Verdict                                  |
| ----------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| 3–4 diagrams (export our `infra-architecture.html` and `devops-aiops-workflow.html` as PNG) described by `gpt-4o` and indexed | 🚀 Stretch (our best multimodal demo)    |
| 2–3 portal screenshots in runbooks ("this is what the 429 metric looks like")                                                 | 🚀 Stretch                               |
| One SOP as a PDF, parsed with Document Intelligence                                                                           | 🚀 Stretch                               |
| **Screenshot in the chat:** the user pastes an error screenshot, a vision model reads it, then we search with the error text  | 🚀 Stretch (very real ops use case)      |
| Multimodal embeddings                                                                                                         | 💬 Talk only (region and preview limits) |

### 7.3 New things to test when images are involved

- Is the image description **correct**? (Review a sample by hand, like the Day 5 calibration.)
- Does a question about a diagram find the diagram? (Add those to the retrieval test set.)
- Cost: a vision call per image at ingestion. Fine for 10 images, needs a budget for 10,000.

### 7.4 Multimodal vs multi-model

They sound alike but mean different things:

| Term            | Meaning                                        | In our project                                                                                                                     |
| --------------- | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| **Multimodal**  | Different *kinds of input*: text, images, PDFs | Layer 7 above                                                                                                                      |
| **Multi-model** | Different *models* for different jobs          | gpt-5-mini (agents), text-embedding-3-small (search), gpt-4o (judge + image descriptions), maybe a small model for query rewriting |

---

## Beyond Q&A: what real ops copilots do

Answering questions is step one. The ops assistants teams actually rely on **act on live data**, safely. These build on everything above:

| Idea                              | How it works                                                                                                                                                             | Verdict                              |
| --------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------ |
| **Live checks (read-only tools)** | Give the ops-agent a `run_kql_query` tool with a read-only role. "Are we getting 429s right now?" → it runs the Day 5 query, then finds the runbook. **RAG + live data** | 🚀 Stretch (very strong demo)        |
| **Alert → incident assistant**    | An alert fires (Day 12) → the action group calls the ops-agent → it posts "likely cause + runbook steps + current numbers" to Teams/email                                | 🚀 Stretch (ties Days 6–12 together) |
| **Turn steps into tasks**         | "Add the fix steps to my list" → todo-agent (Day 9)                                                                                                                      | ✅ Core                              |
| **Human approval for actions**    | The agent proposes a command; a human approves before anything runs                                                                                                      | 💬 Talk only (Day 17 topic)          |
| **Postmortem draft**              | After an incident, the agent drafts a postmortem from the trace and the chat; a human edits it, and it goes back into the knowledge base                                 | 💬 Talk only                         |

---

## Suggested scope for our project

Three sizes to choose from. Each includes the one before it.

### Option S: core only (fits Days 6–8 as planned)

- Runbooks + SOPs + postmortems, templates and lint, secret scan.
- Pull ingestion from Git, deletion handling, blue/green index.
- Hybrid + semantic search, threshold, citations, "not found".
- Retrieval tests + answer tests + injection tests, quality signals.
- ops-agent + todo-agent (agent as a tool).

### Option M: production-grade (recommended; about +1 day)

Everything in S, plus the cheap ➕ items that make it look like a real system:

- `doc_type` / `service` / `environment` metadata, filters, **synonym map**, freshness boost.
- Heading-aware chunks with **chunk headers**; code blocks kept whole.
- Query rewriting; "copy commands exactly"; stale-doc warning.
- PII scrub; synthetic test questions (reviewed); thumbs up/down; weekly missing-docs report.

### Option L: stand-out (about +2–3 days)

Everything in M, plus one or two 🚀 items. The most valuable for interviews, in order:

1. **Live checks** (`run_kql_query` tool): RAG + live telemetry, the core of AIOps.
2. **Multimodal**: diagrams described by gpt-4o, and a screenshot pasted into the chat.
3. **Alert → incident assistant**: connects RAG to Day 12 alerts.
4. **Security trimming**: two groups, different docs.
5. **Experiment matrix** for chunking/search settings.

**Decided 2026-10-30:** all six layers, built for the Maple Bank dispute assistant instead of an ops copilot. See the [Days 6–11 plan](day-06-11-maple-bank-disputes-plan.md).

---

## Interview one-liners from this page

- "Most RAG work in production is the data pipeline and operations, not the model."
- "An SOP is for planned work; a runbook is for when something breaks. We tag them so the agent answers each the right way."
- "The source of truth is Git. The index is rebuilt by the pipeline, so it's cattle, not a pet."
- "We test retrieval and answers separately, so we know whether to fix the data or the prompt."
- "Chunk size is a config change: we compare settings on the retrieval test set and keep the winner."
- "For images we describe them with a vision model and index the description. It reuses the whole text pipeline and keeps a link to the original."
- "The no-hit questions are our documentation backlog."
- "The step after Q&A is live data: the agent checks the telemetry, then finds the runbook, and a human still runs the fix."
