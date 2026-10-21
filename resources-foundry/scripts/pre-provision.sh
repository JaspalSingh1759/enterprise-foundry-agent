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

foundryEndpoint=$(azd env get-value AZURE_AI_PROJECT_ENDPOINT 2>/dev/null || azd env get-value FOUNDRY_PROJECT_ENDPOINT 2>/dev/null || echo "")
if [ -n "$foundryEndpoint" ]; then
    foundryEndpoint=$(echo "$foundryEndpoint" | sed 's/\.services\.ai\.azure\.com/\.cognitiveservices\.azure\.com/g')
    azd env set FOUNDRY_PROJECT_ENDPOINT "$foundryEndpoint"
fi
