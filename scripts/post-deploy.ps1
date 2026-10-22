$REPOSITORY_ROOT = git rev-parse --show-toplevel

$secrets = @{}
dotnet user-secrets `
    --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost/MafStarterPack.AppHost.csproj" list | `
    ForEach-Object {
        $parts = $_ -split ' = ', 2
        $secrets[$parts[0]] = $parts[1]
    }

$foundryName = $secrets["FOUNDRY_NAME"]
$foundryProjectName = $secrets["FOUNDRY_PROJECT_NAME"]
$foundryResourceGroup = $secrets["FOUNDRY_RESOURCE_GROUP"]

$resourceGroup = "rg-$(azd env get-value AZURE_ENV_NAME)"

# The agent container app runs as the id-mcp-todo-* identity created by resources-mcp.
# Look it up by prefix: `azd env get-value` prints its error to stdout when a key is missing.
$principalId = az identity list `
    --resource-group $resourceGroup `
    --query "[?starts_with(name, 'id-mcp-todo-')].principalId | [0]" -o tsv

$azureAIUserRoleId = "53ca6127-db72-4b80-b1b0-d745d6d5456d"
$cognitiveServicesUserRoleId = "a97b65f3-24c7-4388-baec-2e87135dc908"

if ([string]::IsNullOrEmpty($principalId) -or [string]::IsNullOrEmpty($foundryName)) {
    Write-Error "Cannot assign Foundry roles: identity id-mcp-todo-* or FOUNDRY_NAME not found."
    exit 1
}

$foundryResourceId = az cognitiveservices account show `
    --name $foundryName `
    --resource-group $foundryResourceGroup `
    --query id -o tsv

foreach ($roleId in @($azureAIUserRoleId, $cognitiveServicesUserRoleId)) {
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $roleId `
        --scope $foundryResourceId `
        --output none
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
