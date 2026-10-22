#!/bin/bash

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

secrets="$REPOSITORY_ROOT/src/MafStarterPack.AppHost/MafStarterPack.AppHost.csproj"

foundryName=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_NAME ' | sed 's/^FOUNDRY_NAME = //')
foundryProjectName=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_PROJECT_NAME ' | sed 's/^FOUNDRY_PROJECT_NAME = //')
foundryResourceGroup=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_RESOURCE_GROUP ' | sed 's/^FOUNDRY_RESOURCE_GROUP = //')

resourceGroup="rg-$(azd env get-value AZURE_ENV_NAME)"

# The agent container app runs as the id-mcp-todo-* identity created by resources-mcp.
# Look it up by prefix: `azd env get-value` prints its error to stdout when a key is missing.
principalId=$(az identity list \
    --resource-group "$resourceGroup" \
    --query "[?starts_with(name, 'id-mcp-todo-')].principalId | [0]" -o tsv)

azureAIUserRoleId="53ca6127-db72-4b80-b1b0-d745d6d5456d"
cognitiveServicesUserRoleId="a97b65f3-24c7-4388-baec-2e87135dc908"

if [ -z "$principalId" ] || [ -z "$foundryName" ]; then
    echo "Cannot assign Foundry roles: identity id-mcp-todo-* or FOUNDRY_NAME not found." >&2
    exit 1
fi

foundryResourceId=$(az cognitiveservices account show \
    --name "$foundryName" \
    --resource-group "$foundryResourceGroup" \
    --query id -o tsv)

for roleId in "$azureAIUserRoleId" "$cognitiveServicesUserRoleId"; do
    az role assignment create \
        --assignee-object-id "$principalId" \
        --assignee-principal-type ServicePrincipal \
        --role "$roleId" \
        --scope "$foundryResourceId" \
        --output none || exit 1
done
