# Day 4: Agent Instructions, Tool Descriptions, and Debugging Tool Calls

Notes for Day 4 of the [prep plan](../ai-platform-engineer-prep-plan.md) ("Agent quality"). Day 3 (durable data) was skipped for now.

**What we did today:**
1. Rewrote the **agent instructions** so the model knows exactly when to use each tool.
2. Rewrote the **tool descriptions**, parameter descriptions and error messages on the MCP server.
3. Added **guardrails**: only an explicit list of tools is allowed, and deletes need confirmation in risky cases.
4. Learned how to **debug** whether the agent called the right tool, using traces.

---

## Part 1: How a model decides which tool to call

### The model only sees text

The model never sees your C# code. On every request, Foundry sends it three things:

1. **The instructions** (the "system prompt"): who it is and the rules it must follow.
2. **The tool list:** for each tool, its **name**, its **description**, and a **schema** of its inputs, with each parameter's name, type and description.
3. **The conversation so far,** including the results of any tools it already called.

From that alone, the model writes either a reply to the user, or a **tool call**: "call `delete_todo_item` with `{"id": 4}`".

Here is roughly what the model receives for one tool. This is generated from our `[McpServerTool]` and `[Description]` attributes:

```json
{
  "name": "delete_todo_item",
  "description": "Permanently removes one item from the list. This cannot be undone. Use only when the user explicitly asks to remove, delete or get rid of an item. Do not use it when the user finished a task; use complete_todo_item for that. Get the ID from get_todo_items first. Returns the deleted item's ID and text.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "id": { "type": "integer", "description": "ID of the item to delete, taken from get_todo_items." }
    },
    "required": ["id"]
  },
  "annotations": { "destructiveHint": true, "readOnlyHint": false, "idempotentHint": true }
}
```

So **yes: the right description is how the model knows which tool to call.** The name, description and parameter descriptions are the *only* information it has about a tool.

### The tool-calling loop

One user request can involve several rounds:

```
 User: "Remove the buy milk task"
   │
   ▼
 Model reads instructions + tool list + conversation
   │  decides: I need the ID first
   ▼
 Tool call: get_todo_items()            ──▶ MCP server ──▶ "ID: 4, Text: buy milk, ..."
   │
   ▼
 Model reads the result
   │  decides: item 4 is "buy milk"; the user said "remove" → delete
   ▼
 Tool call: delete_todo_item({"id": 4}) ──▶ MCP server ──▶ "Deleted item 4: 'buy milk'."
   │
   ▼
 Model reads the result
   │  decides: done, tell the user
   ▼
 Reply: "Deleted 'buy milk' (ID 4)."
```

Two things follow from this loop:
- **Tool results are instructions too.** The model reads every result to decide its next step. A clear result ("Not deleted: there is no item with ID 9. Call get_todo_items to see the current IDs.") lets it fix its own mistake. A vague one ("error") doesn't.
- **Each round costs time and tokens.** Good descriptions also mean fewer wasted calls.

> **AWS:** Bedrock Agents work the same way. The model chooses an action group's function from its name, description and parameter descriptions. Everything in these notes applies there too.

### Who sends the tools to the model, and when

For a **hosted agent** like ours, Foundry does **not** store the instructions or the MCP tool in the agent's definition. The stored definition only has the container image and environment variables. Instead, **our container's code adds them to every request**, and Foundry fetches the tool descriptions from the MCP server.

Here is the full chain for one chat message:

```
 Gateway (agent app)
   │  POST …/agents/todo-agent/…/responses
   │  { input: "I finished buy milk" }          ← only the user's message
   ▼
 Hosted todo-agent container (our code, HostedAgent/Program.cs)
   │  builds a Responses request to Foundry with:
   │    model:        gpt-5-mini
   │    instructions: the Role / Choosing a tool / Safety… text
   │    tools: [ { type: mcp,                    ← the MCP SERVER, not each tool
   │               server_label: "mcp-todo",
   │               server_url:   "https://mcp-todo…/mcp",
   │               headers:      { x-api-key: … },
   │               allowed_tools: [get_todo_items, add_todo_item, …],
   │               require_approval: never } ]
   ▼
 Foundry Responses runtime
   │  ① asks the MCP server: "what tools do you have?"   (MCP tools/list)
   │     ← names, descriptions, input schemas, annotations   (from TodoTool.cs)
   │     ⚠ the answer is CACHED for a while (see the Day 5 correction below)
   │  ② keeps only the allowed_tools
   │  ③ calls the model with: instructions + tool definitions + conversation
   │  ④ model asks for a tool → Foundry calls the MCP server (tools/call)
   │  ⑤ calls the model AGAIN with the result, plus instructions and tool definitions again
   │  … until the model writes a final answer
   ▼
 Reply goes back to the hosted agent → gateway → user
```

**Three things to remember:**

**1. The hosted agent sends the MCP server on every request.** Each time the container handles a message, it builds a fresh request containing the instructions and the MCP tool (`HostedMcpServerTool` becomes the `tools` entry). If the code dropped the tool, the very next request simply wouldn't have it.

**2. It sends the *server*, not the tool descriptions.** Our code only says *"here's an MCP server: its URL, its key header, and which tools are allowed"*. The descriptions live in the **MCP server** (`TodoTool.cs`), and Foundry fetches them at run time (step ①). That means there are two separate deployments:

| You change...                              | Lives in                                | Redeploy                      | Picked up                                                                          |
| ------------------------------------------ | --------------------------------------- | ----------------------------- | ---------------------------------------------------------------------------------- |
| Tool names, descriptions, parameters       | MCP server (`TodoTool.cs`)              | `resources-mcp` (stage 1)     | **Not immediately:** when Foundry's cached tool list refreshes (we saw ~10–15 min) |
| Instructions, allowed tools, approval mode | Hosted agent (`HostedAgent/Program.cs`) | `resources-foundry` (stage 2) | When the new agent version starts                                                  |

So a change touching both should be **released and tested together**.

> ⚠️ **Correction found on Day 5: Foundry caches the tool list.** We broke a tool description on purpose and then restored it. The MCP server served the fixed description at once, but the agent kept using the **broken** one for about 12 more minutes, even after we deployed a new agent version. App Insights showed why: Foundry opened dozens of MCP sessions in that time but called `tools/list` only twice. So step ① does **not** happen on every request. Foundry keeps the tool list for a while (we measured roughly 10–15 minutes; Microsoft doesn't document the exact time) and the cache is keyed to the MCP server, not to the agent version.
>
> What it means for DevOps:
> - After deploying a tool-description change, **wait out the cache before running evaluations**, or the gate tests the old descriptions and passes or fails for the wrong reason.
> - **A rollback of a bad description is not instant either.** Users keep getting the bad behaviour until the cache refreshes. For a really bad tool, removing it from `allowed_tools` in the agent (redeploy stage 2) takes effect when the new agent version starts, which is faster.
> - Details and the timeline: [Day 5 notes, Part 6](day-05-evaluation-and-quality-gate.md#part-6-what-we-built).

**3. The model receives the tool definitions on *every* model call, not once.** Models are **stateless**: each call is independent and remembers nothing. In one request with two tool rounds (list, then complete), the model is called three times, and **each call carries the instructions, all tool definitions and the conversation so far** (steps ③ and ⑤). That has costs:
- Tool definitions are **input tokens on every call**. Detailed descriptions for five tools may be a few hundred tokens, paid again each round.
- So write descriptions that are **clear but not padded**: every sentence should help the model choose.
- **Prompt caching** softens this. Identical text at the start of a call is charged at a discount; you can see it in traces as `gen_ai.usage.cache_read.input_tokens`. Keeping instructions and tool definitions stable, and at the start, helps caching work.

**You can see step ③'s result in the traces:** `gen_ai.tool.definitions` on Foundry's `chat` span (`cloud_RoleName == "responsesapi"`) is the tool list Foundry fetched from the MCP server, after applying `allowed_tools`. This is also why the Foundry portal's agent page doesn't list these tools for a hosted agent: they aren't stored there. You verify them through the playground and traces instead.

### Hosted agent vs prompt agent

This repo also has a **prompt agent** script ([PromptAgent/create-agent.cs](../../src/MafStarterPack.PromptAgent/create-agent.cs)). It stores the instructions and the MCP tool **inside Foundry's agent definition**, with no container. The caller sends only "use agent X" plus the message, and Foundry adds the stored instructions and tools itself.

|                                                    | Hosted agent (ours)                            | Prompt agent                        |
| -------------------------------------------------- | ---------------------------------------------- | ----------------------------------- |
| Who adds instructions + MCP server to each request | Our container's code                           | Foundry, from the stored definition |
| Shown on the agent's portal page                   | No                                             | Yes, under Tools                    |
| Where tool descriptions come from                  | The MCP server, fetched at run time            | The MCP server, fetched at run time |
| What the model receives on each call               | Instructions + tool definitions + conversation | Same                                |

Either way, the model sees the same kind of input. Only **who supplies it** differs.

> **Interview point:** *"With hosted agents, the tool configuration is part of your code and deployment, so you verify it through telemetry (`gen_ai.tool.definitions`), not the portal's settings page."*

### What goes wrong when the text is vague

| Problem in the text                                                       | What the model does                                    |
| ------------------------------------------------------------------------- | ------------------------------------------------------ |
| Two tools sound alike ("Completes a to-do item" / "Deletes a to-do item") | Picks one at random for "I'm done with buy milk"       |
| No "when to use"                                                          | Guesses from the name alone                            |
| No "get the ID first" rule                                                | Guesses an ID like `1`, or reuses an old one           |
| Instructions mention things no tool does, or miss things a tool does      | Claims abilities it doesn't have, or never uses a tool |
| Error results without a next step                                         | Gives up, or claims success anyway                     |

---

## Part 2: The instructions: before and after

### Before

```text
You are a helpful assistant for managing a todo list.
You can add, remove, and list tasks in the todo list.
```

What was wrong:
- It never mentions **update** or **complete**, so the model had to discover them from the tool list.
- It doesn't say how to tell **done** from **delete**.
- Nothing about **IDs**: no "look it up first", no "what if two items match".
- No **safety** rules: nothing stops it deleting the wrong thing, or saying "done" when a tool failed.
- No rules on **reply format** or **scope**.

### After

In [HostedAgent/Program.cs](../../src/MafStarterPack.HostedAgent/Program.cs). The structure is what matters; each section fixes one of the problems above:

| Section              | What it says                                                                                                                              | Why                                                        |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| **Role**             | Manage the to-do list; lists all five abilities                                                                                           | The model knows its whole job, matching the real tools     |
| **Choosing a tool**  | A mapping from the user's words to tools: "done / finished" → complete; "remove / delete" → delete; if unclear, ask                       | Removes the done-vs-delete guess                           |
| **Working with IDs** | Always call `get_todo_items` first; match by text; if several match, list them and ask                                                    | Stops guessed IDs and silent wrong picks                   |
| **Safety**           | Only delete what was clearly asked; confirm when several items or "everything" could match; never claim success unless the tool succeeded | Prevents the dangerous mistakes                            |
| **Replies**          | One or two sentences, plus the affected items with IDs                                                                                    | Consistent, checkable answers                              |
| **Scope**            | Only the to-do list; politely decline anything else                                                                                       | Keeps the agent on task, and makes prompt injection harder |

**Tips for writing instructions:**
- **Be specific, not polite.** "Before updating, completing or deleting, call get_todo_items" beats "Please be careful with IDs".
- **Map the user's words to tools.** Users say "ticked off", not "complete_todo_item".
- **Say what to do when unsure:** usually "ask".
- **Keep instructions and tool descriptions consistent.** If they disagree, the model gets confused.
- **Don't repeat every tool detail.** The instructions set the *rules*; the tool descriptions explain each *tool*.

---

## Part 3: The tool descriptions: before and after

In [McpTodo/TodoTool.cs](../../src/MafStarterPack.McpTodo/TodoTool.cs).

### Before

```text
add_todo_item       "Adds a to-do item to database."
get_todo_items      "Gets a list of to-do items from database."
update_todo_item    "Updates a to-do item in the database."
complete_todo_item  "Completes a to-do item in the database."
delete_todo_item    "Deletes a to-do item from the database."
parameter id        "The to-do item ID"
```

These describe **what the code does to the database**, not **when the model should use them**. "Updates" vs "completes" vs "deletes" gives the model no way to choose between them for "I'm done with buy milk".

### The pattern for a good description

Every new description answers four questions:

1. **What does it do?** In the user's terms: "Marks one existing item as completed. The item stays on the list."
2. **When should it be used?** "Use when the user says they did, finished, completed or ticked off a task."
3. **When should it *not* be used, and what instead?** "Do not use it when the user wants the item gone; use delete_todo_item for that."
4. **What does it return,** and what must happen first? "Get the ID from get_todo_items first. Returns the item's ID and text."

The "when not to use" line matters most. It tells the model the **difference** between similar tools.

### The new descriptions, side by side

| Tool                 | New description, short version                                                                         | Annotations                                   |
| -------------------- | ------------------------------------------------------------------------------------------------------ | --------------------------------------------- |
| `add_todo_item`      | Adds one new item. For "add / remember / note down". One call per item. Not for changing an item.      | not read-only, not destructive                |
| `get_todo_items`     | Lists all items with ID, text, done. **Always call first before changing an item. Never guess an ID.** | **read-only**, idempotent                     |
| `update_todo_item`   | Replaces an item's text. For "rename / reword / correct". Not for done or delete.                      | **destructive** (overwrites text), idempotent |
| `complete_todo_item` | Marks an item done; **it stays on the list**. For "did / finished / ticked off". Not for removing.     | not destructive, idempotent                   |
| `delete_todo_item`   | **Permanently** removes an item. Only on an explicit "remove / delete". Not for finished tasks.        | **destructive**, idempotent                   |

### Parameter descriptions

The model fills in parameters from their descriptions too:

| Parameter               | Before                | After                                                                                              |
| ----------------------- | --------------------- | -------------------------------------------------------------------------------------------------- |
| `id`                    | "The to-do item ID"   | "ID of the item to delete, **taken from get_todo_items**."                                         |
| `todoItemText` (add)    | "The to-do item text" | "The task text, **exactly as the user wants it written**, e.g. "buy milk". **Must not be empty.**" |
| `todoItemText` (update) | "The to-do item text" | "The **complete new text** for the item, **replacing the old text**."                              |

"Replacing the old text" prevents a subtle mistake: the model sending only the *changed words* instead of the full new text.

### MCP tool annotations

`ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld` are standard **MCP tool annotations**, set on `[McpServerTool]`. They're labels about a tool's behaviour:

| Annotation           | Means                                                         | Used for                                                           |
| -------------------- | ------------------------------------------------------------- | ------------------------------------------------------------------ |
| `ReadOnly = true`    | Changes nothing                                               | Safe to call freely, or to retry                                   |
| `Destructive = true` | Can remove or overwrite data                                  | Clients and platforms can require approval for these automatically |
| `Idempotent = true`  | Calling twice with the same input has the same effect as once | Safe to retry after a timeout                                      |
| `OpenWorld = false`  | Only touches this server's own data, not the outside world    | Risk assessment                                                    |

They're **hints, not enforcement.** The MCP server must still validate everything itself.

### Results and error messages

| Situation         | Before                                                                   | After                                                                                      |
| ----------------- | ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------ |
| Item not found    | "Todo item with ID '9' not found."                                       | "Not deleted: there is no item with ID 9. **Call get_todo_items to see the current IDs.**" |
| Empty text        | Accepted an empty item                                                   | "Not added: the task text is empty. **Ask the user what the task should say.**"            |
| Successful update | "Todo item updated: '2' with text: 'MafStarterPack.McpTodo.TodoItem'" ❌ | "Updated item 2: the text is now 'buy oat milk'."                                          |
| Successful delete | "Todo item deleted: '4'"                                                 | "Deleted item 4: 'buy milk'."                                                              |

The update message was a **real bug**: it printed the C# class name instead of the new text. The model read that, and could have told the user something strange. Including the item's **text** in every result also lets the model double-check that it acted on the right item.

**Rule:** every error result should say **what went wrong and what to do next**. The model will usually follow it.

---

## Part 4: Guardrails

Instructions make mistakes *less likely*. Guardrails limit the *damage* when one happens anyway.

### 1. An explicit list of allowed tools

```csharp
AllowedTools = [ "get_todo_items", "add_todo_item", "update_todo_item", "complete_todo_item", "delete_todo_item" ],
```

If someone adds a new tool to the MCP server later (say `delete_all_items`), the agent **can't use it** until it's added here deliberately, in a reviewed change. This is least privilege for tools.

### 2. Approval for destructive tools: the option we didn't take (yet)

`HostedMcpServerTool.ApprovalMode` has three settings:

| Mode                                           | Effect                                               |
| ---------------------------------------------- | ---------------------------------------------------- |
| `NeverRequire`                                 | Tools run straight away (what we use)                |
| `AlwaysRequire`                                | Every tool call pauses until a person approves it    |
| `RequireSpecific(always: [...], never: [...])` | Only the listed tools pause, e.g. `delete_todo_item` |

With approval required, Foundry doesn't run the tool. It sends back an **approval request**, and the *client* must show it to the user and send their answer back. Our gateway and web UI don't support that yet, so a delete would simply never finish.

So for now, deletes are protected by **instructions** (confirm when ambiguous). The production answer is `RequireSpecific(always: ["delete_todo_item"])` **plus** approval support in the UI. That's a good interview point: *"a guardrail the client can't handle is the same as a broken feature."*

### 3. Validation in the tool's code

The model *should* never send empty text, but the tool **checks anyway** and returns a helpful error. **Prompts reduce mistakes; code prevents them.** Never rely on the model alone for anything that matters.

---

## Part 5: Shipping prompt changes like code

Instructions and descriptions **are code** from a DevOps point of view: they change behaviour in production. Treat them that way:

| Practice                               | In this project                                                                                     |
| -------------------------------------- | --------------------------------------------------------------------------------------------------- |
| Stored in git, changed by pull request | Instructions in `HostedAgent/Program.cs`, descriptions in `TodoTool.cs`                             |
| Each deploy is versioned               | Each `azd deploy` creates a new Foundry agent version, so you can roll back to the previous version |
| Tested before release                  | The test prompts in Part 7 (later: an automated evaluation gate, Day 5 of the plan)                 |
| Watched after release                  | The tool-mix and tool-failure queries in Part 6                                                     |

**Watch out:** the instructions live in the **hosted agent**, but the descriptions live in the **MCP server**. They're deployed separately (stage 2 vs stage 1), so a change to both should be released together and tested together.

---

## Part 6: Debugging: did the agent call the right tool?

Never trust the agent's **reply** to tell you what it did; check what was **actually executed**. Foundry records every tool call in the traces, with its arguments and result.

> ⚠️ **Update (Day 5, Part C):** since App Insights was recreated on 2026-10-26, Foundry's runtime spans (`cloud_RoleName == "responsesapi"`, `execute_tool`) no longer arrive, so the queries below return nothing for now. The MCP server's own spans (`tools/call <tool>`, grouped per turn by `operation_Id`) show the same tool calls, without arguments. See [day-05-quality-signals.md](day-05-quality-signals.md) Part 2.

### The method, step by step

**Step 1: Write down what *should* happen.** For "remove buy milk": `get_todo_items`, then `delete_todo_item` with the ID of "buy milk". Without an expected answer you can't call anything wrong.

**Step 2: Get what *actually* happened.** Every gateway response has an `x-trace-id` header. In App Insights → Logs:
```kusto
dependencies
| where operation_Id == "<x-trace-id>" and name startswith "execute_tool"
| project timestamp,
          tool   = tostring(customDimensions["gen_ai.tool.name"]),
          args   = tostring(customDimensions["gen_ai.tool.call.arguments"]),
          result = tostring(customDimensions["gen_ai.tool.call.result"])
| order by timestamp asc
```

**Step 3: Compare, and name the kind of mistake.**

| You see                                    | Kind of mistake        | Usual cause                                      | Usual fix                                                       |
| ------------------------------------------ | ---------------------- | ------------------------------------------------ | --------------------------------------------------------------- |
| A different tool than expected             | **Wrong tool**         | Overlapping or vague descriptions                | Add "when not to use" lines                                     |
| Right tool, wrong ID or text               | **Wrong arguments**    | Didn't look the item up first; ambiguous request | "Call get_todo_items first"; "ask if several match"             |
| No tool call, but the reply claims success | **Made-up action**     | Model answered from memory                       | "Never say an action happened unless the tool reported success" |
| The same tool called again and again       | **Loop**               | A result the model doesn't understand            | Clearer result and error text                                   |
| Right tool, but the result is an error     | **Tool failure**       | Bug or bad input in the tool                     | Fix the tool; clearer error message                             |
| Right calls, wrong final reply             | **Misreported result** | Model misread the result                         | Include the item's text in results                              |

The **made-up action** is the most dangerous, because users trust the reply. Only the trace can catch it.

**Step 4: See *why* the model chose it.** On Foundry's `chat` span (`cloud_RoleName == "responsesapi"`), these attributes show what the model was given:
```kusto
dependencies
| where operation_Id == "<x-trace-id>" and cloud_RoleName == "responsesapi" and name startswith "chat"
| project timestamp,
          finish   = tostring(customDimensions["gen_ai.response.finish_reasons"]),
          messages = tostring(customDimensions["gen_ai.input.messages"]),
          tools    = tostring(customDimensions["gen_ai.tool.definitions"])
```
- `gen_ai.input.messages`: the conversation the model saw.
- `gen_ai.tool.definitions`: the tool names and descriptions it chose from. **Check that the new descriptions are really there** after a deploy.
- `gen_ai.response.finish_reasons`: why it stopped. `stop` = finished normally; `length` = cut off.

**Step 5: Reproduce it, and measure how often.** Models aren't deterministic. Send the same prompt 10–20 times and count how often the tool sequence is right. "Wrong 3 times out of 20" is a real bug; "wrong once, never again" may be noise.

**Step 6: Fix the cause,** using the "usual fix" column. Change the *smallest* thing: often one "when not to use" line.

**Step 7: Stop it coming back.** Add the prompt and its expected tool sequence to your test set (Part 7). Every future change to instructions, descriptions or the model is checked against it.

### Watching for tool problems in production

You can't read every trace, so watch the numbers:

```kusto
// Which tools are being called, per day. A sudden change after a deploy is a warning sign.
dependencies
| where name startswith "execute_tool"
| summarize calls = count() by tool = tostring(customDimensions["gen_ai.tool.name"]), day = bin(timestamp, 1d)
| order by day desc, calls desc
```

```kusto
// Tool errors, including errors reported inside a "successful" call
dependencies
| where name startswith "execute_tool"
| extend result = tostring(customDimensions["gen_ai.tool.call.result"])
| where success == false or result startswith "Not " or result has_any ("error", "not found")
| project timestamp, operation_Id, tool = tostring(customDimensions["gen_ai.tool.name"]), result
```

The second query uses the new error messages: every failure now starts with **"Not …"** ("Not deleted:", "Not added:"). **Consistent error text makes monitoring easy.** That's a side benefit of rewriting the descriptions.

What to alert on (see Day 2 of the plan, and the alerts discussion):
- **Delete calls rising sharply** after a deploy: the model may be choosing delete instead of complete.
- **More than ~5 tool calls in one request:** a loop.
- **The tool error rate** going above a threshold.

---

## Part 7: Test prompts and results

A small test set. Each prompt has an **expected** tool sequence to compare against the trace.

| #   | Prompt                                                          | Expected tool calls                                                            | Tests                              |
| --- | --------------------------------------------------------------- | ------------------------------------------------------------------------------ | ---------------------------------- |
| 1   | "Add buy milk and call the bank"                                | `add_todo_item` × 2                                                            | One call per item                  |
| 2   | "I finished buy milk"                                           | `get_todo_items` → `complete_todo_item(<buy milk>)`                            | Done vs delete                     |
| 3   | "Remove the call the bank task"                                 | `get_todo_items` → `delete_todo_item(<call the bank>)`                         | Explicit delete                    |
| 4   | "Rename buy milk to buy oat milk"                               | `get_todo_items` → `update_todo_item(<id>, "buy oat milk")`                    | Full new text; the update bug fix  |
| 5   | Two items called "interview prep", then "delete interview prep" | `get_todo_items`, then **a question**, no delete                               | Ambiguity → ask                    |
| 6   | "Delete item 99"                                                | `get_todo_items` and/or `delete_todo_item(99)` → "Not deleted…" → honest reply | Error handling, no made-up success |
| 7   | "What's the weather?"                                           | No tool call; politely declines                                                | Scope                              |

**Results:** *(filled in after deploying)*

---

## Part 8: Interview drill

**"How does an LLM decide which tool to call?"**
> "It only sees text: the instructions, each tool's name, description and input schema, and the conversation, including earlier tool results. It picks the tool whose description best matches the request. So tool descriptions are effectively the API documentation for the model. I write each one to say what it does, when to use it, when *not* to use it and what to use instead, and what it returns."

**"Your agent sometimes deletes items when the user just finished them. What do you do?"**
> "First I confirm it from traces, not replies: query the `execute_tool` spans for those requests and check the tool and arguments. Then I look at the tool definitions the model saw. Usually 'complete' and 'delete' read alike. The fix is a 'when not to use' line in each description, plus a word-to-tool mapping in the instructions. I reproduce the prompt 20 times before and after to measure the improvement, and add it to the regression test set. For safety I'd also require approval for the delete tool."

**"How do you stop an agent claiming it did something it didn't?"**
> "An instruction to only report success when the tool reports it. Tool results that clearly say success or failure, including the item's text. And monitoring that compares claimed actions in replies with actual `execute_tool` spans, because only the trace shows what really ran."

**"What guardrails would you put around an agent's tools?"**
> "Least privilege for tools: an explicit allow-list, so new tools need a reviewed change. Approval for destructive tools, which means the client must support approval requests. Validation inside the tool, since prompts reduce mistakes but code prevents them. And MCP annotations like `destructiveHint`, so the platform knows which tools are risky."

**"How do you release a prompt change safely?"**
> "It's code: it's in git, changed by pull request, and tested against a fixed set of prompts with expected tool sequences before deploying. Each deploy creates a new agent version, so I can roll back. After release I watch the tool mix and the tool error rate for changes. And because instructions and tool descriptions are deployed separately here, I release and test them together."

---

## Glossary

| Term                             | Plain meaning                                                                |
| -------------------------------- | ---------------------------------------------------------------------------- |
| **Instructions / system prompt** | The rules the model gets at the start of every request                       |
| **Tool definition**              | A tool's name, description and input schema, as the model sees them          |
| **Input schema**                 | The list of a tool's parameters, with type and description (JSON Schema)     |
| **Tool call**                    | The model's request to run a tool with specific arguments                    |
| **Tool result**                  | The text the tool returns, which the model reads next                        |
| **MCP tool annotations**         | Labels like `readOnlyHint` / `destructiveHint` describing a tool's behaviour |
| **Allowed tools**                | The explicit list of tools an agent may use from a server                    |
| **Approval mode**                | Whether a tool call must be approved by a person before it runs              |
| **Made-up action**               | The model claims it did something without calling the tool                   |
