#!/usr/bin/env bash
# Creates (or updates) the Entra app registration that protects disputes-api, with two app roles:
#   Analyst        may approve credits up to the senior threshold
#   SeniorAnalyst  may approve any credit
# and assigns them to the signed-in user (or to the principal given with --assign <object-id>).
#
# What it sets up, and why:
#   - App roles allowed for users AND applications, so the pipeline's end-to-end tests can get the role too.
#   - "Assignment required" on the service principal: only people and apps assigned a role can get a token.
#   - An exposed scope (access_as_user) with the Azure CLI pre-authorized, so a developer can call the API with
#     `az account get-access-token --scope api://<id>/access_as_user` without a consent prompt.
#   - v2 access tokens, so the issuer matches what disputes-api's built-in authentication expects.
# No client secret: the API only validates tokens; it never signs anyone in itself.
#
# Idempotent: re-running finds the existing app and only fills in what's missing. The role and scope IDs are
# fixed, so they never change between runs.
#
# Usage: scripts/entra/create-disputes-app.sh [--assign <object-id> ...]
set -euo pipefail

NAME="${DISPUTES_APP_NAME:-maple-disputes-api-dev}"
ANALYST_ROLE_ID="a9bca4ea-d2d3-47b6-8a2b-f2457638d9d2"
SENIOR_ROLE_ID="9a7b06a3-bcf1-49bb-9022-f96e42e5a100"
SCOPE_ID="1a574474-7b0e-440c-aaa7-15f3ebcc9ee5"
AZURE_CLI_APP_ID="04b07795-8ddb-461a-bbee-02f9e1bf7b46"   # Microsoft's well-known Azure CLI client
GRAPH="https://graph.microsoft.com/v1.0"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

ASSIGNEES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --assign) ASSIGNEES+=("$2"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
  esac
done
[ ${#ASSIGNEES[@]} -gt 0 ] || ASSIGNEES=("$(az ad signed-in-user show --query id -o tsv)")

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

echo "2. App roles, identifier URI, scope, v2 tokens"
az rest --method PATCH --uri "$GRAPH/applications/$OBJECT_ID" --headers "Content-Type=application/json" --body @"$(body <<EOF
{
  "identifierUris": ["api://$APP_ID"],
  "appRoles": [
    { "id": "$ANALYST_ROLE_ID", "value": "Analyst", "displayName": "Analyst", "isEnabled": true,
      "allowedMemberTypes": ["User", "Application"],
      "description": "Investigates disputes and approves credits up to the senior threshold." },
    { "id": "$SENIOR_ROLE_ID", "value": "SeniorAnalyst", "displayName": "Senior analyst", "isEnabled": true,
      "allowedMemberTypes": ["User", "Application"],
      "description": "Approves credits of any amount, including above the senior threshold." }
  ],
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [
      { "id": "$SCOPE_ID", "value": "access_as_user", "type": "User", "isEnabled": true,
        "adminConsentDisplayName": "Use the Maple Bank disputes API",
        "adminConsentDescription": "Lets the app call the disputes API as the signed-in user.",
        "userConsentDisplayName": "Use the Maple Bank disputes API",
        "userConsentDescription": "Lets the app call the disputes API as you." }
    ]
  }
}
EOF
)"
# Pre-authorizing needs the scope to exist already, hence a second call.
az rest --method PATCH --uri "$GRAPH/applications/$OBJECT_ID" --headers "Content-Type=application/json" --body @"$(body <<EOF
{ "api": { "preAuthorizedApplications": [ { "appId": "$AZURE_CLI_APP_ID", "delegatedPermissionIds": ["$SCOPE_ID"] } ] } }
EOF
)"

echo "3. Service principal, with role assignment required"
SP_ID="$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv)"
[ -n "$SP_ID" ] || SP_ID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
az ad sp update --id "$SP_ID" --set appRoleAssignmentRequired=true

echo "4. Role assignments"
for principal in "${ASSIGNEES[@]}"; do
  for role in "$ANALYST_ROLE_ID:Analyst" "$SENIOR_ROLE_ID:SeniorAnalyst"; do
    role_id="${role%%:*}"
    existing="$(az rest --method GET --uri "$GRAPH/servicePrincipals/$SP_ID/appRoleAssignedTo" \
                 --query "value[?principalId=='$principal' && appRoleId=='$role_id'] | length(@)" -o tsv)"
    if [ "$existing" = "0" ]; then
      az rest --method POST --uri "$GRAPH/servicePrincipals/$SP_ID/appRoleAssignedTo" --headers "Content-Type=application/json" \
        --body "{\"principalId\":\"$principal\",\"resourceId\":\"$SP_ID\",\"appRoleId\":\"$role_id\"}" -o none
      echo "   $principal: ${role#*:} assigned"
    else
      echo "   $principal: ${role#*:} already assigned"
    fi
  done
done

echo "5. azd environment"
(cd "$ROOT" && azd env set DISPUTES_API_CLIENT_ID "$APP_ID" >/dev/null)
echo "   DISPUTES_API_CLIENT_ID=$APP_ID"
echo
echo "Get a token for local calls:  az account get-access-token --scope api://$APP_ID/access_as_user --query accessToken -o tsv"
