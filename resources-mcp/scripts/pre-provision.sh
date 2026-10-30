#!/usr/bin/env bash
set -euo pipefail

# Makes sure each MCP server's API key is set before Bicep runs. Reuses a key already stored in Key Vault,
# so re-provisioning (including from a fresh CI runner) doesn't rotate it. Generates one on first deploy.
# Each server has its own key, so a leaked mcp-todo key can't reach mcp-banking.

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

pushd "$REPOSITORY_ROOT/resources-mcp" > /dev/null

ensure_key() {
    local envName=$1 secretName=$2

    # `azd env get-value` prints its error to stdout, so test the exit code rather than the output.
    if azd env get-value "$envName" > /dev/null 2>&1; then
        echo "$envName is already set in the azd environment."
        return
    fi

    local resourceGroup vaultName key=""
    resourceGroup="rg-$(azd env get-value AZURE_ENV_NAME)"
    vaultName=$(az keyvault list --resource-group "$resourceGroup" \
        --query "[?starts_with(name, 'kv-')].name | [0]" -o tsv 2> /dev/null || true)

    if [ -n "$vaultName" ]; then
        key=$(az keyvault secret show --vault-name "$vaultName" --name "$secretName" \
            --query value -o tsv 2> /dev/null || true)
    fi

    if [ -n "$key" ]; then
        echo "Reusing $secretName stored in $vaultName."
    else
        key=$(openssl rand -hex 32)
        echo "Generated a new $secretName."
    fi

    azd env set "$envName" "$key"
}

ensure_key MCP_API_KEY mcp-api-key
ensure_key MCP_BANKING_API_KEY mcp-banking-api-key

popd > /dev/null
