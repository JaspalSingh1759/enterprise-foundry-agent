#!/usr/bin/env bash
# Builds Maple Bank's knowledge index from Git, end to end:
#   chunk the documents -> create/update the search definitions -> upload the chunks -> run the indexer -> check it.
#
# Blue/green: each index version has its own name (INDEX_NAME, e.g. maple-knowledge-v1) with its own skillset and
# indexer. Agents query the alias "maple-knowledge". A breaking change (new embedding model, new fields, new
# chunking) gets a new INDEX_NAME; once its retrieval tests pass, run with SWITCH_ALIAS=true to move the alias.
# Rollback = point the alias back at the old index (INDEX_NAME=<old> ALIAS_ONLY=true SWITCH_ALIAS=true).
#
# Signs in with Entra ID (keys are turned off on the search service). Needs Search Service Contributor and
# Storage Blob Data Contributor on the "knowledge" container. Settings come from the environment, else from azd.
#
# Usage: resources-foundry/knowledge/apply.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
API="2024-07-01"
ALIAS_API="2025-08-01-preview"   # index aliases are still preview

azd_value() { (cd "$HERE/.." && azd env get-value "$1" 2>/dev/null) || true; }

export SEARCH_SERVICE="${SEARCH_SERVICE:-$(azd_value AZURE_AI_SEARCH_SERVICE_NAME)}"
export STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-$(azd_value AZURE_STORAGE_ACCOUNT_NAME)}"
export OPENAI_ENDPOINT="${OPENAI_ENDPOINT:-$(azd_value AZURE_OPENAI_ENDPOINT)}"
export OPENAI_ENDPOINT="${OPENAI_ENDPOINT%/}"
export EMBEDDING_DEPLOYMENT="${EMBEDDING_DEPLOYMENT:-text-embedding-3-small}"
export CONTAINER="${CONTAINER:-knowledge}"
export INDEX_NAME="${INDEX_NAME:-maple-knowledge-v1}"
ALIAS="${ALIAS:-maple-knowledge}"
SWITCH_ALIAS="${SWITCH_ALIAS:-false}"
ALIAS_ONLY="${ALIAS_ONLY:-false}"   # skip the build; only (re)point the alias

for v in SEARCH_SERVICE STORAGE_ACCOUNT OPENAI_ENDPOINT; do
  [ -n "${!v}" ] || { echo "ERROR: $v is not set and not found in the azd environment" >&2; exit 1; }
done
export STORAGE_RESOURCE_ID="${STORAGE_RESOURCE_ID:-$(az storage account show -n "$STORAGE_ACCOUNT" --query id -o tsv)}"

ENDPOINT="https://$SEARCH_SERVICE.search.windows.net"
TOKEN="$(az account get-access-token --resource https://search.azure.com --query accessToken -o tsv)"
CHUNKS="$HERE/build/chunks"

# Calls the search REST API. Fails with the response body on any non-2xx status.
search() {
  local method="$1" path="$2" body="${3:-}" api="${4:-$API}" out code data=()
  out="$(mktemp)"
  # A POST with no body still needs Content-Length: 0, or the service answers 411.
  if [ -n "$body" ]; then data=(--data-binary "$body"); elif [ "$method" = POST ]; then data=(-H "Content-Length: 0"); fi
  code=$(curl -sS -o "$out" -w '%{http_code}' -X "$method" "$ENDPOINT/$path?api-version=$api" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" ${data[@]+"${data[@]}"})
  if [[ "$code" != 2* ]]; then
    echo "ERROR: $method $path returned HTTP $code" >&2; cat "$out" >&2; echo >&2; rm -f "$out"; return 1
  fi
  cat "$out"; rm -f "$out"
}

# Fills ${VAR} placeholders in a definition file from the environment.
render() { perl -pe 's/\$\{(\w+)\}/exists $ENV{$1} ? $ENV{$1} : die "unset: $1\n"/ge' "$HERE/$1"; }

update_alias() {
  local current
  current="$(search GET "aliases/$ALIAS" "" "$ALIAS_API" 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["indexes"][0])' 2>/dev/null || true)"
  if [ -z "$current" ] || [ "$SWITCH_ALIAS" = true ]; then
    search PUT "aliases/$ALIAS" "{\"name\":\"$ALIAS\",\"indexes\":[\"$INDEX_NAME\"]}" "$ALIAS_API" >/dev/null
    echo "  $ALIAS -> $INDEX_NAME${current:+ (was $current)}"
  else
    echo "  $ALIAS -> $current (unchanged; set SWITCH_ALIAS=true to point it at $INDEX_NAME)"
  fi
}

if [ "$ALIAS_ONLY" = true ]; then
  echo "Alias $ALIAS"
  update_alias
  exit 0
fi

put() {
  local file="$1" collection="$2" name
  name="$(render "$file" | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')"
  echo "  $collection/$name"
  search PUT "$collection/$name" "$(render "$file")" >/dev/null
}

echo "1. Chunking documents"
python3 "$ROOT/tools/knowledge/chunk_docs.py" "$CHUNKS"
EXPECTED=$(find "$CHUNKS" -name '*.json' | wc -l | tr -d ' ')

echo "2. Applying search definitions"
put synonyms.json synonymmaps
put index.json indexes
put datasource.json datasources
put skillset.json skillsets

echo "3. Uploading chunks to $STORAGE_ACCOUNT/$CONTAINER"
az storage blob upload-batch --account-name "$STORAGE_ACCOUNT" --auth-mode login -d "$CONTAINER" -s "$CHUNKS" \
  --overwrite --content-type application/json --only-show-errors --no-progress -o none
# Delete blobs whose chunk no longer exists. Soft delete keeps them 7 days, and the indexer's deletion
# detection removes their chunks from the index.
comm -13 <(cd "$CHUNKS" && ls | sort) \
         <(az storage blob list --account-name "$STORAGE_ACCOUNT" --auth-mode login -c "$CONTAINER" \
             --query "[].name" -o tsv --only-show-errors | sort) |
while read -r stale; do
  echo "  deleting $stale"
  az storage blob delete --account-name "$STORAGE_ACCOUNT" --auth-mode login -c "$CONTAINER" -n "$stale" --only-show-errors
done

echo "4. Running the indexer"
# The run we wait for is whichever one isn't the indexer's previous run (no clock comparison, so no skew).
last_start() { python3 -c 'import json,sys; print((json.load(sys.stdin).get("lastResult") or {}).get("startTime") or "")'; }
PREVIOUS="$(search GET "indexers/$INDEX_NAME-indexer/status" 2>/dev/null | last_start || true)"
put indexer.json indexers   # creating an indexer also starts a run
# 409 means the run that creating the indexer started is still going; that run is the one we wait for.
if ! search POST "indexers/$INDEX_NAME-indexer/run" >/dev/null 2>"$CHUNKS/../run-error.txt"; then
  grep -q "HTTP 409" "$CHUNKS/../run-error.txt" || { cat "$CHUNKS/../run-error.txt" >&2; exit 1; }
fi

for _ in $(seq 1 60); do
  sleep 10
  STATUS="$(search GET "indexers/$INDEX_NAME-indexer/status")"
  STATE="$(python3 -c '
import json, sys
r = (json.loads(sys.argv[1]).get("lastResult") or {})
done = r.get("status") not in (None, "inProgress") and (r.get("startTime") or "") != sys.argv[2]
print(r.get("status") if done else "running")' "$STATUS" "$PREVIOUS")"
  [ "$STATE" = running ] || break
done

python3 - "$STATUS" <<'EOF'
import json, sys
r = json.loads(sys.argv[1])["lastResult"]
print(f"  status={r['status']} processed={r['itemsProcessed']} failed={r['itemsFailed']}")
for e in r.get("errors", [])[:10]:
    print(f"  error: {e.get('key')}: {e.get('errorMessage')}")
sys.exit(0 if r["status"] == "success" and r["itemsFailed"] == 0 else 1)
EOF

echo "5. Checking the document count"
for _ in $(seq 1 12); do
  COUNT="$(search GET "indexes/$INDEX_NAME/docs/\$count" | tr -dc '0-9')"
  [ "$COUNT" = "$EXPECTED" ] && break
  sleep 5
done
echo "  $COUNT chunks in $INDEX_NAME, $EXPECTED expected"
[ "$COUNT" = "$EXPECTED" ] || { echo "ERROR: document count mismatch" >&2; exit 1; }

echo "6. Alias $ALIAS"
update_alias
