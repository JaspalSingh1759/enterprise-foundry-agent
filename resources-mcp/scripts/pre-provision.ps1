# Makes sure MCP_API_KEY is set before Bicep runs. Reuses the key already stored in Key Vault,
# so re-provisioning (including from a fresh CI runner) doesn't rotate it. Generates one on first deploy.

$REPOSITORY_ROOT = git rev-parse --show-toplevel

pushd $REPOSITORY_ROOT/resources-mcp

# `azd env get-value` prints its error to stdout, so test the exit code rather than the output.
azd env get-value MCP_API_KEY *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "MCP_API_KEY is already set in the azd environment."
    popd
    exit 0
}

$resourceGroup = "rg-$(azd env get-value AZURE_ENV_NAME)"
$vaultName = az keyvault list --resource-group $resourceGroup `
    --query "[?starts_with(name, 'kv-')].name | [0]" -o tsv 2>$null

$mcpApiKey = ""
if (![string]::IsNullOrEmpty($vaultName)) {
    $mcpApiKey = az keyvault secret show --vault-name $vaultName --name mcp-api-key `
        --query value -o tsv 2>$null
}

if (![string]::IsNullOrEmpty($mcpApiKey)) {
    Write-Host "Reusing the MCP API key stored in $vaultName."
} else {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $mcpApiKey = [System.Convert]::ToHexString($bytes).ToLower()
    Write-Host "Generated a new MCP API key."
}

azd env set MCP_API_KEY $mcpApiKey

popd
