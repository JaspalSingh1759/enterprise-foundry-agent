#!/bin/bash

REPOSITORY_ROOT=$(git rev-parse --show-toplevel)

secrets="$REPOSITORY_ROOT/src/MafStarterPack.AppHost/MafStarterPack.AppHost.csproj"

foundryName=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_NAME ' | sed 's/^FOUNDRY_NAME = //')
foundryProjectName=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_PROJECT_NAME ' | sed 's/^FOUNDRY_PROJECT_NAME = //')
foundryResourceGroup=$(dotnet user-secrets --project "$secrets" list \
    | grep '^FOUNDRY_RESOURCE_GROUP ' | sed 's/^FOUNDRY_RESOURCE_GROUP = //')

userAssignedIdentityName=$(azd env get-value MANAGED_IDENTITY_NAME || echo "")
resourceGroup="rg-$(azd env get-value AZURE_ENV_NAME)"

if [ -z "$userAssignedIdentityName" ]; then
    userAssignedIdentityName=$(az identity list --resource-group "$resourceGroup" --query "[0].name" -o tsv 2>/dev/null || echo "")
fi

azureAIUserRoleId="53ca6127-db72-4b80-b1b0-d745d6d5456d"
cognitiveServicesUserRoleId="a97b65f3-24c7-4388-baec-2e87135dc908"

if [ -n "$userAssignedIdentityName" ] && [ -n "$foundryName" ]; then
    principalId=$(az identity show \
        --name "$userAssignedIdentityName" \
        --resource-group "$resourceGroup" \
        --query principalId -o tsv 2>/dev/null || echo "")

    foundryResourceId=$(az cognitiveservices account show \
        --name "$foundryName" \
        --resource-group "$foundryResourceGroup" \
        --query id -o tsv 2>/dev/null || echo "")

    if [ -n "$principalId" ] && [ -n "$foundryResourceId" ]; then
        az role assignment create \
            --assignee "$principalId" \
            --role "$azureAIUserRoleId" \
            --scope "$foundryResourceId" || true

        az role assignment create \
            --assignee "$principalId" \
            --role "$cognitiveServicesUserRoleId" \
            --scope "$foundryResourceId" || true
    fi
fi
