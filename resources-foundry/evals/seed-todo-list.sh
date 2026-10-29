#!/bin/sh
# Resets the dev to-do list to a known, realistic set of items before a judge evaluation run.
# Many test prompts refer to items like "buy milk" or "laundry"; on an empty list the agent could
# only ever answer "not found". DEV ONLY: this deletes every item on the shared list.
#
# Needs: AZURE_RESOURCE_MCP_TODO_FQDN and AZURE_KEY_VAULT_NAME, and an az login that can read the vault.
set -eu

MCP_URL="https://${AZURE_RESOURCE_MCP_TODO_FQDN}/mcp"
API_KEY=$(az keyvault secret show --vault-name "$AZURE_KEY_VAULT_NAME" --name mcp-api-key --query value -o tsv)

rpc() {
  curl -sS "$MCP_URL" \
    -H "x-api-key: $API_KEY" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    ${SESSION_ID:+-H "mcp-session-id: $SESSION_ID"} \
    "$@"
}

# MCP handshake: initialize returns a session ID that every later call must send.
SESSION_ID=$(rpc -D - -o /dev/null -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"seed-todo-list","version":"1"}}}' \
  | grep -i '^mcp-session-id:' | awk '{print $2}' | tr -d '\r')
rpc -o /dev/null -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

call_tool() { # $1 = tool name, $2 = JSON arguments
  rpc -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"$1\",\"arguments\":$2}}"
}

for id in $(call_tool get_todo_items '{}' | grep -o 'ID: [0-9]*' | awk '{print $2}'); do
  call_tool delete_todo_item "{\"id\":$id}" > /dev/null
done

for item in "buy milk" "call mum" "grocery shopping" "do the laundry" "buy bread" "pay rent" "dentist appointment"; do
  call_tool add_todo_item "{\"todoItemText\":\"$item\"}" > /dev/null
done

# One item starts completed, so "show my list" and "what's left" have a mix of states.
DONE_ID=$(call_tool get_todo_items '{}' | grep -o 'ID: [0-9]*, Text: call mum' | awk '{print $2}' | tr -d ',')
call_tool complete_todo_item "{\"id\":$DONE_ID}" > /dev/null

echo "Seeded the to-do list:"
call_tool get_todo_items '{}' | grep -o 'ID: [0-9]*, Text: [^,]*, Completed: [A-Za-z]*'
