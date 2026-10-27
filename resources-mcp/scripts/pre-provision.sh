#!/usr/bin/env bash
set -euo pipefail

# Makes sure MCP_API_KEY is set before Bicep runs. Reuses the key already stored in Key Vault,
# so re-provisioning (including from a fresh CI runner) doesn't rotate it. Generates one on first deploy.

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

pushd "$REPOSITORY_ROOT/resources-mcp" > /dev/null

# `azd env get-value` prints its error to stdout, so test the exit code rather than the output.
if azd env get-value MCP_API_KEY > /dev/null 2>&1; then
    echo "MCP_API_KEY is already set in the azd environment."
    popd > /dev/null
    exit 0
fi

resourceGroup="rg-$(azd env get-value AZURE_ENV_NAME)"
vaultName=$(az keyvault list --resource-group "$resourceGroup" \
    --query "[?starts_with(name, 'kv-')].name | [0]" -o tsv 2> /dev/null || true)

mcpApiKey=""
if [ -n "$vaultName" ]; then
    mcpApiKey=$(az keyvault secret show --vault-name "$vaultName" --name mcp-api-key \
        --query value -o tsv 2> /dev/null || true)
fi

if [ -n "$mcpApiKey" ]; then
    echo "Reusing the MCP API key stored in $vaultName."
else
    mcpApiKey=$(openssl rand -hex 32)
    echo "Generated a new MCP API key."
fi

azd env set MCP_API_KEY "$mcpApiKey"

popd > /dev/null
