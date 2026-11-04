#!/usr/bin/env bash
# Creates (or updates) the Entra app registration for the dispute console (the web UI's /disputes pages).
# Analysts sign in through it, and the console gets a token for disputes-api AS THE ANALYST.
#
# No client secret: the app trusts the web UI's managed identity through a federated identity credential.
# The web UI proves it is the app by presenting a token for its managed identity (Microsoft.Identity.Web
# "SignedAssertionFromManagedIdentity"). Nothing to store, rotate or leak.
#
# Also pre-authorizes the console on disputes-api's scope, so analysts aren't asked to consent. Run after
# create-disputes-app.sh (it needs DISPUTES_API_CLIENT_ID) and after the web UI exists (it needs its identity
# and address). Idempotent.
#
# Usage: scripts/entra/create-disputes-console-app.sh
set -euo pipefail

NAME="${DISPUTES_CONSOLE_APP_NAME:-maple-disputes-console-dev}"
GRAPH="https://graph.microsoft.com/v1.0"
AZURE_CLI_APP_ID="04b07795-8ddb-461a-bbee-02f9e1bf7b46"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# azd prints "ERROR: key not found" on stdout for a missing key, so use the output only when it succeeds.
env_value() { local out; if out="$(cd "$ROOT" && azd env get-value "$1" 2>/dev/null)"; then echo "$out"; fi; }

API_APP_ID="$(env_value DISPUTES_API_CLIENT_ID)"
WEBUI_FQDN="$(env_value WEBUI_FQDN)"
RESOURCE_GROUP="$(env_value AZURE_RESOURCE_GROUP)"; RESOURCE_GROUP="${RESOURCE_GROUP:-rg-$(env_value AZURE_ENV_NAME)}"
WEBUI_IDENTITY="$(env_value AZURE_WEBUI_IDENTITY_NAME)"
[ -n "$WEBUI_IDENTITY" ] || WEBUI_IDENTITY="$(az identity list -g "$RESOURCE_GROUP" --query "[?starts_with(name,'id-webui-')].name | [0]" -o tsv)"
for v in API_APP_ID WEBUI_FQDN WEBUI_IDENTITY; do
  [ -n "${!v}" ] || { echo "ERROR: $v not found (run create-disputes-app.sh and deploy the apps tier first)" >&2; exit 1; }
done
TENANT_ID="$(az account show --query tenantId -o tsv)"
WEBUI_PRINCIPAL="$(az identity show -g "$RESOURCE_GROUP" -n "$WEBUI_IDENTITY" --query principalId -o tsv)"

body() { local f; f="$(mktemp)"; cat > "$f"; echo "$f"; }

echo "1. App registration $NAME"
APP_ID="$(az ad app list --display-name "$NAME" --query "[0].appId" -o tsv)"
if [ -z "$APP_ID" ]; then
  APP_ID="$(az ad app create --display-name "$NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  echo "   created $APP_ID"
else
  echo "   exists  $APP_ID"
fi
OBJECT_ID="$(az ad app show --id "$APP_ID" --query id -o tsv)"

echo "2. Sign-in redirect, and permission to call disputes-api as the user"
SCOPE_ID="$(az ad app show --id "$API_APP_ID" --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv)"
az rest --method PATCH --uri "$GRAPH/applications/$OBJECT_ID" --headers "Content-Type=application/json" --body @"$(body <<EOF
{
  "web": {
    "redirectUris": ["https://$WEBUI_FQDN/signin-oidc"],
    "logoutUrl": "https://$WEBUI_FQDN/signout-oidc"
  },
  "requiredResourceAccess": [
    { "resourceAppId": "$API_APP_ID", "resourceAccess": [ { "id": "$SCOPE_ID", "type": "Scope" } ] }
  ]
}
EOF
)"
[ -n "$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv)" ] || az ad sp create --id "$APP_ID" -o none

echo "3. Federated credential: trust the web UI's managed identity (no secret)"
existing="$(az rest --method GET --uri "$GRAPH/applications/$OBJECT_ID/federatedIdentityCredentials" \
             --query "value[?subject=='$WEBUI_PRINCIPAL'] | length(@)" -o tsv)"
if [ "$existing" = "0" ]; then
  az rest --method POST --uri "$GRAPH/applications/$OBJECT_ID/federatedIdentityCredentials" --headers "Content-Type=application/json" \
    --body @"$(body <<EOF
{ "name": "webui-managed-identity", "issuer": "https://login.microsoftonline.com/$TENANT_ID/v2.0",
  "subject": "$WEBUI_PRINCIPAL", "audiences": ["api://AzureADTokenExchange"],
  "description": "The web UI's user-assigned managed identity ($WEBUI_IDENTITY) acts as this app's credential" }
EOF
)" -o none
  echo "   added (subject $WEBUI_PRINCIPAL)"
else
  echo "   exists"
fi

echo "4. Pre-authorize the console (and the Azure CLI) on disputes-api's scope"
API_OBJECT_ID="$(az ad app show --id "$API_APP_ID" --query id -o tsv)"
az rest --method PATCH --uri "$GRAPH/applications/$API_OBJECT_ID" --headers "Content-Type=application/json" --body @"$(body <<EOF
{ "api": { "preAuthorizedApplications": [
  { "appId": "$AZURE_CLI_APP_ID", "delegatedPermissionIds": ["$SCOPE_ID"] },
  { "appId": "$APP_ID", "delegatedPermissionIds": ["$SCOPE_ID"] } ] } }
EOF
)"

echo "5. azd environment"
(cd "$ROOT" && azd env set DISPUTES_CONSOLE_CLIENT_ID "$APP_ID" >/dev/null)
echo "   DISPUTES_CONSOLE_CLIENT_ID=$APP_ID"
