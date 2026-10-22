$REPOSITORY_ROOT = git rev-parse --show-toplevel

pushd $REPOSITORY_ROOT/resources-foundry

$tenantId = azd env get-value AZURE_TENANT_ID

$foundryResourceGroup = "rg-$(azd env get-value AZURE_ENV_NAME)"
$foundryName = azd env get-value AZURE_AI_ACCOUNT_NAME
$foundryProjectName = azd env get-value AZURE_AI_PROJECT_NAME
$foundryProjectEndpoint = azd env get-value AZURE_AI_PROJECT_ENDPOINT
$foundryModelDeploymentName = azd env get-value AZURE_AI_MODEL_DEPLOYMENT_NAME

$todoAgentName = azd env get-value AGENT_TODO_AGENT_NAME
$todoAgentVersion = azd env get-value AGENT_TODO_AGENT_VERSION
$todoAgentEndpoint = azd env get-value AGENT_TODO_AGENT_RESPONSES_ENDPOINT

dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "AZURE_TENANT_ID" $tenantId

dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "FOUNDRY_RESOURCE_GROUP" $foundryResourceGroup
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "FOUNDRY_NAME" $foundryName
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "FOUNDRY_PROJECT_NAME" $foundryProjectName
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "FOUNDRY_PROJECT_ENDPOINT" $foundryProjectEndpoint
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "FOUNDRY_MODEL_DEPLOYMENT_NAME" $foundryModelDeploymentName

dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "TODO_AGENT_NAME" $todoAgentName
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "TODO_AGENT_VERSION" $todoAgentVersion
dotnet user-secrets --project "$REPOSITORY_ROOT/src/MafStarterPack.AppHost" set "TODO_AGENT_ENDPOINT" $todoAgentEndpoint

# The hosted agent calls the model as its own agent identity, which needs OpenAI data-plane access.
$openAIUserRoleId = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"
$agentPrincipalId = (azd ai agent show $todoAgentName --output json | Out-String | ConvertFrom-Json).instance_identity.principal_id
$foundryResourceId = az cognitiveservices account show `
    --name $foundryName `
    --resource-group $foundryResourceGroup `
    --query id -o tsv

if ([string]::IsNullOrEmpty($agentPrincipalId) -or [string]::IsNullOrEmpty($foundryResourceId)) {
    Write-Error "Cannot assign role: agent identity for $todoAgentName or Foundry account $foundryName not found."
    exit 1
}

az role assignment create `
    --assignee-object-id $agentPrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role $openAIUserRoleId `
    --scope $foundryResourceId `
    --output none
if ($LASTEXITCODE -ne 0) { exit 1 }

popd
