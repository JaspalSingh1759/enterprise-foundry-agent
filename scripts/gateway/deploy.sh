#!/usr/bin/env bash
# Deploys the Day 15 AI gateway (infra/gateway/gateway.bicep) and, with --switch, points disputes-api at it.
# A one-day experiment: run scripts/gateway/teardown.sh the same day (Basic v2 bills by the hour).
#
# Usage: scripts/gateway/deploy.sh [--switch]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# azd prints "ERROR: key not found" on stdout for a missing key, so use the output only when it succeeds.
env_value() { local out; if out="$(cd "$ROOT" && azd env get-value "$1" 2>/dev/null)"; then echo "$out"; fi; }

RESOURCE_GROUP="$(env_value AZURE_RESOURCE_GROUP)"; RESOURCE_GROUP="${RESOURCE_GROUP:-rg-$(env_value AZURE_ENV_NAME)}"
PROJECT_ENDPOINT="$(env_value FOUNDRY_PROJECT_ENDPOINT)"
[ -n "$PROJECT_ENDPOINT" ] || { echo "ERROR: FOUNDRY_PROJECT_ENDPOINT not found in the azd environment" >&2; exit 1; }
TOKEN="$(az resource list -g "$RESOURCE_GROUP" --resource-type Microsoft.OperationalInsights/workspaces --query "[0].name" -o tsv | sed 's/^log-//')"
CALLER_ID="$(az containerapp show -g "$RESOURCE_GROUP" -n disputes-api --query "properties.template.containers[0].env[?name=='AZURE_CLIENT_ID'].value | [0]" -o tsv)"

echo "1. Deploying the gateway (API Management Basic v2; takes a few minutes)"
GATEWAY_URL="$(az deployment group create -g "$RESOURCE_GROUP" -n ai-gateway -f "$ROOT/infra/gateway/gateway.bicep" \
  -p resourceToken="$TOKEN" disputesCallerId="$CALLER_ID" foundryProjectEndpoint="$PROJECT_ENDPOINT" \
  --query properties.outputs.gatewayUrl.value -o tsv)"
echo "   $GATEWAY_URL"

if [ "${1:-}" = "--switch" ]; then
  echo "2. Pointing disputes-api at the gateway (same token scope; the gateway checks it)"
  az containerapp update -g "$RESOURCE_GROUP" -n disputes-api --set-env-vars "Foundry__ProjectEndpoint=$GATEWAY_URL" -o none
  echo "   done. Note: the next 'azd provision' resets this to the direct endpoint."
fi
