#!/usr/bin/env bash
# Removes the Day 15 AI gateway: points disputes-api back at Foundry, deletes the gateway's role assignment and
# the API Management instance, and purges it (API Management keeps deleted instances for 48 hours otherwise).
#
# Usage: scripts/gateway/teardown.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
env_value() { local out; if out="$(cd "$ROOT" && azd env get-value "$1" 2>/dev/null)"; then echo "$out"; fi; }

RESOURCE_GROUP="$(env_value AZURE_RESOURCE_GROUP)"; RESOURCE_GROUP="${RESOURCE_GROUP:-rg-$(env_value AZURE_ENV_NAME)}"
PROJECT_ENDPOINT="$(env_value FOUNDRY_PROJECT_ENDPOINT)"
APIM="$(az apim list -g "$RESOURCE_GROUP" --query "[?tags.purpose=='day-15-ai-gateway'].name | [0]" -o tsv)"

echo "1. disputes-api → Foundry directly"
az containerapp update -g "$RESOURCE_GROUP" -n disputes-api --set-env-vars "Foundry__ProjectEndpoint=$PROJECT_ENDPOINT" -o none

if [ -z "$APIM" ]; then
  echo "2. No gateway found; nothing else to remove."
  exit 0
fi

echo "2. Role assignments of $APIM"
PRINCIPAL="$(az apim show -g "$RESOURCE_GROUP" -n "$APIM" --query identity.principalId -o tsv)"
for id in $(az role assignment list --all --assignee "$PRINCIPAL" --query "[].id" -o tsv); do
  az role assignment delete --ids "$id"
done

echo "3. Deleting and purging $APIM"
LOCATION="$(az apim show -g "$RESOURCE_GROUP" -n "$APIM" --query location -o tsv)"
az apim delete -g "$RESOURCE_GROUP" -n "$APIM" --yes
az apim deletedservice purge --service-name "$APIM" --location "$LOCATION"
echo "   done"
