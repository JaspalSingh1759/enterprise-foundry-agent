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

# Role assignments need the service principal's OBJECT id. The pipeline's client id (appId) looks the same
# but matches no identity, so the roles silently do nothing. az ad sp show accepts either and returns the object id.
# azd prints "ERROR: key not found" on stdout for a missing key, so use the output only when it succeeds.
pipelinePrincipal=""
if value=$(azd env get-value AZURE_PIPELINE_PRINCIPAL_ID 2>/dev/null); then pipelinePrincipal="$value"; fi
if [ -n "$pipelinePrincipal" ]; then
    objectId=$(az ad sp show --id "$pipelinePrincipal" --query id -o tsv 2>/dev/null || echo "")
    if [ -n "$objectId" ] && [ "$objectId" != "$pipelinePrincipal" ]; then
        echo "AZURE_PIPELINE_PRINCIPAL_ID was a client id; using the service principal's object id $objectId"
        azd env set AZURE_PIPELINE_PRINCIPAL_ID "$objectId"
    fi
fi
