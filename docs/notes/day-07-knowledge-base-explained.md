# Day 7: The Knowledge Base, Explained from a DevOps Point of View

Notes for Day 7 of the [prep plan](../ai-platform-engineer-prep-plan.md). Full build plan: [Days 6–11](day-06-11-maple-bank-disputes-plan.md), sections 7.1–7.8.

**Today in one sentence:** we turned Maple Bank's documents into a search index the agents can ask questions of, and wrapped it in a pipeline with tests, so a bad document can't quietly reach production.

Day 7 has a lot of moving parts. This note goes slowly: first the big picture, then each part on its own, then one full run from start to finish, then what can go wrong. For a deeper look at chunking, embeddings and the HNSW graph, see [the deep dive](day-07-chunking-vectors-hnsw.md).

---

## Part 1: The big picture

### 1.1 What problem are we solving?

On Day 8, an agent will review a dispute and say something like *"Deny: the customer raised this 130 days after the charge, and the time limit is 120 days (Card Dispute Policy 3.1)."*

The model doesn't know Maple Bank's policy. It was never trained on it, and the policy changes. So before the agent answers, we **look up the relevant part of the policy and paste it into the prompt**. That's **RAG** (Retrieval-Augmented Generation): retrieve first, then generate.

Day 7 builds the "retrieve" half: a place where you can ask *"what's the time limit for a duplicate charge?"* and get back the right paragraph of the right document.

### 1.2 The library analogy

| Library                                          | Our system                                   |
| ------------------------------------------------ | -------------------------------------------- |
| The books                                        | The 22 Markdown documents in Git             |
| Cutting books into index cards, one per topic    | **Chunking** (66 chunks)                     |
| The label on each card: subject, edition, shelf  | **Metadata** (doc type, version, dates…)     |
| The loading dock where new cards arrive          | The **blob container** `knowledge`           |
| The librarian who files new cards every morning  | The **indexer**                              |
| The card catalogue                               | The **index** `maple-knowledge-v1`           |
| "Show me cards from the 2026 edition only"       | A **filter**                                 |
| The sign on the door saying "Catalogue →"        | The **alias** `maple-knowledge`              |
| The head librarian who approves new books        | **CODEOWNERS** review                        |
| The quality inspector who spot-checks the cards  | **Retrieval tests**                          |

### 1.3 The whole flow on one page

```
 WRITE TIME (when a document changes)

 docs/maple-bank/*.md          You edit a document, open a PR
        │
        │  CODEOWNERS: compliance must approve
        │  check_docs.py: front matter, policy == rules engine, PII, injection
        ▼
 merge to main  ──►  GitHub Actions: ingest-knowledge.yml
        │
        │  chunk_docs.py: 22 documents → 66 JSON chunks
        ▼
 Blob Storage  st6a3q2sputpvay / knowledge      (the loading dock)
        │
        │  Indexer pulls new, changed and deleted files
        │  Skillset calls text-embedding-3-small for each chunk
        ▼
 AI Search index  maple-knowledge-v1             (the catalogue)
        ▲
        │  alias  maple-knowledge  →  maple-knowledge-v1
        │
 retrieval_test.py: 40 questions, must score ≥ 90%


 READ TIME (when an agent reviews a case, Day 8)

 Agent: "time limit for a duplicate charge?" + transaction date 2026-10-15
        │
        ▼
 AI Search: keyword search + vector search + semantic re-ranking,
            filtered to documents in force on 2026-10-15
        │
        ▼
 Top 3 chunks  →  pasted into the agent's prompt  →  answer with a citation
```

> **AWS:** this is S3 → a **Bedrock Knowledge Base** sync job → an **OpenSearch Serverless** vector index. Azure AI Search plays the role of both the Knowledge Base and OpenSearch.

---

## Part 2: The building blocks, one at a time

### 2.1 The documents (`docs/maple-bank/`)

22 Markdown files, all synthetic:

| Folder           | What's in it                                     | Count |
| ---------------- | ------------------------------------------------ | ----- |
| `policy/`        | Card Dispute Policy v3.1 (current)               | 1     |
| `policy/archive` | Card Dispute Policy v3.0 (before Nov 1, 2026)    | 1     |
| `network-rules/` | NorthStar card network summary (reason codes)    | 1     |
| `sops/`          | Step-by-step procedures for analysts             | 10    |
| `templates/`     | Customer letters, English and French             | 8     |
| `faq/`           | Analyst FAQ                                      | 1     |

Every file starts with **front matter**, a small block of labels:

```yaml
---
title: Card Dispute Policy
doc_type: policy
dispute_types: [duplicate_charge, incorrect_amount, ...]
version: "3.1"
effective_from: 2026-11-01
effective_to:                    # empty = still in force
owner: compliance
approved_by: head-of-disputes
audience: [analyst, senior_analyst]
language: en
---
```

These labels matter more than they look. Every one becomes a **filter** in the index. Without them, search can only match on meaning; with them, you can ask "only policies in force on Oct 15, only for analysts, only in French".

**Why Git is the source of truth:** documents get the same treatment as code. There's history (who changed the time limit, and when), review (pull requests), and rollback (revert the commit). The blob container and the index are *copies* that the pipeline rebuilds from Git.

### 2.2 The checker (`tools/knowledge/check_docs.py`)

The checker runs before anything touches Azure. It fails the build if:

1. **Front matter** is missing a field or has a bad value (e.g. `language: de`).
2. A document is **missing the "Maple Bank is fictional" footer**.
3. Two **policy versions overlap** in their dates. Then two versions would both be "in force" on the same day.
4. **The policy doesn't match the rules engine.** The time limits and the $1,000 approval threshold appear in two places: the readable policy and `dispute-rules.json`, which the code uses to decide. If someone changes one but not the other, the build fails.
5. Something looks like a **card number or email address** (personal data).
6. Something reads like a **prompt injection**, e.g. "ignore all previous instructions".

Point 4 is the one to remember for interviews: **"the model explains, code decides"**. The agent quotes the policy, but the actual eligibility decision comes from the rules engine. This check stops the two from drifting apart.

Point 6 matters because indexed text ends up inside the agent's prompt. A poisoned document would attack every case that retrieves it. The pattern check is cheap; Azure's **Prompt Shields** is the stronger, model-based version (Day 17).

### 2.3 Chunking (`tools/knowledge/chunk_docs.py`)

**Why chunk at all?** You don't paste a whole 8-page policy into every prompt. It costs tokens, and the important sentence gets lost in the noise. You want *just* section 3.1.

**How we chunk:**

| Document size            | Rule                         | Example                                      |
| ------------------------ | ---------------------------- | -------------------------------------------- |
| Small (under 2,000 chars) | Keep it whole                | An SOP: its steps only make sense together   |
| Large                    | One chunk per section (`##`/`###`) | Policy → "3.1 Time limits", "4.2 Duplicate charge"… |
| A section over 3,000 chars | Split between paragraphs     | Never happens today; it's a safety net       |

Each chunk **starts with its heading path**:

```
Card Dispute Policy › 3 General rules › 3.1 Time limits

A dispute must be raised within the time limit for its type...
| Duplicate charge (`duplicate_charge`) | 120 days |
...
```

Why add the header? A chunk that says only "120 days" means nothing on its own. With the header, the chunk explains itself to the search engine *and* to the model reading it.

**Why our own chunker instead of Azure's built-in "split skill"?** The built-in skill cuts every N characters. It would slice "4.2 Duplicate charge" in half and lose the section number. Analysts cite section numbers, so the section is the natural unit. Tables are never split, because they have no blank lines inside.

Each chunk is written as a JSON file with the labels copied in:

```json
{
  "chunk_id": "policy-card-dispute-policy_04",
  "parent_path": "policy/card-dispute-policy.md",
  "title": "Card Dispute Policy",
  "section": "3.1 Time limits",
  "doc_type": "policy",
  "version": "3.1",
  "effective_from": "2026-11-01T00:00:00Z",
  "effective_to": null,
  "language": "en",
  "audience": ["analyst", "senior_analyst"],
  "chunk": "Card Dispute Policy › 3 General rules › 3.1 Time limits\n\nA dispute must be..."
}
```

Result: **22 documents → 66 chunks**.

### 2.4 Embeddings: turning text into numbers

An **embedding model** (`text-embedding-3-small`) reads a piece of text and returns a list of **1,536 numbers**, a "vector". Texts with similar meaning get similar vectors.

- "customer was charged twice" and "duplicate charge" share no words, but their vectors are close.
- That's how search finds the right section even when the analyst's words differ from the policy's.

Two moments call the embedding model:

| When                     | What gets embedded         | Who calls it                   |
| ------------------------ | -------------------------- | ------------------------------ |
| Indexing (write time)    | Each of the 66 chunks      | The **skillset**               |
| Searching (read time)    | The question being asked   | The index's **vectorizer**     |

Both use the **same model**. That's essential: vectors from different models can't be compared. It's also why changing the embedding model means rebuilding the index (see blue/green, 2.9).

> **AWS:** the same idea as Amazon **Titan Embeddings** in a Bedrock Knowledge Base.

### 2.5 The five Azure AI Search objects (`resources-foundry/knowledge/*.json`)

This is the part that confuses most people. Azure AI Search has five separate objects, each defined in its own JSON file:

| File              | Object          | What it is, in one line                                         | Library analogy           |
| ----------------- | --------------- | --------------------------------------------------------------- | ------------------------- |
| `datasource.json` | **Data source** | *Where* to read from: the `knowledge` blob container            | The loading dock address  |
| `index.json`      | **Index**       | *What* is stored and how it's searchable: fields, vectors, ranking | The card catalogue design |
| `skillset.json`   | **Skillset**    | *Extra processing* on the way in: here, "embed each chunk"      | The librarian's tools     |
| `indexer.json`    | **Indexer**     | *The worker* that reads the data source, runs the skillset, writes to the index | The librarian |
| `synonyms.json`   | **Synonym map** | Words that mean the same: "chargeback = dispute", "refund = credit" | A thesaurus at the desk   |

How they connect:

```
   datasource ──► indexer ──► index
                    │
                 skillset
            (calls the embedding model)
```

The indexer is the only one that *does* anything; the rest are configuration. Running the indexer = "go to the loading dock, process anything new, file it in the catalogue".

**Index fields, the important ones:**

| Field                             | Type           | Used for                                         |
| --------------------------------- | -------------- | ------------------------------------------------ |
| `id`                              | key            | Unique ID (from the blob's path)                 |
| `chunk`                           | searchable text | Keyword search, with the synonym map             |
| `chunk_vector`                    | 1,536 numbers  | Vector (meaning) search                          |
| `title`, `section`                | text           | Citations ("Policy 3.1") and ranking             |
| `doc_type`, `language`, `audience` | filterable     | "Only templates", "only French", "only analysts" |
| `effective_from`, `effective_to`  | dates          | **"Only what was in force on this date"**        |
| `version`, `parent_path`          | filterable     | Citations and audit                              |

**Pull vs push.** There are two ways to fill an index:

- **Push:** your code calls the index API with documents and vectors.
- **Pull:** you drop files somewhere, and an indexer fetches them.

We use **pull**. The indexer tracks what it has already seen, retries failures, detects deletions, and logs every run, so our pipeline doesn't have to.

**Integrated vectorization** means the search service calls the embedding model itself (skillset at write time, vectorizer at read time). Our code never handles vectors. One less thing to build and secure.

### 2.6 How a search actually works (hybrid + semantic)

When you ask a question, three things happen in one request:

```
 "customer was charged twice at the same store"
          │
   ┌──────┴────────┐
   ▼               ▼
 Keyword search   Vector search
 (BM25: words +   (meaning: question vector vs
  synonym map)     chunk vectors)
   │               │
   └──────┬────────┘
          ▼
   Merge the two lists (Reciprocal Rank Fusion)
          ▼
   Semantic ranker: a language model re-reads the top results
   and re-orders them by how well they answer the question
   (score 0–4)
          ▼
   Top 3 chunks
```

Why both? They fail differently:

| Search type | Good at                                | Bad at                                     |
| ----------- | -------------------------------------- | ------------------------------------------ |
| Keyword     | Exact terms: "NS-12.1", "4.2", "$1,000" | Different wording: "charged twice"         |
| Vector      | Meaning, paraphrases, other wording    | Exact codes and numbers                    |
| **Hybrid**  | Both                                   |                                            |

The **semantic ranker** is the final "does this actually answer the question?" pass. It's on the free plan (1,000 queries a month), turned on in Bicep with `semanticSearch: 'free'`.

> **AWS:** OpenSearch hybrid queries plus a **Bedrock reranker** model.

### 2.7 Filters: the bank-specific part (policy versions)

This is the best interview example from Day 7.

**The problem:** on Nov 1, 2026 the time limit changed from 90 to 120 days. A dispute must be judged by the policy **in force on the transaction date**, not today's. An Oct 15 transaction uses 90 days.

**Without a filter** both versions come back, almost tied:

```
3.04  policy/card-dispute-policy.md              v3.1  3.1 Time limits
2.99  policy/archive/card-dispute-policy-v3.0.md  v3.0  3.1 Time limits
```

The agent might quote 120 days for an October transaction. That's a **wrong decision about a customer's money**: a compliance failure, not a quality dip.

**With a filter** for transaction date 2026-10-15:

```
effective_from le 2026-10-15 and (effective_to eq null or effective_to gt 2026-10-15)
```

only v3.0 can come back. Similarity search can't solve this, because the two versions are nearly identical text. **Metadata solves it.**

Other filters we use:

| Filter                                 | Why                                                       |
| -------------------------------------- | --------------------------------------------------------- |
| `audience/any(a: a eq 'analyst')`      | The "approving high-value credits" SOP is for seniors only |
| `language eq 'fr'`                     | A French-speaking customer gets the French letter         |
| `doc_type eq 'template'`               | When writing a letter, look only at letters               |

The last one came from a failed test. "Letter telling the customer their credit was approved" first matched *policy* text about approvals and letters. The fix wasn't tuning the search. A real agent knows it's looking for a template, so it **filters by type**. Good interview point: often the fix is a better query, not a better index.

### 2.8 The alias and blue/green

Agents never query `maple-knowledge-v1` directly. They query the **alias** `maple-knowledge`, a pointer to whichever index is live.

Why? Some changes can't be made to a live index: a new embedding model, new fields, new chunking. For those:

```
1. Build maple-knowledge-v2 next to v1      (agents still use v1 via the alias)
2. Run the retrieval tests against v2
3. Tests pass → move the alias: maple-knowledge → v2
4. Problem in production? Move it back to v1 (seconds, no rebuild)
```

That's **blue/green deployment**, the same pattern as for app releases, applied to data.

```bash
# build v2 and test it, alias unchanged
INDEX_NAME=maple-knowledge-v2 resources-foundry/knowledge/apply.sh
python3 tools/knowledge/retrieval_test.py --index maple-knowledge-v2

# switch, or roll back by naming v1
INDEX_NAME=maple-knowledge-v2 ALIAS_ONLY=true SWITCH_ALIAS=true resources-foundry/knowledge/apply.sh
```

Aliases are still a preview feature in Azure AI Search, so the script uses a preview API version for them.

> **AWS:** OpenSearch **index aliases** work the same way.

### 2.9 Deletions: the easy thing to get wrong

If a document is deleted from Git, its chunks must leave the index. Otherwise the agent keeps quoting a retired procedure.

How it works here:

1. `chunk_docs.py` starts from an empty folder each time, so a deleted document produces no chunks.
2. `apply.sh` compares the folder with the container and **deletes blobs that no longer have a chunk**.
3. **Blob soft delete** (7 days, set in `storage.bicep`) keeps the deleted blob in a "deleted" state instead of removing it at once.
4. The data source's **deletion detection policy** spots soft-deleted blobs and removes their chunks from the index.

We tested it: removing one letter took the count from 66 to 65, and restoring it brought it back to 66.

---

## Part 3: Security: who can do what

Key login is **turned off** on the search service (`disableLocalAuth: true`) and on storage (`allowSharedKeyAccess: false`). Everything signs in with Entra ID identities, like on Day 2.

| Who                         | Can do                                  | Role                                                | Why                                  |
| --------------------------- | --------------------------------------- | --------------------------------------------------- | ------------------------------------ |
| Search service (its own identity) | Read blobs; call the embedding model | Storage Blob Data Reader; Cognitive Services OpenAI User | The indexer pulls files and embeds them |
| Foundry project (the agents) | **Only read** the index                | Search Index Data Reader                            | Agents search; they never change the index |
| Pipeline (GitHub Actions)   | Create indexes; upload to `knowledge` only | Search Service Contributor; Blob Data Contributor on the container | Builds the index                      |
| You                         | Same as the pipeline, plus query         | Search Service Contributor; Index Data Contributor; Blob Data Contributor | Run `apply.sh` from your laptop     |

Notice the agents get the *smallest* role. If an agent were tricked by a prompt injection, it still couldn't change or delete the knowledge base.

> **AWS:** the search service's identity is like the **IAM service role** a Bedrock Knowledge Base uses to read S3 and call Titan.

**Data residency:** the search service lives in **Canada Central**, and the models are in East US 2. This happened because East US 2 ran out of capacity for new Search services. It's also the right call for a Canadian bank: the documents are indexed in Canada. It's a `searchLocation` parameter in Bicep.

---

## Part 4: The scripts, and what each one does

### 4.1 `apply.sh`: build everything in one command

```
1. Chunk documents           chunk_docs.py → build/chunks/*.json   (66 files)
2. Apply search definitions  PUT synonym map, index, data source, skillset
3. Upload chunks             upload to blob, delete stale blobs
4. Run the indexer           create or update it, start a run, wait for a NEW run to finish
5. Check document count      index count must equal chunk count (66)
6. Alias                     create it if missing; switch only if SWITCH_ALIAS=true
```

**Idempotent:** run it twice and you get the same result; the second run takes about 22 seconds. Every definition is a `PUT` (create or replace), so there's no "already exists" error.

Settings come from environment variables, or from the azd environment when run locally:

| Variable         | Example                       | Default                     |
| ---------------- | ----------------------------- | --------------------------- |
| `INDEX_NAME`     | `maple-knowledge-v2`          | `maple-knowledge-v1`        |
| `SWITCH_ALIAS`   | `true`                        | `false`                     |
| `ALIAS_ONLY`     | `true` (skip the build)       | `false`                     |
| `SEARCH_SERVICE`, `STORAGE_ACCOUNT`, `OPENAI_ENDPOINT` | | from `azd env` |

### 4.2 `retrieval_test.py`: is the search any good?

40 questions in [knowledge-retrieval.jsonl](../../resources-foundry/evals/datasets/knowledge-retrieval.jsonl), each with the answer we expect:

| Group         | Count | Example                                           | Passes when                          |
| ------------- | ----- | ------------------------------------------------- | ------------------------------------ |
| `policy`      | 15    | "Gym membership was cancelled but they kept charging" | Policy 4.5 in the top 3              |
| `sop`         | 10    | "How do I escalate a suspected fraud case?"       | The fraud SOP in the top 3           |
| `date_filter` | 6     | "Time limit for a duplicate charge?" on 2026-10-15 | **v3.0** section 3.1 in the top 3    |
| `template`    | 4     | "Lettre pour demander des documents…"             | The right letter, right language     |
| `no_answer`   | 5     | "Mortgage prepayment penalty?"                    | Nothing scores ≥ 2.0                 |

One test line:

```json
{"id": "date-01", "group": "date_filter", "question": "What is the time limit for a duplicate charge?",
 "as_of": "2026-10-15", "expect": {"doc_type": "policy", "section": "3.1", "version": "3.0"}}
```

**The gate:** hit@3 ≥ 90% **and** every date-filter test passes. Date tests can't be averaged away: a wrong policy version is wrong for a real customer. Today's result: **40/40**.

**hit@3** = "the right chunk is somewhere in the top 3". We use 3 because the agent will get the top 3 chunks.

**No-answer tests** matter as much as the others. If someone asks about mortgages, the right behaviour is "I don't have that", not the "closest" dispute paragraph. Off-topic questions scored at most 1.4 and real answers at least 2.1, so the cut-off is 2.0.

It's also your everyday debugging tool:

```bash
python3 tools/knowledge/retrieval_test.py -q "time limit for a duplicate charge?" --as-of 2026-10-15
python3 tools/knowledge/retrieval_test.py -q "lettre de refus" --language fr --doc-type template
```

### 4.3 `ingest-knowledge.yml`: the pipeline

```
 Pull request ──► Job 1: Check documents (no Azure access)
                     • check_docs.py   (lint, policy == rules, PII, injection)
                     • gitleaks        (secrets)
                     • chunk dry run   (does the chunker still work?)

 Merge to main ─► Job 1, then Job 2: Build index & test retrieval
 or manual run       • Azure login (OIDC, no stored secrets)
                     • find the search service, storage, Foundry account in rg-<env>
                     • apply.sh
                     • retrieval_test.py  (gate)
                     • switch alias (only if you ticked "switch_alias")
```

Design choices worth explaining:

- **PRs get no Azure access.** Checks run on untrusted changes; only reviewed, merged content touches the index.
- **`concurrency` with `cancel-in-progress: false`.** Two runs never fight over the same index, and a run is never killed halfway through indexing.
- **Resource names are looked up, not hard-coded.** They carry a random suffix (`search-6a3q2sputpvay`), so the workflow finds them by type in `rg-dev`.
- **Triggers include `dispute-rules.json`.** Changing the rules engine re-checks that the policy still matches.

### 4.4 `CODEOWNERS`: the human gate

[.github/CODEOWNERS](../../.github/CODEOWNERS) says who must review each folder:

| Path                                  | Real-bank owner   |
| ------------------------------------- | ----------------- |
| `policy/`, `network-rules/`, `rules/` | Compliance        |
| `sops/`, `templates/`, `faq/`         | Disputes ops      |
| `knowledge/`, tests, workflow         | Platform team     |

It only bites once **branch protection** on `main` has "Require review from Code Owners" turned on (a GitHub setting, still to do). In this one-person repo you stand in for every team.

---

## Part 5: One change, start to finish

The policy owner wants to change the senior approval threshold from $1,000 to $1,500. Here's what happens.

1. **Edit.** They change "Senior approval threshold: $1,000.00 CAD" in the policy and open a PR.
2. **Checks fail.** `check_docs.py` says the threshold differs from `dispute-rules.json`. Good: otherwise the agent would *say* $1,500 while the code *decides* with $1,000.
3. **Fix.** They update `dispute-rules.json` too. In reality this would be a new policy **version** with a new `effective_from` date, so older transactions keep the old rule.
4. **Review.** CODEOWNERS requires compliance approval.
5. **Merge.** Job 2 runs: chunks → upload → indexer re-embeds the changed chunks.
6. **Gate.** The retrieval tests run. If the change broke search for some questions, the run fails, and you see which questions.
7. **Live.** Agents searching `maple-knowledge` now get the new text. Git history shows who changed it, who approved it, and when.

---

## Part 6: Things that went wrong today (your interview stories)

| What happened | Cause | Lesson |
| --- | --- | --- |
| `azd provision` blocked: "715-123420 unusual activity" | Azure's fraud protection flagged repeated account updates | Not every failure is your code. Isolate it: a direct CLI call worked, so the block was on the account update. It cleared within hours |
| Search failed: "region out of resources" | East US 2 had no Search capacity | Make region a parameter. We moved Search to Canada Central, which also suits data residency |
| `gpt-5-nano` silently not deployed | azd reads models from the env variable `AI_PROJECT_DEPLOYMENTS`, not from azure.yaml | Know where your tool *actually* reads config. Editing the obvious file did nothing |
| The second `apply.sh` run hung | An empty POST needs `Content-Length: 0`; Azure returned 411 and `\|\| true` hid it. The first run only worked because creating an indexer starts a run | Never swallow all errors. Now only 409 "already running" is ignored |
| Waiting compared timestamps as text | `…:49.384Z` sorts before `…:49Z` | Don't compare clocks across machines. Now it waits for a run that's *different* from the previous one |
| Script broke on macOS | macOS bash 3.2 treats an empty array as unset under `set -u` | Test scripts on both your laptop and the CI runner |
| One retrieval test failed | Letter question matched policy text | Fix the query (filter by type), not the index |

---

## Part 7: AWS cheat sheet

| Azure (what we built)                   | AWS equivalent                                 |
| --------------------------------------- | ---------------------------------------------- |
| Blob container `knowledge`              | S3 bucket                                      |
| Blob soft delete                        | S3 versioning / delete markers                 |
| AI Search index                         | OpenSearch Serverless vector collection        |
| Indexer + data source                   | Bedrock Knowledge Base data source + sync job  |
| Skillset (embedding skill)              | Knowledge Base embedding model setting         |
| `text-embedding-3-small`                | Titan Text Embeddings                          |
| Semantic ranker                         | Bedrock rerank models                          |
| Hybrid search                           | OpenSearch hybrid query                        |
| Filters on metadata fields              | Knowledge Base metadata filtering              |
| Index alias                             | OpenSearch index alias                         |
| Search service managed identity         | Knowledge Base IAM service role                |
| Search Index Data Reader for the agent  | IAM policy with only `bedrock:Retrieve`        |
| GitHub OIDC → Azure                     | GitHub OIDC → IAM role                         |

---

## Part 8: Interview questions you can now answer

**"How does your RAG handle a policy change?"**
> Each policy version is indexed with effective dates. The agent filters by the *transaction* date, so an October transaction only ever sees the October policy. Six tests cover both sides of the change date, and they're mandatory in the gate. Separately, a check fails the build if the readable policy and the rules engine disagree.

**"How do you get documents into the index?"**
> Git is the source of truth. On merge, a pipeline chunks the documents by section, uploads them to blob storage, and an Azure AI Search indexer pulls them, embeds them with the skillset and writes the index. It's pull-based, so the indexer handles change tracking, retries and deletions.

**"How do you know retrieval is good?"**
> 40 labelled questions, scored on hit@3, with a 90% gate. They include date-filter tests that must all pass, and no-answer tests that check we return nothing for off-topic questions. They run in the pipeline after every rebuild and before any alias switch.

**"Why hybrid search?"**
> Keywords catch exact codes and numbers like "NS-12.1"; vectors catch paraphrases like "charged twice". The semantic ranker re-orders the merged list by how well each chunk answers the question.

**"How would you change the embedding model?"**
> Build a new index next to the live one, test it, then move the alias. Rollback is moving the alias back. You can't mix vectors from two models in one index.

**"How do you secure it?"**
> Keys are off; everything uses Entra ID. Agents have read-only access to the index. Documents pass a PII and injection scan before indexing. Policy changes need compliance review through CODEOWNERS.

**"How did you choose your chunk size?"**
> By structure, not size: one chunk per policy section, because a section is what people cite. Small documents stay whole. Each chunk carries its heading path so it makes sense on its own.

---

## Part 9: Commands you'll use

```bash
# Check documents (no Azure needed)
python3 tools/knowledge/check_docs.py

# Rebuild the index from Git
resources-foundry/knowledge/apply.sh

# Run all retrieval tests / ask one question
python3 tools/knowledge/retrieval_test.py
python3 tools/knowledge/retrieval_test.py -q "who approves a credit of 2500 dollars?" --audience analyst

# List what's in the search service
tok=$(az account get-access-token --resource https://search.azure.com --query accessToken -o tsv)
curl -s -H "Authorization: Bearer $tok" \
  "https://search-6a3q2sputpvay.search.windows.net/indexes?api-version=2024-07-01&\$select=name"
```

**In the portal:** rg-dev → `search-6a3q2sputpvay` → **Indexes** → `maple-knowledge-v1` → **Search explorer** to try queries; **Indexers** for run history and errors.

---

## What's next

- **Day 7 leftovers:** first pipeline run on GitHub; branch protection with "Require review from Code Owners".
- **Day 8:** the three agents. The policy agent will call this index through the alias, with the transaction date as a filter, and must cite the section it used.
