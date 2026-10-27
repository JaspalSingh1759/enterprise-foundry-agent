#!/usr/bin/env bash

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

mkdir -p "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"

cp -R "$REPOSITORY_ROOT/src/MafStarterPack.HostedAgent/." "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"
rm -f "$REPOSITORY_ROOT/resources-foundry/src/todo-agent/agent.yaml"

cp "$REPOSITORY_ROOT/Directory.Build.props" "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"
cp "$REPOSITORY_ROOT/Directory.Packages.props" "$REPOSITORY_ROOT/resources-foundry/src/todo-agent"

mcpTodoFqdn=$(dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" list 2>/dev/null | \
    sed -n 's/^AZURE_RESOURCE_MCP_TODO_FQDN[[:space:]]*=[[:space:]]*//p')

if [ -n "$mcpTodoFqdn" ]; then
    azd env set AZURE_RESOURCE_MCP_TODO_FQDN "$mcpTodoFqdn"
fi

for key in AZURE_KEY_VAULT_NAME AZURE_KEY_VAULT_ENDPOINT; do
    value=$(dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" list 2>/dev/null | \
        sed -n "s/^${key}[[:space:]]*=[[:space:]]*//p")
    if [ -n "$value" ]; then
        azd env set "$key" "$value"
    fi
done

foundryEndpoint=$(azd env get-value AZURE_AI_PROJECT_ENDPOINT 2>/dev/null || azd env get-value FOUNDRY_PROJECT_ENDPOINT 2>/dev/null || echo "")
if [ -n "$foundryEndpoint" ]; then
    azd env set FOUNDRY_PROJECT_ENDPOINT "$foundryEndpoint"
fi
