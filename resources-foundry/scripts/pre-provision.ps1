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
