"""Publishes Maple Bank's dispute agents to Foundry from Git (agents as code).

Each folder here is one prompt agent: agent.json (name, model, which mcp-banking tools it may call),
instructions.md and schema.json (its structured output). This script:

1. Makes sure the Foundry connection "mcp-banking" holds the server's current API key, read from Key Vault
   with the caller's identity. Agents reach mcp-banking through that connection, so the key is never in an
   agent definition, in Git or in a pipeline variable.
2. Builds each agent's definition and creates a new agent version only if the definition changed (a hash
   of it is stored in the version's metadata). Unchanged agents keep their version, so a version number
   always means a real change.

Prompt agents, not hosted ones: they need no custom code, so there's no container to build, patch or scale.
Foundry runs the tool loop and versions the definition.

Usage:
  python3 resources-foundry/agents/apply.py                         # all agents
  python3 resources-foundry/agents/apply.py --agent intake-agent --model gpt-5-mini --name intake-agent-mini
"""

import argparse
import hashlib
import json
import os
import ssl
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
API = "v1"
CONNECTION = "mcp-banking"

TLS = ssl.create_default_context()
if os.path.exists("/etc/ssl/cert.pem"):  # the python.org build for macOS ships without CA certificates
    TLS.load_verify_locations("/etc/ssl/cert.pem")


def azd_value(name, project="resources-foundry"):
    if value := os.environ.get(name):
        return value
    result = subprocess.run(["azd", "env", "get-value", name], cwd=ROOT / project, capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else ""


def az(*args):
    return subprocess.run(["az", *args], capture_output=True, text=True, check=True).stdout.strip()


def call(method, url, body=None):
    token = az("account", "get-access-token", "--resource", "https://ai.azure.com", "--query", "accessToken", "-o", "tsv")
    request = urllib.request.Request(url, method=method, data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, context=TLS) as response:
            return json.load(response)
    except urllib.error.HTTPError as e:
        if e.code == 404 and method == "GET":
            return None
        sys.exit(f"ERROR: {method} {url} returned HTTP {e.code}: {e.read().decode()}")


def ensure_connection(account_id, server_url, key_vault):
    """Creates or updates the custom-keys connection. The key goes through a private temp file, not argv."""
    key = az("keyvault", "secret", "show", "--vault-name", key_vault, "-n", "mcp-banking-api-key", "--query", "value", "-o", "tsv")
    body = {"properties": {"category": "CustomKeys", "authType": "CustomKeys", "target": server_url, "isSharedToAll": True,
                           "credentials": {"keys": {"x-api-key": key}},
                           "metadata": {"purpose": "mcp-banking API key for the dispute agents"}}}
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
        os.chmod(f.name, 0o600)
        json.dump(body, f)
    try:
        az("rest", "--method", "put", "--body", f"@{f.name}", "-o", "none", "--url",
           f"https://management.azure.com{account_id}/projects/{project_name()}/connections/{CONNECTION}?api-version=2025-06-01")
    finally:
        os.remove(f.name)
    print(f"connection {CONNECTION} -> {server_url}")


def project_name():
    return azd_value("AZURE_AI_PROJECT_NAME") or "ai-project-dev"


def definition_for(folder, server_url, model=None):
    config = json.loads((folder / "agent.json").read_text())
    return config, {
        "kind": "prompt",
        "model": model or config["model"],
        "instructions": (folder / "instructions.md").read_text().strip(),
        "tools": [{
            "type": "mcp",
            "server_label": "mcp_banking",
            "server_url": server_url,
            "project_connection_id": CONNECTION,
            "require_approval": "never",  # every tool it may call is read-only (see allowed_tools)
            "allowed_tools": config["tools"],
        }],
        "text": {"format": {"type": "json_schema", "name": config["name"].replace("-", "_"), "strict": True,
                            "schema": json.loads((folder / "schema.json").read_text())}},
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--agent", help="only this agent folder")
    parser.add_argument("--model", help="override the model (for comparing models)")
    parser.add_argument("--name", help="publish under another name (for comparing models side by side)")
    parser.add_argument("--skip-connection", action="store_true")
    args = parser.parse_args()

    endpoint = azd_value("AZURE_AI_PROJECT_ENDPOINT").rstrip("/")
    account_id = azd_value("AZURE_AI_ACCOUNT_ID")
    fqdn = azd_value("AZURE_RESOURCE_MCP_BANKING_FQDN", project="resources-mcp")
    key_vault = azd_value("AZURE_KEY_VAULT_NAME")
    if not (endpoint and fqdn):
        sys.exit("ERROR: need AZURE_AI_PROJECT_ENDPOINT and AZURE_RESOURCE_MCP_BANKING_FQDN (env or azd)")
    server_url = f"https://{fqdn}/mcp"

    if not args.skip_connection:
        ensure_connection(account_id, server_url, key_vault)

    folders = [HERE / args.agent] if args.agent else sorted(p for p in HERE.iterdir() if (p / "agent.json").exists())
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    for folder in folders:
        config, definition = definition_for(folder, server_url, args.model)
        name = args.name or config["name"]
        sha = hashlib.sha256(json.dumps(definition, sort_keys=True).encode()).hexdigest()[:16]

        current = call("GET", f"{endpoint}/agents/{name}?api-version={API}")
        latest = ((current or {}).get("versions") or {}).get("latest") or {}
        if (latest.get("metadata") or {}).get("definition_sha") == sha:
            print(f"{name}: unchanged (version {latest['version']}, {definition['model']})")
            continue

        created = call("POST", f"{endpoint}/agents/{name}/versions?api-version={API}", {
            "definition": definition,
            "description": config["description"],
            "metadata": {"definition_sha": sha, "git_commit": commit},
        })
        print(f"{name}: created version {created['version']} ({definition['model']}, tools: {', '.join(config['tools'])})")


if __name__ == "__main__":
    main()
