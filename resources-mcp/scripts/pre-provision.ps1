# Makes sure each MCP server's API key is set before Bicep runs. Reuses a key already stored in Key Vault,
# so re-provisioning (including from a fresh CI runner) doesn't rotate it. Generates one on first deploy.
# Each server has its own key, so a leaked mcp-todo key can't reach mcp-banking.

$REPOSITORY_ROOT = git rev-parse --show-toplevel

pushd $REPOSITORY_ROOT/resources-mcp

function Set-McpKey([string] $envName, [string] $secretName) {
    # `azd env get-value` prints its error to stdout, so test the exit code rather than the output.
    azd env get-value $envName *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "$envName is already set in the azd environment."
        return
    }

    $resourceGroup = "rg-$(azd env get-value AZURE_ENV_NAME)"
    $vaultName = az keyvault list --resource-group $resourceGroup `
        --query "[?starts_with(name, 'kv-')].name | [0]" -o tsv 2>$null

    $key = ""
    if (![string]::IsNullOrEmpty($vaultName)) {
        $key = az keyvault secret show --vault-name $vaultName --name $secretName `
            --query value -o tsv 2>$null
    }

    if (![string]::IsNullOrEmpty($key)) {
        Write-Host "Reusing $secretName stored in $vaultName."
    } else {
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $key = [System.Convert]::ToHexString($bytes).ToLower()
        Write-Host "Generated a new $secretName."
    }

    azd env set $envName $key
}

Set-McpKey MCP_API_KEY mcp-api-key
Set-McpKey MCP_BANKING_API_KEY mcp-banking-api-key

popd
