# Day 7 Deep Dive: Chunking, Vectors and the HNSW Graph

A companion to [Day 7: The Knowledge Base, Explained](day-07-knowledge-base-explained.md). That note covers every moving part; this one follows **one document** all the way from a Markdown file to a point in an HNSW graph, and back out again as a search result. It's written for interviews: each stage ends with what to say.

**The three stages:**

| Stage | Question it answers | Where it happens in our project |
| --- | --- | --- |
| 1. **Chunking** | How do we cut documents into searchable pieces? | Our script `chunk_docs.py`, in the pipeline, *before* Azure |
| 2. **Vectorizing** | How does text become something we can compare by meaning? | The **skillset** (embedding skill), run by the **indexer**, inside Azure AI Search |
| 3. **Indexing in HNSW** | How do we find the nearest vectors quickly? | Inside the **index**, automatically, as each row is written |

We'll follow the **Analyst FAQ** (`docs/maple-bank/faq/analyst-faq.md`) the whole way.

---

## Stage 1: Chunking

### 1.1 Why chunk at all?

Three reasons, all of which you should be able to say in an interview:

1. **Precision.** A search result should be *the answer*, not "somewhere in this 8-page document".
2. **Cost and context.** Every retrieved chunk is pasted into the agent's prompt. Small, focused chunks mean fewer tokens and less noise for the model.
3. **Embedding quality.** An embedding is one vector for the whole text. Embed a whole document that covers ten topics and the vector is a blurry average of all ten. A chunk about one topic gets a sharp vector.

### 1.2 Who chunks? We do.

Azure AI Search never chunks by itself. If you configure nothing, one file = one search document. You choose one of:

| Option | How it splits |
| --- | --- |
| Text Split skill | Fixed size (e.g. 2,000 characters) with overlap |
| Document Layout skill | By headings and tables (Document Intelligence) |
| Markdown parsing mode | By Markdown headings |
| **Pre-chunk in your own code** ← *ours* | Any logic you want |

**Why we pre-chunk:** our documents have numbered sections ("4.2 Duplicate charge"), and the section is what analysts and the agent *cite*. Fixed-size splitting would cut sections in half and lose the number. Writing our own chunker also means we can attach the front-matter labels (version, effective dates, audience) to every chunk, which the date filter depends on.

### 1.3 The rules (`tools/knowledge/chunk_docs.py`)

```
For each document:
  1. Read the front matter (title, version, effective dates, audience...)
  2. Remove the "# Title" line and the fictional footer
  3. Body under 2,000 characters?  → ONE chunk (SOPs, letters)
     Otherwise                     → ONE chunk PER SECTION (## and ### headings)
        • a heading with no text of its own ("## 3 General rules") becomes part
          of its children's header instead of an empty chunk
        • a section over 3,000 characters is split between paragraphs
          (tables have no blank lines inside, so they're never split)
  4. Start each chunk with its heading path:  "Title › Section › Subsection"
  5. Copy the front-matter labels into every chunk
  6. Write each chunk as its own JSON file
```

**Why the heading path at the start?** A chunk that says *"An analyst may approve up to $1,000"* doesn't say *which* document or section it's from. With `Card Dispute Policy › 3 General rules › 3.3 Approval` in front:

- **Keyword search** can match "approval" and "policy" even if the body doesn't repeat them.
- **The embedding** captures the context, not just the sentence.
- **The model reading it** knows what it's looking at and can cite it.

**Why no overlap?** Overlap (repeating the last 10–25% of one chunk at the start of the next) is for *fixed-size* chunking, where a cut can land mid-sentence. Our cuts are at section boundaries, which are natural breaks.

### 1.4 The FAQ, chunked

The FAQ body is about 2,100 characters, over the 2,000 limit, so it's split by section. It has 10 `##` questions, so it becomes **10 chunks**:

```
docs/maple-bank/faq/analyst-faq.md
   │
   ├─► faq-analyst-faq_01.json   "Which policy version applies when the policy changes?"
   ├─► faq-analyst-faq_02.json   "How do I count the days for the time limit?"
   ├─► ...
   ├─► faq-analyst-faq_08.json   "Who approves a credit of exactly $1,000.00?"
   ├─► ...
   └─► faq-analyst-faq_10.json   "A document attached to a case contains instructions..."
```

One of them, in full:

```json
{
  "chunk_id": "faq-analyst-faq_08",
  "parent_path": "faq/analyst-faq.md",
  "title": "Card Disputes Analyst FAQ",
  "section": "Who approves a credit of exactly $1,000.00?",
  "doc_type": "faq",
  "dispute_types": ["duplicate_charge", "incorrect_amount", "..."],
  "version": "1.4",
  "effective_from": "2026-11-01T00:00:00Z",
  "effective_to": null,
  "language": "en",
  "audience": ["analyst", "senior_analyst"],
  "owner": "disputes-ops",
  "approved_by": "head-of-disputes",
  "chunk": "Card Disputes Analyst FAQ › Who approves a credit of exactly $1,000.00?\n\nAn analyst. Only credits of **more than** $1,000.00 need a senior analyst (policy 3.3)."
}
```

**Across all documents: 22 documents → 66 chunks.** The policy gives 16, the archived policy 16, the FAQ 10, the network rules 6, and each SOP and letter 1.

### 1.5 Upload

`apply.sh` uploads the 66 files to Blob Storage: `st6a3q2sputpvay` → container `knowledge`. It also deletes any blob that no longer has a chunk, so removed sections and documents leave the index (see Stage 4).

At this point **nothing is vectorized yet**. The blobs are plain JSON text.

> **Say in an interview:** "We chunk by document structure, not size: one chunk per policy section, because sections are what people cite, while small docs stay whole. Each chunk starts with its heading path, so it stands on its own, and carries the document's metadata as filterable fields. We chunk in the pipeline before upload, so the logic is versioned, tested and reviewed like any other code."

---

## Stage 2: Vectorizing

### 2.1 What a vector is, in plain words

An **embedding model** reads text and outputs a list of numbers, a **vector**. Ours is `text-embedding-3-small`, which outputs **1,536 numbers** for any text, whether it's a word or a page.

Think of each vector as a **point on a map of meanings**. Texts that mean similar things land close together, even when they share no words:

```
            "charged twice"  •  • "duplicate charge"
                                  • "same purchase billed two times"


   "package never arrived" •
        "order not delivered" •


                                     • "branch opening hours"   (far from everything)
```

The real map has 1,536 directions instead of 2, but the idea is the same: **closeness = similar meaning**.

**How "close" is measured: cosine similarity.** Imagine each vector as an arrow from the centre. Cosine similarity measures the **angle** between two arrows: same direction = 1 (same meaning), at right angles = 0 (unrelated). We set `"metric": "cosine"` in `index.json`. It's the standard choice for OpenAI embeddings.

### 2.2 Who calls the embedding model? The indexer, through the skillset.

Once the blobs are uploaded, `apply.sh` starts the **indexer**. Think of it as an ETL job:

```
 EXTRACT                       TRANSFORM                        LOAD
 read blob  ─►  parse JSON  ─►  skillset: embed "chunk"  ─►  write row to index
 (datasource)   (parsingMode)   (AzureOpenAIEmbeddingSkill)   (outputFieldMappings)
```

For **each** of the 66 blobs, the indexer does this:

| Step | What happens to `faq-analyst-faq_08.json` | Configured in |
| --- | --- | --- |
| 1. Read | Fetches the blob from `knowledge`, using the search service's managed identity | `datasource.json` |
| 2. Parse | `parsingMode: json`, so the JSON keys become fields: `section`, `version`, `chunk`… | `indexer.json` |
| 3. Key | Builds the document key from the blob path (base64-encoded) | `indexer.json` → `fieldMappings` |
| 4. Embed | Sends the `chunk` text to `text-embedding-3-small`; gets back 1,536 numbers | `skillset.json` |
| 5. Map | Puts those numbers in the `chunk_vector` field | `indexer.json` → `outputFieldMappings` |
| 6. Write | Writes the whole row (text + labels + vector) to `maple-knowledge-v1` | `indexer.json` → `targetIndexName` |

The skillset, the part that does step 4:

```json
{
  "@odata.type": "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
  "context": "/document",                       // once per document = once per chunk
  "resourceUri": "https://ai-account-6a3q2sputpvay.openai.azure.com",
  "deploymentId": "text-embedding-3-small",
  "dimensions": 1536,
  "inputs":  [{ "name": "text", "source": "/document/chunk" }],
  "outputs": [{ "name": "embedding", "targetName": "chunk_vector" }]
}
```

**No API key.** The search service has a **system-assigned managed identity** with the *Cognitive Services OpenAI User* role on the Foundry account (in Bicep). It signs in to the model like any other Azure service.

**Cross-region note:** the search service is in Canada Central and the model in East US 2, so the chunk text crosses regions for embedding. For synthetic data that's fine. For a real bank you'd want both in Canada; that goes in the data-residency decision record.

### 2.3 "Integrated vectorization"

Our code never handles a vector. The search service:

- embeds **chunks** at indexing time (skillset), and
- embeds **questions** at search time (the index's **vectorizer**, Stage 3.5).

Both use the **same model**, which is mandatory: vectors from different models live on different "maps" and can't be compared. It's why changing the embedding model means re-embedding everything into a new index.

> **Say in an interview:** "We use integrated vectorization. The indexer runs a skillset with the Azure OpenAI embedding skill, which embeds each chunk with text-embedding-3-small, and an output mapping writes the 1,536-dimension vector into the index. The search service calls the model with its managed identity, so there are no keys. The same model is configured as the index's vectorizer, so questions are embedded server-side too."

---

## Stage 3: Indexing in the HNSW graph

### 3.1 The problem HNSW solves

A question comes in, and we turn it into a vector. Now: **which of the stored vectors are closest?**

The obvious way is to compare the question with **every** stored vector (that's called *exhaustive KNN*). With 66 vectors that's instant. With 10 million vectors × 1,536 numbers, it's far too slow to do on every query.

**HNSW (Hierarchical Navigable Small World)** is a way of storing vectors so you can find the nearest ones by checking only a tiny fraction of them. It's *approximate*: it almost always finds the true nearest neighbours, and occasionally misses one in exchange for being much faster.

### 3.2 The idea, with an analogy

**Small world:** think of a social network. Everyone is connected to a few friends, and through friends-of-friends you can reach anyone in a few hops. HNSW connects each vector to its **nearest neighbours**, making a graph you can walk through.

**Hierarchical:** think of travelling by **highway → main road → local street**.

```
 Layer 2 (highways):     A ─────────────────────── F            few points, long links
                         │                         │
 Layer 1 (main roads):   A ──── C ──────── E ───── F            more points
                         │      │          │       │
 Layer 0 (every street): A ─ B ─ C ─ D ─ E ─ G ─ F ─ H ...     ALL points, short links
```

- **Every** vector is in layer 0, linked to its close neighbours.
- A random few are also in layer 1, fewer still in layer 2, and so on.
- The upper layers have long-distance links, for crossing the map quickly.

### 3.3 How a vector is added (this happens during the indexer run)

When the indexer writes the FAQ_08 row, AI Search **automatically** inserts its vector into the graph. You don't run anything:

```
1. Randomly pick how many layers this vector joins (usually just layer 0)
2. Start at the top layer's entry point
3. Greedy walk: keep hopping to whichever neighbour is closer to the new vector
4. Drop down a layer, keep walking — like leaving the highway for main roads
5. In each layer it joins, link it to its nearest neighbours found there
```

This is just like a SQL database updating its B-tree index as rows are inserted: **the structure is maintained on write, so reads are fast.**

### 3.4 How a search walks the graph

```
1. Enter at the top layer
2. Hop to whichever neighbour is closest to the question vector
3. Can't get closer on this layer? Drop down a layer
4. On layer 0, explore the local neighbourhood carefully
5. Return the k closest vectors found
```

You touch a few hundred vectors instead of millions. That's why vector search stays fast as the index grows.

### 3.5 Our configuration (`index.json`)

```json
"vectorSearch": {
  "algorithms": [
    { "name": "hnsw", "kind": "hnsw", "hnswParameters": { "metric": "cosine" } }
  ],
  "vectorizers": [
    { "name": "aoai", "kind": "azureOpenAI",
      "azureOpenAIParameters": { "resourceUri": "...", "deploymentId": "text-embedding-3-small" } }
  ],
  "profiles": [
    { "name": "default", "algorithm": "hnsw", "vectorizer": "aoai" }
  ]
}
```

and the field that uses it:

```json
{ "name": "chunk_vector", "type": "Collection(Edm.Single)",
  "dimensions": 1536, "vectorSearchProfile": "default", "retrievable": false }
```

How the pieces connect: the **field** points to a **profile**; the profile names an **algorithm** (how vectors are stored and searched) and a **vectorizer** (how questions are embedded).

**The HNSW tuning knobs** (we only set `metric`; the others use Azure's defaults):

| Parameter | What it controls | Azure default | Trade-off |
| --- | --- | --- | --- |
| `m` | Links per vector in the graph | 4 | More links = better recall, more memory |
| `efConstruction` | How hard it looks for neighbours when *adding* a vector | 400 | Higher = better graph, slower indexing |
| `efSearch` | How many candidates it considers when *searching* | 500 | Higher = better recall, slower queries |
| `metric` | How closeness is measured | cosine | Match what the embedding model was trained for |

With 66 vectors none of this matters: it's all instant and effectively exact. It matters at millions of vectors, which is exactly why interviewers ask.

**HNSW vs exhaustive KNN:**

| | HNSW | Exhaustive KNN |
| --- | --- | --- |
| Finds | *Approximately* the nearest (very high recall) | *Exactly* the nearest |
| Speed at scale | Fast | Slow (checks every vector) |
| Memory | Extra memory for the graph links | Just the vectors |
| Use when | Almost always, in production | Small indexes, or measuring HNSW's recall against the exact answer |

**Memory, as a sizing example:** each vector is 1,536 × 4 bytes ≈ 6 KB. Ours: 66 × 6 KB ≈ 400 KB. One million chunks ≈ 6 GB of vectors plus graph overhead. Vector storage is limited per search tier, which is why production teams use **quantization** (storing the numbers in fewer bits) or **smaller dimensions** (`text-embedding-3` models can output fewer than 1,536).

> **Say in an interview:** "HNSW is a layered graph of nearest-neighbour links. Upper layers have few points and long links for crossing the space quickly; the bottom layer has every vector. Inserting walks the graph to find a new vector's neighbours and links it in, and Azure AI Search does this automatically as the indexer writes documents. A query does the same greedy walk from the top, so it checks a tiny fraction of the vectors. It's approximate nearest neighbour: you trade a little recall for big speed, tuned with m, efConstruction and efSearch. We use cosine because that's what OpenAI embeddings are trained for."

---

## Stage 3½: What's actually stored, and where (pictures)

A common mix-up: *"each chunk gets vectorized, and its vector and metadata make an index for that chunk, which is stored in a vector database."* Two words need fixing:

- **The index isn't per chunk.** The **index is the whole table**; **each chunk becomes one row** in it. Azure calls a row a "document".
- **There's no separate vector database.** Azure AI Search *is* the vector store. Text, metadata and vectors live together in the same index, in the same row.

### Picture 1: one chunk becomes one row

```
 faq-analyst-faq_08.json  (blob)                       ONE ROW in the index
 ┌──────────────────────────────────┐                  ┌──────────────────────────────────────────┐
 │ section: "Who approves ... $1,000"│                  │ id            aHR0cHM6Ly9zdDZh...  (key)  │
 │ version: "1.4"                   │   indexer reads   │ chunk_id      faq-analyst-faq_08         │
 │ effective_from: 2026-11-01       │ ───────────────►  │ section       Who approves ... $1,000.00?│
 │ audience: [analyst, senior...]   │                   │ doc_type      faq                        │
 │ chunk: "Card Disputes Analyst    │                   │ version       1.4                        │
 │   FAQ › Who approves... An       │   skillset embeds │ effective_from 2026-11-01                │
 │   analyst. Only credits of more  │ ──── "chunk" ───► │ audience      [analyst, senior_analyst]  │
 │   than $1,000.00 need..."        │   text-embedding  │ chunk         "Card Disputes Analyst..." │
 └──────────────────────────────────┘   -3-small        │ chunk_vector  [0.012, -0.044, 0.031,     │
                                                        │               ... 1,536 numbers ...]     │
                                                        └──────────────────────────────────────────┘
                                                          text + metadata + vector, all in one row
```

The JSON fields are copied into the row as they are. The skillset adds one new field, `chunk_vector`, computed from the `chunk` text.

### Picture 2: the index is the table holding all 66 rows

```
 Azure AI Search service: search-6a3q2sputpvay  (Canada Central)
 │
 └── INDEX: maple-knowledge-v1        ◄── alias "maple-knowledge" points here
     │
     │   one row per chunk (66 rows)
     ├───────────────────────────────┬──────────┬─────────┬────────────┬─────────────┬─────────────────────┐
     │ chunk_id                      │ doc_type │ version │ eff_from   │ eff_to      │ chunk_vector        │
     ├───────────────────────────────┼──────────┼─────────┼────────────┼─────────────┼─────────────────────┤
     │ policy-card-dispute-policy_04 │ policy   │ 3.1     │ 2026-11-01 │ null        │ [0.021, -0.008, ...]│
     │ policy-archive-..-v3-0_04     │ policy   │ 3.0     │ 2026-01-01 │ 2026-11-01  │ [0.019, -0.011, ...]│
     │ sops-duplicate-charge_01      │ sop      │ 1.2     │ 2026-06-01 │ null        │ [-0.004, 0.037, ...]│
     │ faq-analyst-faq_08            │ faq      │ 1.4     │ 2026-11-01 │ null        │ [0.012, -0.044, ...]│
     │ templates-letter-..-fr_01     │ template │ 2.0     │ 2026-06-01 │ null        │ [0.030, 0.002, ...] │
     │ ... 61 more rows                                                                                   │
     └───────────────────────────────┴──────────┴─────────┴────────────┴─────────────┴─────────────────────┘
          ▲                              ▲                                              ▲
          │                              │                                              │
   ┌──────┴──────┐              ┌────────┴────────┐                          ┌──────────┴──────────┐
   │ KEYWORD     │              │ FILTERS         │                          │ HNSW GRAPH          │
   │ (inverted)  │              │ fast lookups on │                          │ over chunk_vector   │
   │ index on    │              │ filterable      │                          │ (see picture 3)     │
   │ "chunk",    │              │ fields: dates,  │                          │                     │
   │ "title"     │              │ audience, type  │                          │                     │
   └─────────────┘              └─────────────────┘                          └─────────────────────┘
        three lookup structures, built automatically, all pointing at the SAME rows
```

Think of it as **one SQL table with three indexes on different columns**:

| Lookup structure | Built from | Used for | Database equivalent |
| --- | --- | --- | --- |
| Inverted index | `chunk`, `title`, `section` (searchable fields) | Keyword search: "which rows contain *approve*?" | A full-text index |
| Filter structures | `doc_type`, `effective_from`, `audience`… (filterable fields) | "Only rows in force on Oct 15" | A B-tree index on a column |
| HNSW graph | `chunk_vector` | "Which rows *mean* something close to this question?" | A vector index (e.g. pgvector's HNSW) |

The word "index" is confusing here. Azure uses it for the whole table, while these three structures are what a database would call indexes. All three are built **automatically** as the indexer writes each row; the field attributes in `index.json` (`searchable`, `filterable`, `vectorSearchProfile`) decide which structure each field goes into.

### Picture 3: the HNSW graph links vectors by meaning

Each dot is one row's `chunk_vector`. Similar meanings sit close together and are linked:

```
 Layer 1 (highways: few dots, long links)

        pol_3.1 ●───────────────────────────────────● tpl_fr
                │                                   │
 ───────────────┼───────────────────────────────────┼─────────────────────────

 Layer 0 (every one of the 66 dots, linked to near neighbours)

    "time limits"                 "approval"                     "letters"
   pol_3.1 ●──● pol30_3.1      pol_3.3 ●──● pol30_3.3        tpl_fr ●──● tpl_en
           │ ╲                         │ ╲                          │
  faq_02 ●─┘  ● net_3          faq_08 ●──● sop_highvalue    tpl_denied ●
                                       │
    "duplicates"                       │                      "fraud"
   pol_4.2 ●──● sop_dup               │                     sop_fraud ●──● pol_4.1
           │                          │                                 │
     net_2 ●                    sop_review ●                     faq_05 ●
```

`faq_08` ("who approves exactly $1,000?") ends up linked to the policy's approval sections and the high-value SOP, because their **meanings** are close. Nobody configured that: the embedding model placed them near each other, and HNSW linked near neighbours as each row was written. (The picture is a simplified illustration, not the real graph.)

### Picture 4: the whole journey

```
 GIT                    BLOB STORAGE              AZURE AI SEARCH
 ───                    ────────────              ───────────────

 analyst-faq.md                                   ┌─ INDEX maple-knowledge-v1 ─────────┐
      │                                           │                                     │
 chunk_docs.py                                    │  row: faq_08  text│meta│vector ──┐  │
      │                                           │  row: faq_09  text│meta│vector   │  │
      ├─► faq_01.json ─┐                          │  row: pol_3.3 text│meta│vector   │  │
      ├─► ...          ├─► knowledge/ ──► INDEXER ┤  ... 66 rows                     │  │
      └─► faq_10.json ─┘   container      │       │                                  ▼  │
                                          │       │  HNSW graph ● ─ ● ─ ●  (links    │  │
                                          │       │             ●   ● ─ ●   vectors) │  │
                                     SKILLSET     └─────────────────────────────────────┘
                                     embeds text          ▲
                                          │               │  search: question → vector →
                                          ▼               │  walk graph → nearest rows
                                 text-embedding-3-small   │
                                 (Foundry, East US 2)  AGENT (Day 8)
```

### The corrected sentence

> ~~We vectorize the chunk and get a vector and its metadata, which makes an index for that chunk, then it's stored in an AI Search vector database.~~

> "Each chunk is vectorized, and its text, metadata and vector are written as **one row** in **the index**. The index is the single table holding all 66 rows; it lives in Azure AI Search, which *is* the vector store. As each row is written, Azure links its vector into the index's **HNSW graph**, and the text and metadata go into the keyword and filter structures, so one query can search by meaning, by words and by filters together."

> **AWS:** an OpenSearch index works the same way: one document per chunk, with text fields, metadata fields and a `knn_vector` field backed by HNSW, all in one index.

---

## Stage 4: Searching it, end to end

An analyst's agent asks: **"Who approves a credit of 2500 dollars?"**, for an analyst.

```
                         "Who approves a credit of 2500 dollars?"
                                        │
                ┌───────────────────────┴───────────────────────┐
                ▼                                               ▼
       KEYWORD SEARCH (BM25)                           VECTOR SEARCH
       words + synonym map                             vectorizer embeds the question
       "approves", "credit"...                         → HNSW walk → nearest chunk vectors
                │                                               │
                └───────────────────────┬───────────────────────┘
                                        ▼
                          FILTER (applied to both)
                          audience/any(a: a eq 'analyst')
                          → the senior-only SOP can't appear
                                        ▼
                          MERGE: Reciprocal Rank Fusion
                          chunks ranked high in BOTH lists rise to the top
                                        ▼
                          SEMANTIC RANKER
                          a language model re-reads the top results
                          and scores how well each answers the question (0–4)
                                        ▼
                          TOP 3
   2.43  policy/card-dispute-policy.md               v3.1  3.3 Approval
   2.33  policy/archive/card-dispute-policy-v3.0.md   v3.0  3.3 Approval
   2.22  faq/analyst-faq.md                           v1.4  Who approves a credit of exactly $1,000.00?
```

The FAQ_08 chunk we followed comes back third: chunked by our script, embedded by the skillset, found by the HNSW walk, kept by the filter, re-ranked by the semantic ranker.

Notice that **both policy versions** came back. The agent would also pass the transaction date as a filter, so only the version in force would appear. Vector similarity can't tell two near-identical versions apart; **metadata can**. That's why the chunker copies the effective dates onto every chunk.

**Why hybrid and not just vectors?** Vectors are weak on exact tokens: "NS-12.1", "$1,000", "4.2". Keywords are weak on paraphrase: "charged twice" vs "duplicate". Hybrid gets both, and Reciprocal Rank Fusion merges the two lists by *rank*, not raw score, because the two scores aren't on the same scale.

---

## Stage 5: When a document changes

| Change | Chunking | Vectorizing | HNSW |
| --- | --- | --- | --- |
| A FAQ answer is edited | That question's chunk file changes | The indexer sees a newer last-modified time on that blob and re-embeds **only that chunk** | The old vector is replaced; the graph is updated in place |
| A FAQ question is removed | One fewer chunk file; `apply.sh` deletes the leftover blob | Soft delete + deletion detection tells the indexer the blob is gone | That document and its vector are removed from the index and graph |
| A new FAQ question is added | One more chunk file | Embedded on the next run | A new point is linked into the graph |
| **The embedding model changes** | Same chunks | **Every** chunk must be re-embedded | New dimensions / new "map": **a new index** (`v2`), tested, then the alias is switched |
| The chunking rules change | Different chunks everywhere | Everything re-embedded | Best done as a new index too (blue/green) |

Cost scales with **what changed**, not the size of the knowledge base, because the indexer tracks which blobs it has already processed.

---

## Interview questions on this topic

**"Walk me through how a document becomes searchable."**
> Our pipeline chunks it by section in Python, adding metadata to each chunk, and uploads the chunks as JSON to Blob Storage. An Azure AI Search indexer pulls each blob, runs a skillset that embeds the chunk text with text-embedding-3-small, and writes the text, metadata and vector to the index. The index stores the vector field with HNSW, so each vector is linked into the graph as it's written.

**"What chunk size do you use?"**
> No fixed size: one chunk per policy section, because sections are the unit people cite, with small documents kept whole and a 3,000-character safety split. If the documents had no structure, I'd use fixed-size chunks of a few hundred tokens with 10–20% overlap, and tune it with retrieval tests.

**"How would you know if your chunking is bad?"**
> Retrieval tests. We have 40 labelled questions with a hit@3 gate of 90%. If chunks are too big, the right section is buried and scores drop; if they're too small, chunks lose context and similar ones crowd the top 3. A chunking change is built as a new index and must pass the tests before the alias switches.

**"What's HNSW and why use it?"**
> A layered nearest-neighbour graph for approximate nearest-neighbour search: highway layers on top for long jumps, every vector on the bottom layer. It's much faster than comparing against every vector, at the cost of rare misses. You tune recall against speed and memory with m, efConstruction and efSearch.

**"Why do you need the same model for indexing and querying?"**
> Each model maps text into its own vector space. A question embedded with model B can't be compared to chunks embedded with model A; the distances would be meaningless. So changing models means re-embedding everything into a new index.

**"What if the embedding service is down during indexing?"**
> The indexer records which documents failed and retries them on the next run; our script fails the pipeline if `itemsFailed` isn't zero or the document count doesn't match the chunk count. The live index isn't touched by a failed build if you're building a new version behind the alias.

**"Where is the vector stored?"**
> In the index itself, in a `Collection(Edm.Single)` field with 1,536 dimensions. There's no separate vector database: Azure AI Search stores text, metadata and vectors in one index and searches them together. We mark the vector field non-retrievable, so it's used for search but not returned in results.
