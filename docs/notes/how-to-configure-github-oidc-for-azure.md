# How to Configure OIDC Sign-in from GitHub Actions to Azure

A step-by-step guide to letting a GitHub Actions pipeline sign in to Azure **without storing any password**. It uses this repository as the example, and works the same for any repo.

For *why* this works and what happens behind the scenes, see the [Day 1 notes](day-01-oidc-and-three-stage-design.md#2-how-github-signs-in-to-azure-without-a-password-oidc).

---

## What you'll end up with

```
GitHub repo: JaspalSingh1759/enterprise-foundry-agent
 ├─ GitHub environment "dev"  (optional protection rules)
 ├─ secrets: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID
 └─ workflow: permissions id-token: write  →  azure/login@v2
                              │
                              │  "here's my signed GitHub token"
                              ▼
Microsoft Entra ID
 └─ App registration "sp-enterprise-foundry-agent-github"
     ├─ Federated credential: trust GitHub tokens for this repo + environment dev
     └─ Service principal ──▶ Azure role(s) at a chosen scope
                              (e.g. Contributor on resource group rg-dev)
```

Setting this up has **two halves**:
- **Azure side (steps 1–4):** create the identity, tell Azure which GitHub runs to trust, and decide what the identity may do.
- **GitHub side (steps 5–7):** store the identity's IDs, and let the workflow ask GitHub for a token.

---

## Before you start

You need:

| What | Why | Check with |
|---|---|---|
| **Azure CLI**, signed in | To create the identity and roles | `az account show` |
| Permission to **create app registrations** in Entra ID | Step 2. Allowed for normal users by default, unless your organisation turned it off | Try step 2; an error means ask an admin |
| **Owner** or **User Access Administrator** (or RBAC Administrator) on the scope | Step 4: granting roles | `az role assignment list --assignee <your-email> --all -o table` |
| **GitHub CLI**, signed in, admin on the repo | Steps 5–6: secrets and environments | `gh auth status` |

Collect three values first:

```bash
az account show --query "{subscription:id, tenant:tenantId, user:user.name}" -o table
```

Write down the **subscription ID** and **tenant ID**. You'll need them in steps 4 and 6.

---

## Step 1: Decide three things before creating anything

Deciding these first saves redoing the setup later.

**1. Which GitHub runs may sign in?** Each becomes one federated credential (step 3):

| Runs you want to allow | Subject you'll register |
|---|---|
| Jobs that use the GitHub environment `dev` | `repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev` |
| Jobs on the `main` branch, no environment | `repo:JaspalSingh1759/enterprise-foundry-agent:ref:refs/heads/main` |
| Jobs triggered by a tag like `v1.2.0` | `repo:JaspalSingh1759/enterprise-foundry-agent:ref:refs/tags/v1.2.0` |
| Pull-request runs | `repo:JaspalSingh1759/enterprise-foundry-agent:pull_request` (usually **don't**, see Step 3) |

**Rule:** if a job declares `environment:`, GitHub uses the environment form, even on `main`. All jobs in this repo's `deploy.yml` declare one, so the **environment** form is the one that matters here.

**2. One identity or one per environment?**
- **One per environment** (recommended): `dev`, `staging` and `prod` each get their own identity, allowed only into their own resources. A mistake in a dev run can't touch production.
- **One shared identity:** simpler, which is what this repo does today, but every environment has the same power.

**3. Which roles, on what scope?** Give the **smallest** that works:

| Pipeline needs to... | Role | Scope |
|---|---|---|
| Create and change resources | **Contributor** | The environment's resource group if it already exists; the subscription if the pipeline must create the resource group itself |
| Grant roles to the app's own identities (this repo's post-deploy steps do) | **Role Based Access Control Administrator**, ideally limited to specific roles | Same scope |
| Only read things | **Reader** | Whatever it reads |

---

## Step 2: Create the identity (app registration + service principal)

The **app registration** is the definition; the **service principal** is the actual identity in your tenant that roles are given to. You need both.

**Using the CLI:**

```bash
APP_NAME="sp-enterprise-foundry-agent-github"

# 1. App registration
APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)

# 2. Service principal for it
SP_OBJECT_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)

echo "Client ID (AZURE_CLIENT_ID): $APP_ID"
echo "Service principal object ID: $SP_OBJECT_ID"
```

**Using the portal:**
1. **Microsoft Entra ID → App registrations → New registration.**
2. Name: `sp-enterprise-foundry-agent-github`. Supported account types: *this organizational directory only*. No redirect URI.
3. **Register.** Copy the **Application (client) ID**: that's your `AZURE_CLIENT_ID`.
4. The portal creates the service principal for you (it appears under **Enterprise applications**).

**Note: you don't create a client secret or certificate.** That's the whole point of OIDC.

---

## Step 3: Tell Azure which GitHub runs to trust (federated credentials)

This is the core of OIDC. Each federated credential says: *"accept a token that GitHub signed, for exactly this repo and situation, meant for Azure."*

**Using the CLI.** Write one small JSON file per credential:

```bash
cat > fed-dev.json <<'EOF'
{
  "name": "github-env-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:JaspalSingh1759/enterprise-foundry-agent:environment:dev",
  "description": "GitHub Actions jobs in the dev environment",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF

az ad app federated-credential create --id "$APP_ID" --parameters @fed-dev.json
rm fed-dev.json
```

What each field means:

| Field | Value | Meaning |
|---|---|---|
| `name` | `github-env-dev` | Just a label for you |
| `issuer` | `https://token.actions.githubusercontent.com` | Only tokens **signed by GitHub** |
| `subject` | `repo:<owner>/<repo>:environment:dev` | Only from **this repo, in this environment**. Must match **exactly**: no wildcards, and capitals matter |
| `audiences` | `["api://AzureADTokenExchange"]` | Only tokens **meant for Azure**. Always this value |

**Using the portal:**
1. **App registrations → your app → Certificates & secrets → Federated credentials → Add credential.**
2. Scenario: **GitHub Actions deploying Azure resources.**
3. Organization: `JaspalSingh1759`. Repository: `enterprise-foundry-agent`.
4. Entity type: **Environment**. GitHub environment name: `dev`.
5. Name: `github-env-dev`. **Add.**

The portal builds the issuer, subject and audience for you, which avoids typos.

**Repeat** for each kind of run you decided to allow in step 1. If you use one identity per environment, create staging's credential on the staging identity, and so on.

**About pull requests:** a `pull_request` credential lets any PR run sign in. If the identity can change resources, a malicious PR could do real damage. Only add one for a separate, **read-only** identity, and only if PR workflows actually need Azure. This repo's CI doesn't.

---

## Step 4: Decide what the identity may do (role assignments)

Signing in only proves *who* the pipeline is. Without roles, it can sign in but every action fails with `AuthorizationFailed`.

**Option A: scope to a resource group** (best, if the group already exists):

```bash
SUB_ID="<subscription id>"
RG_SCOPE="/subscriptions/$SUB_ID/resourceGroups/rg-dev"

az group create -n rg-dev -l eastus2          # create it once, if needed

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Contributor" --scope "$RG_SCOPE"
```

**Option B: scope to the subscription** (what this repo does, because its Bicep creates the resource group):

```bash
SUB_SCOPE="/subscriptions/$SUB_ID"

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Contributor" --scope "$SUB_SCOPE"

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "$SUB_SCOPE"
```

**Make RBAC Administrator safer.** Unrestricted, it can grant *any* role to *anyone*, including itself. In the portal, when you add this role assignment, choose **"Allow user to only assign selected roles to selected principals (fewer privileges)"**. Then pick only the roles your pipeline grants. In this repo those are:
- Azure AI User (Foundry User)
- Cognitive Services User
- Cognitive Services OpenAI User
- AcrPull

**Don't add `|| true`** to these commands. If a grant fails, you want to know now, not on the first deploy.

**Check:**
```bash
az role assignment list --all --assignee "$SP_OBJECT_ID" \
  --query "[].{role:roleDefinitionName, scope:scope}" -o table
```

---

## Step 5: Create the GitHub environment (and protect it)

If your federated credential uses `environment:dev`, the environment must exist in GitHub, and the job must use it.

**Portal:** repo → **Settings → Environments → New environment** → `dev`.

**CLI:**
```bash
gh api -X PUT repos/JaspalSingh1759/enterprise-foundry-agent/environments/dev
```

**Protection rules** decide *who* may run jobs in an environment. The subject only proves the run *entered* the environment, so the rules are part of your security:

| Environment | Suggested rules |
|---|---|
| `dev` | None, or only from `main` |
| `staging` | Only from `main` |
| `prod` | Only from `main`, **plus required reviewers** (someone must approve each run) |

Set them under **Settings → Environments → prod → Deployment protection rules** and **Deployment branches and tags**.

---

## Step 6: Store the identity's IDs in GitHub

These three values **aren't passwords**: on their own they can't sign anyone in. They're stored as secrets mainly to keep them out of logs.

```bash
REPO="JaspalSingh1759/enterprise-foundry-agent"
TENANT=$(az account show --query tenantId -o tsv)
SUB_ID=$(az account show --query id -o tsv)

echo "$APP_ID" | gh secret set AZURE_CLIENT_ID       --repo "$REPO"
echo "$TENANT" | gh secret set AZURE_TENANT_ID       --repo "$REPO"
echo "$SUB_ID" | gh secret set AZURE_SUBSCRIPTION_ID --repo "$REPO"
```

**With one identity per environment,** store them as **environment secrets** instead, so each environment automatically gets its own identity:

```bash
echo "$APP_ID_STAGING" | gh secret set AZURE_CLIENT_ID --repo "$REPO" --env staging
```

A job running in `staging` then reads staging's `AZURE_CLIENT_ID` without any change to the workflow.

---

## Step 7: Use it in the workflow

Three things in the YAML make it work:

```yaml
permissions:
  id-token: write     # ① lets the job ask GitHub for a signed token
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: dev   # ② makes the token's subject "...:environment:dev"
    steps:
      - uses: actions/checkout@v4

      - name: Sign in to Azure with OIDC
        uses: azure/login@v2   # ③ swaps the GitHub token for an Azure token
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Prove it worked
        run: az account show --query "{subscription:name, signedInAs:user.name}" -o table
```

1. **`permissions: id-token: write`.** Without it, the job can't get a token, and `azure/login` fails immediately.
2. **`environment: dev`.** This decides the token's subject. It must match the federated credential from step 3 exactly.
3. **`azure/login@v2` with only the three IDs,** and **no** `client-secret` or `creds`. Seeing the three IDs and no secret is how you know it's using OIDC.

**For azd,** reuse the Azure CLI's sign-in, as this repo's `deploy.yml` does:
```yaml
      - name: Use the Azure CLI sign-in for azd
        run: azd config set auth.useAzCliAuth "true"
```

---

## Step 8: Test it before relying on it

Make a tiny workflow, for example `.github/workflows/oidc-test.yml`, with the YAML from step 7 and `on: workflow_dispatch`. Run it from the **Actions** tab.

| What you see | Meaning |
|---|---|
| `az account show` prints your subscription | ✅ Sign-in works |
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL` | Missing `permissions: id-token: write` |
| `AADSTS700213: No matching federated identity record found for presented assertion subject '...'` | The subject doesn't match. The error **prints the subject GitHub sent**: copy it and compare it character by character with your credential |
| An `AADSTS` error about the audience | The credential's audience isn't `api://AzureADTokenExchange` |
| `AADSTS700016: Application ... was not found` | Wrong `AZURE_CLIENT_ID` or `AZURE_TENANT_ID` |
| Sign-in works, a later step says `AuthorizationFailed` | OIDC is fine. The identity lacks a role at that scope (step 4) |

Delete the test workflow afterwards, or keep it as a quick health check.

---

## Adding a new environment (for example staging)

This repo needs this today: `deploy.yml` offers `staging` and `prod`, but only `dev` can sign in.

1. **Identity:** create `sp-enterprise-foundry-agent-github-staging` (step 2), or reuse the existing one if you're keeping one shared identity.
2. **Federated credential:** subject `repo:JaspalSingh1759/enterprise-foundry-agent:environment:staging` (step 3).
3. **Roles:** on staging's own scope, such as `rg-staging` or a separate subscription (step 4).
4. **GitHub environment** `staging`, with "only from `main`" (step 5).
5. **Environment secrets** for staging's client ID, if it has its own identity (step 6).
6. **Test** by running the workflow with `staging` selected (step 8).

---

## Removing access

When a repo or environment shouldn't deploy any more:

```bash
# Stop trusting one kind of run
az ad app federated-credential delete --id "$APP_ID" --federated-credential-id github-pull-requests

# Remove what the identity can do
az role assignment delete --assignee "$SP_OBJECT_ID" --role "Contributor" --scope "$SUB_SCOPE"

# Remove the identity completely
az ad app delete --id "$APP_ID"
```

Because there's no secret, there's nothing to rotate or revoke anywhere else: deleting the credential or the app stops access immediately.

---

## Checklist

- [ ] Decided which runs may sign in, one identity or several, and roles and scope
- [ ] App registration + service principal created (no client secret)
- [ ] Federated credential for each allowed run, with an exact subject
- [ ] No `pull_request` credential on an identity that can change resources
- [ ] Roles assigned at the smallest scope that works, without `|| true`
- [ ] RBAC Administrator limited to the specific roles the pipeline grants
- [ ] GitHub environments created, with `prod` requiring approval
- [ ] `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` stored (per environment if separate identities)
- [ ] Workflow has `permissions: id-token: write`, `environment:`, and `azure/login@v2` with no secret
- [ ] Test workflow ran `az account show` successfully

## How this repo does it today

`scripts/setup-azure-oidc.sh` automates steps 2, 3, 4 and 6 in one go: one identity, three credentials (main, pull requests, dev), and Contributor + RBAC Administrator on the whole subscription. The [Day 1 notes](day-01-oidc-and-three-stage-design.md#how-scriptssetup-azure-oidcsh-sets-all-this-up) walk through it line by line.

Compared with this guide, it lacks:
- staging and prod credentials
- protection rules on the GitHub environments
- a limit on what RBAC Administrator can grant

It also includes a pull-request credential, and hides failed role grants with `|| true`.
