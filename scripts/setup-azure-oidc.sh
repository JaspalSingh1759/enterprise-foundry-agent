#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Script: setup-azure-oidc.sh
# Purpose: Configure Azure Entra ID App Registration, OIDC Federated Credentials,
#          RBAC Role Assignments, and automatically save secrets to GitHub Actions.
# Target Repository: JaspalSingh1759/enterprise-foundry-agent
# ==============================================================================

REPO="JaspalSingh1759/enterprise-foundry-agent"
APP_NAME="sp-enterprise-foundry-agent-github"
LOCATION="eastus2"

echo "================================================================="
echo "   Azure OIDC Workload Identity Federation Setup for GitHub     "
echo "================================================================="

# 1. Verify Azure CLI login
echo "[1/6] Verifying Azure CLI authentication..."
if ! az account show > /dev/null 2>&1; then
    echo "ERROR: You are not logged in to Azure."
    echo "Please run 'az login' and rerun this script."
    exit 1
fi

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
SUBSCRIPTION_NAME=$(az account show --query name -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

echo "  ✓ Logged in as: $(az account show --query user.name -o tsv)"
echo "  ✓ Subscription: $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)"
echo "  ✓ Tenant ID:    $TENANT_ID"

# 2. Create or retrieve Entra ID App Registration
echo ""
echo "[2/6] Checking Entra ID App Registration: '$APP_NAME'..."
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv || true)

if [ -z "$APP_ID" ]; then
    echo "  Creating new App Registration..."
    APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
    echo "  ✓ Created App ID: $APP_ID"
else
    echo "  ✓ Found existing App ID: $APP_ID"
fi

# 3. Create or retrieve Service Principal
echo ""
echo "[3/6] Ensuring Service Principal exists for App Registration..."
SP_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv || true)

if [ -z "$SP_ID" ]; then
    echo "  Creating Service Principal..."
    SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
    echo "  ✓ Created Service Principal: $SP_ID"
else
    echo "  ✓ Service Principal exists: $SP_ID"
fi

# 4. Create Federated Identity Credentials (OIDC) for GitHub Actions
echo ""
echo "[4/6] Configuring OIDC Federated Credentials for $REPO..."

create_federated_credential() {
    local cred_name="$1"
    local subject="$2"
    local desc="$3"

    if az ad app federated-credential show --id "$APP_ID" --federated-credential-id "$cred_name" > /dev/null 2>&1; then
        echo "  ✓ Federated credential '$cred_name' already configured."
    else
        echo "  Creating federated credential '$cred_name'..."
        cat <<EOF > /tmp/fed-cred.json
{
  "name": "$cred_name",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "$subject",
  "description": "$desc",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF
        az ad app federated-credential create --id "$APP_ID" --parameters @/tmp/fed-cred.json > /dev/null
        rm -f /tmp/fed-cred.json
        echo "  ✓ Created '$cred_name' (Subject: $subject)"
    fi
}

# Credential for main branch pushes
create_federated_credential "github-main-branch" "repo:${REPO}:ref:refs/heads/main" "GitHub Actions on main branch"

# Credential for PRs
create_federated_credential "github-pull-requests" "repo:${REPO}:pull_request" "GitHub Actions on PRs"

# Credential for 'dev' environment deployments
create_federated_credential "github-env-dev" "repo:${REPO}:environment:dev" "GitHub Actions dev environment"

# 5. Assign Azure RBAC Roles to the Service Principal
echo ""
echo "[5/6] Assigning RBAC permissions on subscription scope..."
SUBSCRIPTION_SCOPE="/subscriptions/$SUBSCRIPTION_ID"

echo "  Assigning 'Contributor' role..."
az role assignment create \
    --role "Contributor" \
    --assignee "$APP_ID" \
    --scope "$SUBSCRIPTION_SCOPE" \
    --only-show-errors > /dev/null 2>&1 || true
echo "  ✓ 'Contributor' role active."

echo "  Assigning 'Role Based Access Control Administrator' role (needed for post-deploy managed identity binding)..."
az role assignment create \
    --role "Role Based Access Control Administrator" \
    --assignee "$APP_ID" \
    --scope "$SUBSCRIPTION_SCOPE" \
    --only-show-errors > /dev/null 2>&1 || true
echo "  ✓ 'Role Based Access Control Administrator' role active."

# 6. Set GitHub Secrets and Variables using gh CLI
echo ""
echo "[6/6] Populating GitHub Secrets in repository: $REPO..."

echo "$APP_ID" | gh secret set AZURE_CLIENT_ID --repo "$REPO"
echo "$TENANT_ID" | gh secret set AZURE_TENANT_ID --repo "$REPO"
echo "$SUBSCRIPTION_ID" | gh secret set AZURE_SUBSCRIPTION_ID --repo "$REPO"
gh variable set AZURE_LOCATION --body "$LOCATION" --repo "$REPO" || true

echo "  ✓ Set secret: AZURE_CLIENT_ID"
echo "  ✓ Set secret: AZURE_TENANT_ID"
echo "  ✓ Set secret: AZURE_SUBSCRIPTION_ID"
echo "  ✓ Set variable: AZURE_LOCATION ($LOCATION)"

echo ""
echo "================================================================="
echo "   🎉 SUCCESS! Azure OIDC is fully configured for GitHub!      "
echo "================================================================="
echo "You can now trigger the CD pipeline from GitHub Actions:"
echo "https://github.com/${REPO}/actions/workflows/deploy.yml"
echo "================================================================="
