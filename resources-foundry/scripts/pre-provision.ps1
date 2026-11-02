$REPOSITORY_ROOT = git rev-parse --show-toplevel

New-Item -ItemType Directory -Path "$REPOSITORY_ROOT/resources-foundry/src/todo-agent" -Force

Copy-Item "$REPOSITORY_ROOT/src/MafStarterPack.HostedAgent/*" -Destination "$REPOSITORY_ROOT/resources-foundry/src/todo-agent" -Recurse -Force

if (Test-Path "$REPOSITORY_ROOT/resources-foundry/src/todo-agent/agent.yaml") {
    Remove-Item "$REPOSITORY_ROOT/resources-foundry/src/todo-agent/agent.yaml" -Force
}

Copy-Item "$REPOSITORY_ROOT/Directory.Build.props" "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"
Copy-Item "$REPOSITORY_ROOT/Directory.Packages.props" "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"

$mcpTodoFqdn = (dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" list | `
    Where-Object { $_ -match '^AZURE_RESOURCE_MCP_TODO_FQDN\s*=' }) -replace '^[^=]+=\s*', ''

azd env set AZURE_RESOURCE_MCP_TODO_FQDN $mcpTodoFqdn

foreach ($key in @("AZURE_KEY_VAULT_NAME", "AZURE_KEY_VAULT_ENDPOINT")) {
    $value = (dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" list | `
        Where-Object { $_ -match "^$key\s*=" }) -replace '^[^=]+=\s*', ''
    if (![string]::IsNullOrEmpty($value)) {
        azd env set $key $value
    }
}

# Role assignments need the service principal's OBJECT id. The pipeline's client id (appId) looks the same
# but matches no identity, so the roles silently do nothing. az ad sp show accepts either and returns the object id.
$pipelinePrincipal = azd env get-value AZURE_PIPELINE_PRINCIPAL_ID 2>$null
if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrEmpty($pipelinePrincipal)) {
    $objectId = az ad sp show --id $pipelinePrincipal --query id -o tsv 2>$null
    if (![string]::IsNullOrEmpty($objectId) -and $objectId -ne $pipelinePrincipal) {
        Write-Host "AZURE_PIPELINE_PRINCIPAL_ID was a client id; using the service principal's object id $objectId"
        azd env set AZURE_PIPELINE_PRINCIPAL_ID $objectId
    }
}
