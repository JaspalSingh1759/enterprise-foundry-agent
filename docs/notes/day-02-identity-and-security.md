# Day 2: Identity and Security, from a DevOps Point of View

Notes for Day 2 of the [prep plan](../ai-platform-engineer-prep-plan.md).

**Today in one sentence:** we treated the project like a security review had just come back with four findings, and fixed each one in infrastructure code.

| # | Finding | Fix |
|---|---|---|
| 1 | Three apps share **one identity**, so each has the others' permissions | Give **each app its own identity** with only what it needs |
| 2 | The MCP server is **on the internet with no login** | Require an **API key** on every request |
| 3 | There's **no proper place for secrets** | Store the key in **Key Vault**; apps read it with their identity |
| 4 | Permissions are granted by **shell scripts** | Declare them in **Bicep**, like the rest of the infrastructure |

Each part below explains one fix: what was wrong, what we built, and how it works. AWS comparisons are included throughout.

---

## Part 1: Four ideas you need first

### 1.1 A managed identity is an app's "IAM role"

An app running in Azure often needs to call other Azure services: pull its image, read a secret, call a model. It needs to prove who it is, but you don't want a password in its config.

A **managed identity** solves this. Azure creates an identity for the app and handles its credentials. In code you just use `DefaultAzureCredential`, and it signs in automatically.

> **AWS:** a managed identity is the **IAM role you attach to an ECS task** (the task role). The container gets temporary credentials automatically; you control what it can do by what you attach to the role.

This project uses **user-assigned** identities: separate resources that Bicep creates *before* the app, so their permissions are ready when the app starts. (The other kind, *system-assigned*, is created with the app and deleted with it.)

### 1.2 Every permission is "who + what + where"

Recap from Day 1. A **role assignment** always has three parts:

```
who:   id-agent-6a3q2sputpvay          (an identity)
what:  Azure AI User                   (a role: a list of allowed actions)
where: the Foundry account             (the scope)
```

The **scope** can be a subscription, a resource group, one resource, or even **one secret inside a Key Vault**. The smaller the scope, the less a compromised app can touch.

> **AWS:** "what" is the actions in an IAM policy, "where" is the policy's `Resource` (for example one specific secret's ARN).

### 1.3 Control plane vs data plane

Azure has two kinds of permission, and mixing them up is a classic mistake:

| | Control plane | Data plane |
|---|---|---|
| **Means** | Managing a resource | Using what's *inside* it |
| **Key Vault example** | Create the vault, change its settings | Read a secret |
| **Foundry example** | Create the account, deploy a model | Call the model, run an agent |
| **Typical roles** | Contributor, Owner | Key Vault Secrets User, Azure AI User |

**Contributor gives no data-plane access.** A pipeline with Contributor can create a Key Vault but can't read its secrets. Apps only ever need data-plane roles.

> **AWS:** like `kms:CreateKey` vs `kms:Decrypt`, or `s3:CreateBucket` vs `s3:GetObject`.

### 1.4 Least privilege

**Least privilege** means each identity gets only the roles its job needs, at the smallest scope that works. The question to ask is: *"if this app were taken over tomorrow, what could the attacker do?"* The answer should be "only what that app does anyway".

---

## Part 2: Fix 1: one identity per app

### What was wrong

All three container apps ran as the same identity, `id-mcp-todo-<token>`:

```
                 id-mcp-todo-*  ──▶ AcrPull (pull images)
                       │        ──▶ Foundry User + Cognitive Services User (call Foundry)
       ┌───────────────┼───────────────┐
       ▼               ▼               ▼
     webui           agent          mcp-todo
```

Only `agent` (the gateway) calls Foundry, but **all three could**. The worst case: `mcp-todo` faces the internet, so if someone broke into it, they could call your Foundry models and run up your bill.

> **AWS:** like three ECS services sharing one task role with Bedrock access, when only one of them uses Bedrock.

### What we built

```
 id-webui-*     ──▶ AcrPull                                         ──▶ webui
 id-agent-*     ──▶ AcrPull, Azure AI User on Foundry    hi           ──▶ agent
 id-mcp-todo-*  ──▶ AcrPull, read ONE secret (mcp-api-key)          ──▶ mcp-todo
 todo-agent     ──▶ OpenAI User on Foundry, read ONE secret          ──▶ hosted agent
 (agent identity, created by Foundry)
```

| Identity | Used by | Can do | On what | Defined in |
|---|---|---|---|---|
| `id-webui-<token>` | web UI | Pull images (AcrPull) | Container registry | `infra/resources.bicep` |
| `id-agent-<token>` | gateway | Pull images | Container registry | `infra/resources.bicep` |
| | | Call Foundry (Azure AI User) | Foundry account | same |
| `id-mcp-todo-<token>` | MCP server | Pull images | Container registry | `resources-mcp/infra/resources.bicep` |
| | | Read secrets (Key Vault Secrets User) | **Only the `mcp-api-key` secret** | same |
| `todo-agent` agent identity | hosted agent | Call the model (Cognitive Services OpenAI User) | Foundry account | `resources-foundry/scripts/post-deploy.sh` |
| | | Read secrets | **Only the `mcp-api-key` secret** | same |

Now:
- **The web UI can't call Azure at all,** except to pull its own image. It only talks to the gateway over HTTPS.
- **Only the gateway can call Foundry,** with one role instead of two. Azure AI User already covers calling the project's agents, so Cognitive Services User was never needed.
- **The secret readers can see exactly one secret.** If someone adds more secrets to the vault later, these identities still can't read them.

### How it's written in Bicep

Three pieces, in [infra/resources.bicep](../../infra/resources.bicep):

**1. Create the identity:**
```bicep
resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-agent-${resourceToken}'
  location: location
}
```

**2. Give it a role (who + what + where):**
```bicep
resource agentFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundryAccount                                   // where
  name: guid(resourceGroup().id, foundryAccountName, agentIdentity.id, azureAIUserRoleId)
  properties: {
    principalId: agentIdentity.properties.principalId     // who
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)  // what
  }
}
```
The `guid(...)` builds the same name every time, so redeploying doesn't create duplicates. That's what makes it **idempotent**.

**3. Attach it to the container app,** and wait for the image-pull permission first:
```bicep
module agentApp 'br/public:avm/res/app/container-app:0.8.0' = {
  dependsOn: [ agentAcrPull ]                      // permission exists before the app starts
  params: {
    managedIdentities: { userAssignedResourceIds: [ agentIdentity.id ] }
    registries: [ { server: containerRegistry.properties.loginServer, identity: agentIdentity.id } ]
    containers: [ { env: [ { name: 'AZURE_CLIENT_ID', value: agentIdentity.properties.clientId } ] } ]
  }
}
```
`AZURE_CLIENT_ID` tells `DefaultAzureCredential` which identity to sign in as.

> **AWS / Terraform:** this is like `aws_iam_role` + `aws_iam_role_policy_attachment` + `task_role_arn` in the ECS task definition.

### Why the hosted agent's roles are still granted by a script

Bicep runs during **provisioning**. The `todo-agent` identity doesn't exist then: **Foundry creates it the first time the agent is deployed**, later in the process. So its roles can only be granted afterwards, by the Foundry post-deploy hook.

This is worth saying in an interview: *"some platforms create identities at deploy time, and those can't be pre-provisioned in IaC."*

---

## Part 3: Fix 2: put a lock on the MCP server

### What was wrong

`mcp-todo` had a public web address and **no authentication**. Anyone who found `https://mcp-todo.<env>.azurecontainerapps.io/mcp` could read, add and delete todos.

### The options a DevOps engineer would weigh

| Option | How it works | Verdict |
|---|---|---|
| **Make it private** (network) | Only reachable from inside a private network | Strongest, but Foundry calls the server from Microsoft's shared cloud. That needs private networking for Foundry too: expensive and complex. **Later.** |
| **Microsoft Entra ID token** | Caller sends a signed token; server checks it | Strong and identity-based, but Foundry would need extra setup to fetch a token for every call. **A good next step.** |
| **API key** ✅ | Caller sends a shared secret in a header; server compares it | Simple, and Foundry's MCP tool supports custom headers out of the box. **Closes the open door today.** |

We chose the API key. The rest of the design (the key in Key Vault, read by identities) stays the same if we later move to Entra tokens.

### How one request is checked, step by step

```
 Foundry (calling a tool)                          mcp-todo
        │                                             │
        │── POST /mcp                                 │
        │   x-api-key: 3f9a…(64 characters)  ───────▶ │  1. Is a key configured?
        │                                             │       no  → production: 503 (refuse everything)
        │                                             │  2. Does the header match it exactly?
        │                                             │       no  → 401 Unauthorized
        │                                             │       yes → run the tool
        │◀────────────── tool result ─────────────────│
```

The code is a small piece of middleware in [McpTodo/Program.cs](../../src/MafStarterPack.McpTodo/Program.cs) that runs before every `/mcp` request. Two details a reviewer looks for:

- **It fails closed.** If the key is missing because setup went wrong, the server refuses *everything* instead of quietly becoming open again. Only local development allows requests without a key.
- **Constant-time comparison.** It uses `CryptographicOperations.FixedTimeEquals`, which always takes the same time. A normal `==` stops at the first wrong character, and an attacker could measure response times to guess the key one character at a time.

### How the hosted agent sends the key

The agent declares the MCP server as a tool, and that tool can carry HTTP headers. The agent puts the key there, and Foundry sends it with every tool call:

```csharp
serverTool.Headers = new Dictionary<string, string> { ["x-api-key"] = secret.Value.Value };
```

---

## Part 4: Fix 3: store the key in Key Vault

### What Key Vault is

**Key Vault** is Azure's safe for secrets, keys and certificates. Apps read from it at run time **with their identity**, so secrets never sit in code, config files or container images.

> **AWS:** Key Vault secrets ≈ **AWS Secrets Manager**. Key Vault in "RBAC mode" ≈ Secrets Manager controlled by IAM policies.

### How the vault is set up

In [resources-mcp/infra/resources.bicep](../../resources-mcp/infra/resources.bicep):

| Setting | Value | Why |
|---|---|---|
| RBAC mode (`enableRbacAuthorization`) | On | Access uses normal role assignments, the same "who + what + where" as everything else. The old alternative ("access policies") is a separate permission system just for vaults. |
| Soft delete | 7 days | A deleted vault or secret can be recovered for 7 days. |
| Purge protection | **Off** | So the dev environment can be fully deleted each night. **Turn it on in production**, so nobody can permanently destroy secrets early. |

### Where the key comes from

We don't want a person to invent the key, and we don't want a **new** key on every deploy: the MCP server and the agent could briefly disagree. So a hook script, [resources-mcp/scripts/pre-provision.sh](../../resources-mcp/scripts/pre-provision.sh), runs before Bicep:

```
 Is MCP_API_KEY already in the azd environment?
   yes → use it
   no  → does the vault already exist and hold mcp-api-key?
           yes → reuse that value          (e.g. a fresh GitHub runner re-deploying)
           no  → generate one: openssl rand -hex 32     (first-ever deploy)
 → pass it to Bicep as a @secure parameter → Bicep writes it into Key Vault
```

To let the script read the existing key, Bicep gives whoever deploys (you, or the pipeline's identity) **Key Vault Secrets Officer** on the vault.

**Trade-off:** locally, the key also sits in `resources-mcp/.azure/dev/.env`. It's git-ignored and on your own machine, but it's still a copy outside Key Vault. Stricter setups generate the key inside Azure so it never leaves.

### How each app reads the key

There are two different ways, and both are worth knowing:

**1. Let the platform fetch it (the MCP server).** The app's code never talks to Key Vault. In Bicep we tell Container Apps: *"fetch this secret with this identity, and give it to the app as an environment variable."*
```bicep
secrets: { secureList: [ {
  name: 'mcp-api-key'
  keyVaultUrl: '${keyVault.properties.vaultUri}secrets/mcp-api-key'
  identity: mcpTodoIdentity.outputs.resourceId          // fetched as this identity
} ] }
env: [ { name: 'Mcp__ApiKey', secretRef: 'mcp-api-key' } ]
```
> **AWS:** exactly like an **ECS task definition `secrets` entry with `valueFrom` pointing at a Secrets Manager ARN.**

**2. Read it in code with the SDK (the hosted agent).**
```csharp
var secretClient = new SecretClient(new Uri(keyVaultEndpoint), credential);
var secret = await secretClient.GetSecretAsync("mcp-api-key");
```
> **AWS:** `secretsmanager:GetSecretValue` from the AWS SDK.

Option 1 is simpler and keeps Azure code out of the app. Option 2 gives the app control over *when* to read it. The hosted agent needs that, as explained next.

### Why the hosted agent reads the key in the background

On a brand-new environment, things happen in this order:

1. `azd deploy` creates the agent. **Foundry creates its identity now** and starts the container.
2. The deploy waits until the agent reports healthy.
3. Only then does the post-deploy hook grant the identity permission to read the key.

If the agent **required** the key at startup, step 1 would crash (no permission yet), step 2 would wait forever, and step 3 would never run. A deadlock.

So the agent **starts without the key** and keeps trying in the background every 30 seconds. Once the permission lands, it gets the key and from then on refreshes it every 10 minutes. That also means **a rotated key is picked up without a restart**. On a first deploy, tool calls fail with 401 for a minute or two until the permission takes effect.

### The full journey of the key (read this before an interview)

This puts Parts 3 and 4 together: who reads the key, when, and how it reaches the MCP server.

```
 AT STARTUP / IN THE BACKGROUND                         ON EVERY TOOL CALL
 ──────────────────────────────                         ──────────────────

 ① Container Apps platform                              ④ Foundry's Responses runtime
    (as id-mcp-todo-*)                                     decides to call a tool and sends:
    reads mcp-api-key from Key Vault                         POST https://mcp-todo.../mcp
    → puts it in env var Mcp__ApiKey                         x-api-key: <key>
    → mcp-todo keeps it in memory                                   │  (HTTPS: encrypted)
                                                                    ▼
 ② hosted todo-agent container (our code)               ⑤ mcp-todo compares the header with
    (as the todo-agent agent identity)                     the key in memory:
    reads mcp-api-key from Key Vault                         match → run the tool
    every 10 minutes                                         no match / missing → 401

 ③ puts it in the MCP tool's Headers:
    x-api-key: <key>
    → sent to Foundry with each request
```

**The key's path:** Key Vault → our agent container → Foundry's runtime → the MCP server.

### Two easy misunderstandings

**Misunderstanding 1: "The MCP server calls Key Vault to check each request."**
No. The MCP server's code has **no Key Vault code at all**.
- The **Container Apps platform** fetches the secret **once, when it starts the app** (step ①), using the `id-mcp-todo-*` identity.
- It hands the value to the app as the environment variable `Mcp__ApiKey`.
- On each request, the app only compares the `x-api-key` header with that value **in memory** (step ⑤). That's fast, and Key Vault isn't called per request.

> **AWS:** exactly like ECS injecting a Secrets Manager secret into a container through `secrets` / `valueFrom`. The app just reads an environment variable.

A side effect worth knowing: if the key in Key Vault changes, the running MCP app keeps the **old** value until a new revision starts (for example on the next deploy).

**Misunderstanding 2: "Foundry reads the key from Key Vault with its own identity."**
Not quite. Two **different** parts of Foundry are involved:

| Part | What it does with the key |
|---|---|
| **The hosted agent container**, which is *our code* (`HostedAgent/Program.cs`) running on Foundry | **Reads** the key from Key Vault, using **the todo-agent's own agent identity** (step ②), and puts it in the MCP tool's `Headers` (step ③) |
| **Foundry's Responses runtime**, Microsoft's service that runs the model | **Sends** the key: when the model chooses a tool, it calls the MCP server, using the tool definition our agent gave it, including the header (step ④) |

So "Foundry" never reads Key Vault on its own. **Our agent code does, with the agent's identity**, and Foundry's runtime just carries the header to the MCP server.

### HTTP header vs HTTPS

- The key travels in an **HTTP header** (`x-api-key`).
- The connection is **HTTPS**: HTTP inside an encrypted TLS connection. The header is part of the encrypted request, so nobody watching the network can read the key.
- Container Apps ingress accepts HTTPS only by default, and the MCP app also redirects plain HTTP to HTTPS.

### The one place the key could leak

Because the key travels inside the **tool definition** through Foundry's runtime, it **might be recorded in Foundry's traces** in App Insights (the `gen_ai.tool.definitions` attribute). If it is, anyone who can read App Insights could see it. **Always check this after deploying.** If it does leak, pass the key another way, for example with a Foundry project connection or Entra ID tokens.

---

## Part 5: Fix 4: permissions as code, not scripts

### What was wrong

The gateway's Foundry permissions were granted in **two** places: a shell script (`scripts/post-deploy.sh`, run by azd) and a step in the GitHub workflow. From a DevOps point of view that's bad:

- **Hard to review:** permissions hidden in scripts don't show up when you review infrastructure changes.
- **Silent failures:** we saw on Day 1 how scripts skipped grants without any error.
- **Drift:** two places to keep in sync, and nothing stops someone granting extra roles by hand.

### What we built

One declaration in Bicep (shown in Part 2). Bicep creates the identity, then grants its role, then starts the app, always in that order, the same way in every environment.

**It also fixed a real bug.** The workflow step ran in **stage 2**, but the new gateway identity is created in **stage 3**. On the next fresh deploy that step would have failed every time. Both the step and `scripts/post-deploy.*` are now deleted.

---

## Part 6: What each deploy stage does now

| Stage | Creates | Grants |
|---|---|---|
| **1. MCP tier** | Key Vault + `mcp-api-key` secret, `id-mcp-todo-*`, registry, Container Apps environment, `mcp-todo` app | `id-mcp-todo-*`: AcrPull, read the one secret. Deployer: Key Vault Secrets Officer. *(Bicep)* |
| **2. Foundry tier** | Foundry account, project, model; `todo-agent` version (and its identity, the first time) | Agent identity: OpenAI User on Foundry, read the one secret. *(post-deploy script)* |
| **3. App tier** | `id-agent-*`, `id-webui-*`, `agent` and `webui` apps | Both: AcrPull. `id-agent-*`: Azure AI User on Foundry. *(Bicep)* |

New values passed between stages: stage 1 now also hands **the Key Vault name and address** to stage 2, so the hosted agent knows where to read the key.

---

## Part 7: What went wrong while deploying, and what it teaches

Real deploys rarely work first time. These are worth remembering, and make good interview stories.

**1. The new hook never ran.** azd checks that every required Bicep parameter has a value **before** it runs `preprovision` hooks. Our hook *creates* the key, so azd stopped with "missing required input: mcpApiKey".
*Fix:* also run the script as a **`preup`** hook, which runs at the very start of `azd up`. The Foundry tier already did this; now we know why.

**2. "Permission denied" (exit code 126).** The new `.sh` file wasn't marked executable.
*Fix:* `chmod +x`, and record it in git (`git update-index --chmod=+x`) so GitHub's Linux runners get it too.

**3. Foundry kept the old agent after the account was deleted.** We deleted and purged the Foundry account on Oct 23. On redeploy, the agent's old versions (10–14) and its old identity were **still there**. Foundry stores agent definitions by account and project name, not inside the account resource.

**4. The hosted agent won't start.** Versions 15 and 16 are listed as "active", but Foundry reports *"Agent version provisioning failed"*, and every request fails with a generic `internal_error`. The deploy itself also timed out waiting for it. The likely cause is finding 3: new versions built on top of the leftover agent and identity, inside a brand-new account. The usual fix is to **delete the agent completely and deploy it fresh**, so it gets a clean identity.

**Lesson for teardown:** deleting a Foundry account does not delete its agents. A clean teardown should delete agents first (`azd ai agent delete`), then the resources.

---

## Part 8: How to check it, and what we've confirmed

```bash
MCP=https://$(cd resources-mcp && azd env get-value AZURE_RESOURCE_MCP_TODO_FQDN)/mcp
BODY='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}'
H=(-H "Content-Type: application/json" -H "Accept: application/json, text/event-stream")

# The lock: no key, wrong key, right key
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$MCP" "${H[@]}" -d "$BODY"
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$MCP" "${H[@]}" -H "x-api-key: wrong" -d "$BODY"
KEY=$(az keyvault secret show --vault-name "$(cd resources-mcp && azd env get-value AZURE_KEY_VAULT_NAME)" \
      --name mcp-api-key --query value -o tsv)
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$MCP" "${H[@]}" -H "x-api-key: $KEY" -d "$BODY"

# Who can do what
for id in agent webui mcp-todo; do
  P=$(az identity list -g rg-dev --query "[?starts_with(name, 'id-$id-')].principalId | [0]" -o tsv)
  echo "== id-$id"; az role assignment list --all --assignee "$P" --query "[].{role:roleDefinitionName, scope:scope}" -o table
done
```

**Results on Oct 26:**

| Check | Result |
|---|---|
| MCP with no key | 401 ✅ |
| MCP with a wrong key | 401 ✅ |
| MCP with the right key | 200 ✅ |
| `id-agent-*` | AcrPull + Foundry User only ✅ |
| `id-webui-*` | AcrPull only ✅ |
| `id-mcp-todo-*` | AcrPull + read `mcp-api-key` only ✅ |
| Hosted agent identity | OpenAI User + read `mcp-api-key` only ✅ |
| Hosted agent settings | Key Vault address and new MCP address present ✅ |
| **Full chat request** | ❌ Not yet: the hosted agent fails to provision (Part 7, item 4) |

Still to confirm once the agent runs:
- the agent reads the key and its tool calls get through
- the gateway works with only Azure AI User
- the key does **not** appear in Foundry's traces (Foundry records tool definitions on its spans, and headers might be included)

---

## Part 9: Trade-offs and next steps

| Today | Why it's acceptable for now | Production next step |
|---|---|---|
| API key (a shared secret) | Blocks anonymous access | Entra ID tokens: each call tied to an identity, nothing to leak or rotate |
| MCP server still on the public internet | Now refuses anonymous calls | Private networking (VNet integration, private endpoints) |
| Key copy in the local `.env` | Git-ignored, your machine only | Generate the key inside Azure |
| Purge protection off | Daily teardown of dev | On, in every shared environment |
| Hosted agent's roles granted by a script | Foundry creates that identity at deploy time | Watch for Foundry support for pre-created identities |
| App Insights connection string is a plain setting | It only allows *sending* telemetry; Microsoft doesn't treat it as a secret | Fine as is |

---

## Part 10: Interview drill

**"How do you apply least privilege to an agent platform?"**
> "One managed identity per component, each with only data-plane roles, at the smallest scope. In my project the web UI can only pull its image, only the gateway can call Foundry, and the MCP server and the agent can each read exactly one secret, not the whole vault. It's all declared in Bicep, so permission changes go through code review. The one exception is the hosted agent: Foundry creates its identity at deploy time, so a post-deploy step grants its roles."

**"How do you handle secrets?"**
> "In Key Vault, in RBAC mode, never in code or config. Apps read them with their own identity. Either the platform injects them, like a Container Apps Key Vault reference (the same idea as ECS `secrets` from Secrets Manager), or the app reads them with the SDK when it needs to refresh. The key is generated once and reused across deploys, so pipelines don't rotate it by accident."

**"Your MCP server was public with no auth. What did you do?"**
> "Added an API key from Key Vault, compared in constant time, failing closed if no key is configured. Foundry's MCP tool sends it as a header. Next I'd move to Entra ID tokens and a private network."

**"Walk me through how the MCP server checks a call from Foundry."**
> "The key lives in Key Vault. Container Apps fetches it once at startup with the MCP server's own identity and injects it as an environment variable, like ECS `secrets` from Secrets Manager. So the MCP code never calls Key Vault; it just compares the `x-api-key` header in memory, in constant time. On the other side, our hosted-agent code running on Foundry reads the same secret with the agent's own identity and puts it in the MCP tool's headers. Foundry's runtime then sends that header over HTTPS whenever the model calls a tool. Each identity can read only that one secret."

**"Contributor versus a data-plane role?"**
> "Contributor manages resources; it can create a Key Vault but can't read its secrets. Data-plane roles like Key Vault Secrets User let you use what's inside. Apps only get data-plane roles. Like `kms:CreateKey` versus `kms:Decrypt`."

**"Tell me about a deploy that went wrong."**
> "Adding a generated secret broke `azd up`, because azd validates required Bicep inputs before `preprovision` hooks run. I moved the generation into a `preup` hook. Then I found that deleting a Foundry account doesn't delete its hosted agents: the old agent and identity came back and new versions failed to provision. So now teardown deletes agents explicitly first."

---

## Glossary

| Term | Plain meaning | AWS equivalent |
|---|---|---|
| **Managed identity** | An Azure identity for an app, with no password to manage | IAM role for an ECS task / EC2 instance |
| **User-assigned identity** | A managed identity created as its own resource | A standalone IAM role |
| **Role assignment** | Who + what + where | IAM policy attached to a role |
| **Scope** | Where a role applies | The policy's `Resource` |
| **Data plane / control plane** | Using a resource / managing it | e.g. `kms:Decrypt` / `kms:CreateKey` |
| **Key Vault** | Azure's secrets store | Secrets Manager |
| **Key Vault reference** | Container Apps fetches a secret and injects it as an env var | ECS `secrets` with `valueFrom` |
| **Fail closed** | If something's misconfigured, deny everything | — |
| **Idempotent** | Running it again changes nothing that's already right | — |
| **`preup` / `preprovision` hook** | Scripts azd runs at the start of `azd up` / before `azd provision` | — |
