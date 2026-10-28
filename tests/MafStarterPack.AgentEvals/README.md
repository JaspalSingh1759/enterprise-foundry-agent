# Agent state checks

Sends prompts to the deployed `todo-agent`, then reads the to-do list straight from the MCP server to check what the agent actually did. Background: [Day 5 notes](../../docs/notes/day-05-evaluation-and-quality-gate.md).

These tests change data. Run them against **dev only**: they empty the shared to-do list before every case.

## Run

```bash
export AZURE_AI_PROJECT_ENDPOINT=$(cd resources-foundry && azd env get-value AZURE_AI_PROJECT_ENDPOINT)
export AZURE_RESOURCE_MCP_TODO_FQDN=$(cd resources-foundry && azd env get-value AZURE_RESOURCE_MCP_TODO_FQDN)
export AZURE_KEY_VAULT_ENDPOINT=$(cd resources-foundry && azd env get-value AZURE_KEY_VAULT_ENDPOINT)
export AZURE_TENANT_ID=$(cd resources-foundry && azd env get-value AZURE_TENANT_ID)

dotnet test --project tests/MafStarterPack.AgentEvals --output Detailed
```

| Variable | Required | Meaning |
|---|---|---|
| `AZURE_AI_PROJECT_ENDPOINT` | yes | Foundry project endpoint |
| `AZURE_RESOURCE_MCP_TODO_FQDN` | yes | MCP server host name |
| `AZURE_KEY_VAULT_ENDPOINT` | unless `MCP_API_KEY` is set | Vault holding `mcp-api-key` |
| `MCP_API_KEY` | no | Use this key instead of reading Key Vault |
| `AGENT_NAME` | no | Defaults to `todo-agent` |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | no | Defaults to `gpt-5-mini` |
| `AZURE_TENANT_ID` | no | Tenant for `DefaultAzureCredential` |

The signed-in identity needs **Azure AI User** on the Foundry project and **Key Vault Secrets User** (or Officer) on the vault.

Exit code `0` means every case passed; `2` means at least one failed. A failure starting with `INFRASTRUCTURE:` means the agent run kept erroring (for example rate limits), so quality wasn't measured.
