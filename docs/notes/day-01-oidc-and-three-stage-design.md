# Day 1: How the Pipeline Signs In, and Why It Has Three Stages

Notes for Day 1 of the [prep plan](../ai-platform-engineer-prep-plan.md).

**By the end of today you should be able to explain:**
1. What `azd` does and how it's organised.
2. How GitHub Actions signs in to Azure **without a password** (OIDC).
3. Why this project deploys in **three stages**, in that order.

Unfamiliar words are explained in the [glossary](#glossary) at the end.

---

## 1. azd in plain words

`azd` (Azure Developer CLI) is the tool that deploys this project. Think of it as a **recipe runner**:

- **The recipe** is a folder with an `azure.yaml` file. It says what to build and where the infrastructure files (Bicep) are. This repo has **three** recipes: `resources-mcp/`, `resources-foundry/`, and the repo root.
- **The settings** are an *environment*, such as `dev`. It's a small file (`.azure/dev/.env`) holding values like the region (`eastus2`) and addresses created during deploy.
- **The commands:**
  - `azd provision` creates the Azure resources (servers, databases, and so on).
  - `azd deploy` builds your code and puts it on those resources.
  - `azd up` does both.
- **Hooks** are small scripts azd runs at set moments, such as "after deploy". This project uses them to copy files, save settings and grant permissions.

Whenever the infrastructure code creates something useful, like the MCP server's web address, azd **saves it into the environment**. That's how one step finds out what an earlier step created.

---

## 2. How GitHub signs in to Azure without a password (OIDC)

> To set this up yourself, step by step, see [How to configure OIDC sign-in from GitHub Actions to Azure](how-to-configure-github-oidc-for-azure.md).

### The problem

The pipeline needs to do things in Azure: create resources, upload images, grant permissions. It must prove who it is.

The old way: create a password (a "client secret") and store it in GitHub. That's risky:
- It lasts a long time, often years.
- If it leaks, anyone can use it from anywhere.
- Nobody remembers to change it.

### The idea: a fresh visitor pass, not a key

Think of a secure office building:

- **The old way** is giving the contractor a **permanent key**. If they lose it, anyone can get in, forever.
- **The OIDC way:** the contractor's employer (**GitHub**) writes them a **signed visitor pass** each morning saying *"this is our worker, on job X, for project Y"*. Reception (**Azure**) has been told in advance: *"accept passes signed by GitHub, but only for project Y, job X."* The pass expires after a few minutes, so a lost one is useless.

**Nothing secret is stored anywhere.** Each pipeline run gets a new pass, and it expires almost immediately.

### Who's involved

| Who                         | Role in the analogy                        | In this project                                         |
| --------------------------- | ------------------------------------------ | ------------------------------------------------------- |
| **GitHub**                  | The employer that signs passes             | Built into GitHub Actions                               |
| **Microsoft Entra ID**      | Reception that checks passes               | Your Azure tenant's identity service                    |
| **The pipeline's identity** | The worker's badge in the building         | `sp-enterprise-foundry-agent-github`                    |
| **Federated credentials**   | Reception's list of which passes to accept | Created by `scripts/setup-azure-oidc.sh`                |
| **Azure roles**             | Which rooms the badge opens                | Contributor and Role Based Access Control Administrator |

The three GitHub secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) **aren't passwords**. They only say *which* badge, building and floor to use. On their own they can't get anyone in.

### The mental model: three questions

Everything in pipeline sign-in answers one of three questions. Keep them separate and the rest makes sense.

| Question                              | Answered by                                                                                                 | Set up                    | Used when                                          |
| ------------------------------------- | ----------------------------------------------------------------------------------------------------------- | ------------------------- | -------------------------------------------------- |
| **Q1. WHO is the pipeline in Azure?** | The app registration + service principal `sp-enterprise-foundry-agent-github`                               | Once, by the setup script | Always: it's the identity everything else hangs on |
| **Q2. WHO is allowed to act as it?**  | The federated credentials: "accept tokens signed by GitHub, for this repo and environment, meant for Azure" | Once, by the setup script | At **sign-in**                                     |
| **Q3. WHAT may it do, and WHERE?**    | Role assignments: Contributor + RBAC Administrator, on the subscription                                     | Once, by the setup script | At **every action** afterwards                     |

```
 SET UP ONCE                                    ON EVERY PIPELINE RUN
 ───────────                                    ─────────────────────

 Q1  Identity: sp-enterprise-foundry-agent-github ◀──────────┐
                                                             │
 Q2  Federated credential:                ① GitHub gives the job a signed token:
     issuer   = GitHub                       "repo JaspalSingh1759/enterprise-foundry-agent,
     subject  = this repo, env dev            environment dev"
     audience = Azure                  ──▶ ② Azure checks it against Q2.
                                             Match → returns an access token that says
                                             "you are the identity from Q1"
                                                             │
 Q3  Role assignments on the identity:     ③ Pipeline: "create resource group rg-dev"
     Contributor + RBAC Admin,         ──▶ ④ Azure checks: does the identity from Q1 have a
     scope = whole subscription              role (Q3) that allows this, here? Yes → done.
```

**Sign-in (steps ① and ②) uses only Q1 and Q2. Each action (steps ③ and ④) uses Q3.**

In the building analogy:
- **Q1** is the employee's **badge**.
- **Q2** decides **who may carry that badge**: only someone holding a signed letter from GitHub about this repo.
- **Q3** decides **which doors the badge opens**.

### How `scripts/setup-azure-oidc.sh` sets all this up

This script is run **once, by a person, on their own machine** before the pipeline can work. It needs someone who is already signed in to Azure (`az login`) with enough rights to create identities and grant roles, and signed in to GitHub (`gh auth login`).

It does six things, in order.

**Step 1: Check who's signed in.**
```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
```
It reads which Azure subscription and tenant you're signed in to. Everything later is created there.

**Step 2: Create the app registration** (the definition of the pipeline's identity).
```bash
APP_NAME="sp-enterprise-foundry-agent-github"
APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
```
If one with that name already exists, it reuses it. `APP_ID` (the "client ID") is what GitHub will later use to say *which* identity it wants to sign in as.

**Step 3: Create the service principal** (the identity that actually exists in your tenant and can be given roles).
```bash
SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
```
The app registration is the blueprint; the service principal is the real badge. Roles are given to the service principal.

**Step 4: Add the "passes to accept" entries (federated credentials).**
This is where the trust in **one particular repo** is set up. The script builds a small JSON file for each entry and attaches it to the app registration:
```json
{
  "name": "github-env-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev",
  "audience": ["api://AzureADTokenExchange"]
}
```
```bash
az ad app federated-credential create --id "$APP_ID" --parameters @/tmp/fed-cred.json
```
Each field is one of the three things Azure checks on every pass:
- **issuer:** only passes signed by GitHub
- **subject:** only from *this* repo (`JaspalSingh1759/enterprise-foundry-agent`), and only in *this* situation
- **audience:** only passes meant for Azure

The repo name is hard-coded at the top of the script (`REPO="JaspalSingh1759/enterprise-foundry-agent"`). That's what ties the trust to this one repository: a pass from any other repo has a different subject and is rejected.

The script creates **three** entries:

| Entry name             | Subject                      | Lets these runs sign in                                                   |
| ---------------------- | ---------------------------- | ------------------------------------------------------------------------- |
| `github-main-branch`   | `repo:…:ref:refs/heads/main` | Jobs on the `main` branch that don't use a GitHub environment             |
| `github-pull-requests` | `repo:…:pull_request`        | Pull-request runs (see problem 2 below)                                   |
| `github-env-dev`       | `repo:…:environment:dev`     | Jobs that use the `dev` environment: **this is the one your deploys use** |

It skips any entry that already exists, so running it twice is safe.

Azure also has three more entries that the script **didn't** create: `github-immutable-main`, `github-immutable-pr` and `github-immutable-env-dev`. They use GitHub's permanent numeric IDs instead of names, and were added separately. The script would need updating to create them too.

There's no entry for `staging` or `prod`. That's problem 1 below.

**Step 5: Give the identity its roles (what it's allowed to do).**
```bash
SUBSCRIPTION_SCOPE="/subscriptions/$SUBSCRIPTION_ID"

az role assignment create --role "Contributor" \
    --assignee "$APP_ID" --scope "$SUBSCRIPTION_SCOPE" || true

az role assignment create --role "Role Based Access Control Administrator" \
    --assignee "$APP_ID" --scope "$SUBSCRIPTION_SCOPE" || true
```

| Role                                        | What it allows                             | Why the pipeline needs it                                                                 |
| ------------------------------------------- | ------------------------------------------ | ----------------------------------------------------------------------------------------- |
| **Contributor**                             | Create, change and delete **any resource** | Creating the resource group, Container Apps, registry, Foundry account and so on          |
| **Role Based Access Control Administrator** | Grant and remove **roles** for anyone      | The post-deploy steps that give the app identity and the agent identity access to Foundry |

Both are given at **subscription scope** (`/subscriptions/<id>`), so they apply to **everything** in the subscription, not just this project's resource group. That's convenient, because the resource group doesn't exist yet when the pipeline first runs, but it's much more power than the pipeline needs. Together they're close to **Owner**: because the identity can grant roles, it could grant itself any extra permission it lacks.

Two things to notice:
- **`|| true` hides failures.** If granting a role fails (for example, the person running the script isn't allowed to grant roles), the script still prints "✓ role active". That's the same silent-failure pattern found in the post-deploy scripts. Check the roles afterwards with the command in "Check it yourself".
- **RBAC Administrator here has no limits.** It could grant *any* role, including Owner, to *anyone*, itself included. A tighter setup adds a *condition* so it can only grant the few roles this project uses.

**Step 6: Save the IDs into GitHub.**
```bash
echo "$APP_ID"          | gh secret set AZURE_CLIENT_ID       --repo "$REPO"
echo "$TENANT_ID"       | gh secret set AZURE_TENANT_ID       --repo "$REPO"
echo "$SUBSCRIPTION_ID" | gh secret set AZURE_SUBSCRIPTION_ID --repo "$REPO"
gh variable set AZURE_LOCATION --body "$LOCATION" --repo "$REPO"
```
These are what `azure/login@v2` reads in `deploy.yml`. Again, they're identifiers, not passwords.

**The result after the script runs:**

```
GitHub repo JaspalSingh1759/enterprise-foundry-agent
  └─ secrets: which identity, tenant, subscription to use
                         │
                         ▼
Entra ID: app registration sp-enterprise-foundry-agent-github
  ├─ accepts passes from: main branch, pull requests, dev environment
  └─ service principal ──▶ Azure roles on the whole subscription:
                             • Contributor
                             • Role Based Access Control Administrator
```

### What happens on every run

```
 GitHub Actions job            GitHub                    Azure (Entra ID)
        │                         │                            │
   1    │── "I need a pass" ─────▶│                            │
   2    │◀── signed pass ─────────│                            │
        │    (who: this repo, environment dev)                 │
   3    │── "here's my pass, let me in as the pipeline identity" ─▶│
   4    │                         │      checks the signature,  │
        │                         │      and checks the pass     │
        │                         │      against its list        │
   5    │◀── Azure access token (works for about 1 hour) ────────│
   6    │── deploys using that token ──────────────────────────────▶ Azure
```

1. **The job asks GitHub for a pass.** It's only allowed to because `deploy.yml` contains `permissions: id-token: write`.
2. **GitHub gives a signed pass** that says where the run came from: which repo, branch and environment.
3. **The `azure/login` step hands the pass to Azure** and asks to sign in as the pipeline's identity.
4. **Azure checks it.** Is it really signed by GitHub? Does it match an entry on the list?
5. **Azure gives back a normal access token,** valid for about an hour.
6. **Every later command uses that token.** The setting `azd config set auth.useAzCliAuth "true"` tells azd to reuse this sign-in.

When the job finishes, the GitHub machine it ran on is thrown away.

### What exactly Azure checks

Azure compares **three things** on the pass with its list. All three must match exactly:

| Field                | Means                     | Must be                                                                                       |
| -------------------- | ------------------------- | --------------------------------------------------------------------------------------------- |
| **Issuer** (`iss`)   | Who signed the pass       | `https://token.actions.githubusercontent.com` (GitHub)                                        |
| **Audience** (`aud`) | Who the pass is meant for | `api://AzureADTokenExchange` (Azure)                                                          |
| **Subject** (`sub`)  | Which run it is           | An entry on the list, such as `repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev` |

**The subject is the important one.** It decides *which pipeline runs* may sign in. GitHub writes it like this:

| If the job...                             | The subject says          |
| ----------------------------------------- | ------------------------- |
| runs in a GitHub environment (like `dev`) | `...:environment:dev`     |
| runs on the `main` branch, no environment | `...:ref:refs/heads/main` |
| runs for a pull request                   | `...:pull_request`        |

All jobs in `deploy.yml` use an environment, so your deploys present `...:environment:dev`.

### What Azure currently accepts

```
repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev
repo:JaspalSingh1759/enterprise-foundry-agent:ref:refs/heads/main
repo:JaspalSingh1759/enterprise-foundry-agent:pull_request
```

Each entry also exists a second time with numbers in it (`JaspalSingh1759@59330899/enterprise-foundry-agent@1427507801`). Those are GitHub's permanent IDs for the account and repo, which stay the same even if the repo is renamed. Same meaning, safer form. (The setup script creates the first three; the numbered ones were added separately.)

### ⚠️ Two problems found

**Problem 1: staging and prod can't sign in.**
`deploy.yml` lets you pick `dev`, `staging` or `prod`, but the list only has `dev`. A staging run would show `...:environment:staging`, find no match, and stop with:
```
AADSTS700213: No matching federated identity record found ...
```
**Fix:** add entries for `environment:staging` and `environment:prod`. Ideally each uses its own identity, so a staging run can never touch production.

**Problem 2: pull requests can sign in with full power.**
The `pull_request` entry lets any pull-request run sign in as the deploy identity, which can change anything in the subscription. Your CI workflow never needs Azure, so this entry isn't used.
**Fix:** remove it.

### Signing in vs being allowed

These are **two separate checks**:
- **Sign-in (authentication):** "are you really the pipeline?" That's what OIDC solves.
- **Permission (authorization):** "is the pipeline allowed to do this?" That's decided by Azure roles.

If sign-in works but you get `AuthorizationFailed`, OIDC is fine; the identity is missing a role.

### Three easy mistakes

These are the misunderstandings people (including me, while learning this) most often have:

**1. "The roles are given to my account."**
No. Your account (the person running the setup script) only does the setup, like an admin creating a new employee's badge. The roles go to the **pipeline's identity**, the service principal `sp-enterprise-foundry-agent-github`.

**2. "The subscription is what gets the role."**
No. The subscription is **where** the role applies, not **who** gets it. Every role assignment has three parts:
```
who:   sp-enterprise-foundry-agent-github   (the service principal)
what:  Contributor                          (the role)
where: /subscriptions/aaf2b1a9-...          (the scope)
```
The same role could instead be given at a smaller scope, such as one resource group (`/subscriptions/…/resourceGroups/rg-dev`), and would then only apply there.

**3. "Azure gives the pipeline a token based on its roles."**
No. The token only proves **who** the pipeline is. Roles aren't looked at during sign-in at all. They're checked **later, on every single action**. That's why a pipeline with **no roles** can still sign in successfully, and only fails on its first real action with `AuthorizationFailed`.

### If you know AWS: the same thing with AWS names

GitHub-to-Azure sign-in works exactly like GitHub-to-AWS sign-in. Only the names, and the way permissions are attached, differ.

**How it's done in AWS:**
1. Create an **IAM OIDC identity provider** for `token.actions.githubusercontent.com`.
2. Create an **IAM role** (say `github-deploy-role`) with a **trust policy** saying who may assume it:
   ```json
   {
     "Effect": "Allow",
     "Principal": { "Federated": "arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com" },
     "Action": "sts:AssumeRoleWithWebIdentity",
     "Condition": {
       "StringEquals": {
         "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
         "token.actions.githubusercontent.com:sub": "repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev"
       }
     }
   }
   ```
3. Attach **permission policies** to the role, saying what it may do (for example `PowerUserAccess`).
4. In the workflow, `aws-actions/configure-aws-credentials` with `role-to-assume: <role ARN>`.

At run time the action calls **STS `AssumeRoleWithWebIdentity`**. STS checks the GitHub token against the **trust policy**, then returns **temporary credentials**. Every later API call is checked against the role's **permission policies**.

**The same pieces in Azure:**

| AWS                                              | Azure                                                                      | In this project                                           |
| ------------------------------------------------ | -------------------------------------------------------------------------- | --------------------------------------------------------- |
| **IAM role** (the identity the pipeline becomes) | **App registration + service principal**                                   | `sp-enterprise-foundry-agent-github`                      |
| **Trust policy** (who may assume the role)       | **Federated credentials** on the app registration                          | `github-env-dev`, `github-main-branch`, …                 |
| **IAM OIDC identity provider**                   | No separate object: the issuer is written inside each federated credential | `"issuer": "https://token.actions.githubusercontent.com"` |
| **Permission policies** (what the role can do)   | **Role assignments** on the service principal                              | Contributor, RBAC Administrator                           |
| **AWS account**                                  | **Subscription**                                                           | `aaf2b1a9-…`                                              |
| **STS `AssumeRoleWithWebIdentity`**              | **Entra ID token exchange**, done by `azure/login`                         | —                                                         |
| **Temporary credentials** (~1 hour)              | **Access token** (~1 hour)                                                 | —                                                         |
| **`role-to-assume` ARN**                         | `client-id` + `tenant-id` + `subscription-id`                              | The three GitHub secrets                                  |

**The trust policy, field by field:**

| AWS trust-policy part                     | Azure federated-credential field                          |
| ----------------------------------------- | --------------------------------------------------------- |
| `Principal.Federated` (the OIDC provider) | `issuer`                                                  |
| `Condition ...:sub`                       | `subject`: the **same string**, because GitHub creates it |
| `Condition ...:aud` = `sts.amazonaws.com` | `audiences` = `api://AzureADTokenExchange`                |

One difference: AWS allows wildcards (`StringLike` with `repo:org/repo:*`). Azure federated credentials need an **exact** subject, which is why Azure needs one entry per branch or environment.

**Permissions: where Azure really differs.**

In AWS, one policy names both the actions and the resources, and you attach it to the role:
```json
{ "Effect": "Allow", "Action": "s3:*", "Resource": "arn:aws:s3:::my-bucket/*" }
```

In Azure, those are split:
- A **role definition** lists only the actions, like an AWS managed policy **without** the `Resource` line. Example: **Contributor**.
- A **role assignment** links **who** (the service principal) + **which role definition** + **scope** (where). The scope plays the part of AWS's `Resource`.

```
AWS:    IAM role ──attached──▶ policy { Action + Resource }

Azure:  role assignment = { who:   service principal,
                            what:  role definition (actions only),
                            where: scope (subscription / resource group / one resource) }
```

**This project's roles, in AWS terms:**

| Azure role, on the whole subscription       | Closest AWS equivalent                                                                                                                                       |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Contributor**                             | Roughly **`PowerUserAccess`**: manage almost any resource, but **no IAM changes**                                                                            |
| **Role Based Access Control Administrator** | The IAM permissions to **attach policies** (`iam:AttachRolePolicy`, `iam:PutRolePolicy`), needed so the pipeline can give the app identity access to Foundry |
| **Both together**                           | Close to **`AdministratorAccess`**, because the pipeline could grant itself anything it lacks                                                                |

**The whole flow, side by side:**

| Step     | Azure (this project)                                      | AWS                                            |
| -------- | --------------------------------------------------------- | ---------------------------------------------- |
| Set up   | Create app registration + service principal               | Create the IAM role                            |
| Set up   | Add federated credential (issuer, subject, audience)      | Write the role's trust policy                  |
| Set up   | Assign Contributor + RBAC Admin on the subscription       | Attach permission policies to the role         |
| Set up   | Store client, tenant and subscription IDs in GitHub       | Store the role ARN in GitHub                   |
| Each run | GitHub gives the job a signed token                       | Same token, same claims                        |
| Each run | `azure/login` exchanges it with Entra ID                  | `configure-aws-credentials` calls STS          |
| Each run | Entra ID checks the federated credential                  | STS checks the trust policy                    |
| Each run | Returns an access token for the identity                  | Returns temporary credentials for the role     |
| Each run | Every `az`/`azd` call is checked against role assignments | Every API call is checked against the policies |

**One line to remember:** *signing in = assuming the role (checked against the trust policy / federated credential); permissions = the policies attached to it (role assignments).* A role with a correct trust policy but no permissions can be assumed, but every call is denied. In Azure: sign-in works, then `AuthorizationFailed`.

### How a production team would tighten this

| Today                                    | Better                                             |
| ---------------------------------------- | -------------------------------------------------- |
| One identity for all environments        | One identity per environment                       |
| Allowed to change the whole subscription | Only allowed into its own resource group           |
| Can grant any role                       | Can grant only the few roles this project needs    |
| `dev` environment has no rules           | `prod` needs an approval and only runs from `main` |
| Pull requests can sign in                | They can't                                         |

### Check it yourself

```bash
# The list of passes Azure accepts
APP=$(az ad sp show --id 3bfa7b5e-ffc5-48b4-b94a-ac978aa796ac --query appId -o tsv)
az ad app federated-credential list --id $APP --query "[].subject" -o table

# What the pipeline identity is allowed to do
az role assignment list --all --assignee 3bfa7b5e-ffc5-48b4-b94a-ac978aa796ac \
  --query "[].{role:roleDefinitionName, scope:scope}" -o table
```

### When it goes wrong

| You see                                              | It usually means                                                      |
| ---------------------------------------------------- | --------------------------------------------------------------------- |
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL`         | The workflow is missing `permissions: id-token: write`                |
| `AADSTS700213 No matching federated identity record` | The subject doesn't match the list: wrong environment, branch or repo |
| An `AADSTS` error mentioning the audience            | Wrong audience; it must be `api://AzureADTokenExchange`               |
| Sign-in works, then `AuthorizationFailed`            | The identity lacks a role for that action                             |

---

## 3. Why the deploy has three stages

### The short answer

**Each stage needs something the previous stage creates.** Like building a house: foundation first, then walls, then the roof.

```
 Stage 1: MCP tier         Stage 2: Foundry tier          Stage 3: App tier
 ─────────────────         ─────────────────────          ─────────────────
 Creates the MCP server ─▶ Creates the AI agent,     ─▶  Creates the web UI and
 that holds the todo list  which needs the MCP           agent API, which need the
                           server's ADDRESS              Foundry project ENDPOINT
        │                          │                            │
        └── passes on: MCP address └── passes on: endpoint,     │
                                       agent name, version      │
```

- The **agent** can't be set up until it knows *where* the MCP server is.
- The **agent API** can't be configured until it knows *where* the Foundry project is.

### What each stage does

**Stage 1: MCP tier** (`resources-mcp`, runs `azd up`)
- Creates the shared basics: resource group `rg-dev`, container registry, Container Apps environment, logging, and the identity `id-mcp-todo-…`.
- Builds the MCP server's image with Docker and starts the `mcp-todo` app.
- **Passes on:** the MCP server's address.

**Stage 2: Foundry tier** (`resources-foundry`, runs `azd provision` then `azd deploy`)
- Creates the Foundry account, the project and the gpt-5-mini model.
- Builds the `todo-agent` image and registers a new agent version, telling it the MCP address.
- Grants the agent's own identity permission to use the model.
- Grants the app identity permission to call Foundry.
- **Passes on:** the project endpoint, the agent's name and version.

**Stage 3: App tier** (repo root, runs `azd provision` then `azd deploy`)
- Creates the `agent` and `webui` apps, **reusing** stage 1's environment, registry and identity.
- Gives the agent API the Foundry endpoint and agent name.
- Grants the app identity its Foundry permissions (again, to be safe).

### How the stages pass information

Each stage runs on a **brand-new GitHub machine** that knows nothing about the previous one. So values are handed over explicitly, like a **note passed to the next person**:

1. Stage 1 finishes and writes `MCP_FQDN=mcp-todo.xxxx.azurecontainerapps.io` as a **job output**.
2. Stage 2 reads it (`needs.deploy-mcp-server.outputs.mcp_fqdn`) and saves it into its own azd settings.
3. Stage 2 writes the endpoint and agent version as outputs, and stage 3 reads them the same way.

### Why not do it all in one go?

| Reason                    | In simple words                                                                                 |
| ------------------------- | ----------------------------------------------------------------------------------------------- |
| **Order matters**         | Each stage needs the previous stage's result                                                    |
| **Different tools**       | Stage 2 deploys an AI agent with a special azd extension; stages 1 and 3 deploy normal web apps |
| **Change one part alone** | Fix the API without touching the AI agent: `azd deploy` in just that folder                     |
| **Smaller damage**        | If stage 2 breaks, stage 3 never runs against a half-built setup                                |
| **Team boundaries**       | In bigger companies, different teams own these parts                                            |

### The hidden links (where things break)

Some connections between stages **aren't passed as notes**. They only work because every stage follows the same rules:

1. **Same resource group.** All three stages put everything in `rg-<environment>`.
2. **Same name ending.** Each stage calculates the same random-looking suffix (like `6a3q2sputpvay`) from the subscription, resource group and region. Stage 3 finds stage 1's identity *by guessing its name* with that suffix. Change the region in one stage and stage 3 can't find anything.
3. **One shared settings store on your machine.** All .NET projects share one "user secrets" store, so the scripts from every stage read and write the same local settings.
4. **The endpoint format.** The Foundry endpoint must stay `https://<account>.services.ai.azure.com/api/projects/<project>`. Changing it to `cognitiveservices.azure.com` broke sign-in twice.

When a deploy fails in a confusing way, check these four first.

### Good to know

- **Re-running is safe.** Running a stage again doesn't duplicate anything; it just makes sure everything is as described.
- **If a stage fails, later stages don't run.** Fix the problem and re-run the failed jobs.
- **Every run rebuilds all infrastructure,** even if you only changed code. A common improvement is separate pipelines for infrastructure changes and code changes.

---

## 4. Today's hands-on checklist

- [ ] Read `scripts/setup-azure-oidc.sh` and `.github/workflows/deploy.yml`, and match each part to these notes.
- [ ] Run the two "Check it yourself" commands in section 2.
- [ ] **Fix problem 1:** add `environment:staging` and `environment:prod` entries, and create those environments in GitHub (Settings → Environments).
- [ ] **Fix problem 2:** remove the `pull_request` entry.
- [ ] **Run the CD workflow** for `dev` (Actions → "AI Platform CD" → Run workflow). Watch each stage and find where it passes values on.
- [ ] **Check it worked:**
  - `https://agent.<your-env>.eastus2.azurecontainerapps.io/health-agent` returns `"status":"ok"`.
  - A chat request returns an `x-trace-id`, and that trace appears in App Insights.
  - The permissions were granted **without** any manual commands.
- [ ] Add a short **"How to deploy"** section to the README.
- [ ] **Tear down** at the end of the day to stop costs.

---

## 5. Interview drill

### Your 2-minute answer: "Walk me through your project"

> "It's an AI agent platform on Azure in three parts:
> - A web UI and an agent API, running in Container Apps.
> - An AI agent hosted in Azure AI Foundry, using gpt-5-mini.
> - An MCP server holding the todo data. Foundry calls it when the model needs to act.
>
> GitHub Actions deploys it in three stages, MCP first, then Foundry, then the apps, because each stage needs an address the previous one creates.
>
> The pipeline signs in to Azure with OIDC, so no passwords are stored.
>
> Everything sends telemetry to Application Insights, so I can follow one request from the API through Foundry to the model and the tools.
>
> The hardest problems were about identity and permissions, like the AI agent needing its own role to call the model."

### Practice questions

1. **"How does your pipeline sign in to Azure?"**
   GitHub gives each run a short-lived, signed pass saying which repo and environment it's from. Azure accepts it only if it matches a pre-approved entry, then hands back a normal access token. No password is stored anywhere.

2. **"Why is that better than a stored password?"**
   There's nothing long-lived to leak or rotate. Each pass works only for that run, only for that repo, and expires in minutes.

3. **"What stops someone else's repo from deploying to your Azure?"**
   Azure checks the subject, which includes the exact repo and environment. Another repo's pass says something different and gets rejected.

4. **"A staging deploy fails at login. Why?"**
   Azure has no entry for `environment:staging`. Add one, preferably for a separate staging-only identity.

5. **"Sign-in works but the deploy fails with AuthorizationFailed. Why?"**
   Sign-in and permissions are separate checks. OIDC worked; the identity is missing an Azure role.

6. **"Why three stages instead of one?"**
   Each needs the previous one's output: the MCP address for the agent, then the Foundry endpoint for the API. It also lets us deploy parts separately and limits damage when something fails.

7. **"What's weak about the design?"**
   Hidden links: stages find each other's resources by computing the same names. I'd pass resource IDs explicitly instead, and separate infrastructure and code pipelines.

---

## Glossary

| Term                      | Plain meaning                                                                                          |
| ------------------------- | ------------------------------------------------------------------------------------------------------ |
| **OIDC** (OpenID Connect) | A standard way for one system to vouch for who someone is, using signed tokens                         |
| **Token / JWT**           | A small signed piece of text that says who you are; it can't be changed without breaking the signature |
| **Claim**                 | One fact inside a token, like the subject or audience                                                  |
| **Federated credential**  | An entry on Azure's "which outside passes to accept" list                                              |
| **Service principal**     | An identity for a program (not a person) in Azure                                                      |
| **App registration**      | The definition behind a service principal                                                              |
| **Azure role (RBAC)**     | A named set of permissions, given to an identity for a specific area                                   |
| **Scope**                 | The area a role applies to: a subscription, a resource group, or one resource                          |
| **Bicep**                 | Azure's language for describing infrastructure as code                                                 |
| **Job output**            | A value one GitHub Actions job passes to later jobs                                                    |
| **Idempotent**            | Safe to run again: repeating it gives the same result                                                  |
