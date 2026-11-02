"""Retrieval tests for Maple Bank's knowledge index: does search return the right section for each question?

Each test in resources-foundry/evals/datasets/knowledge-retrieval.jsonl runs the same query the agents will:
hybrid (keywords + vector) with the semantic ranker, top 3, plus filters:

- as_of: the transaction date. Only documents in force on that date (effective_from <= date < effective_to).
- language, audience, doc_type: optional filters.

A test passes when one of the top 3 results matches every field in "expect" ("section" matches by prefix, so
"4.2" matches "4.2 Duplicate charge"). An "expect_none" test passes when no result's semantic score reaches
NO_ANSWER_SCORE: the index has nothing on that question, and the agent should say so instead of guessing.

The gate: hit@3 >= --min-hit (default 0.9) AND every date_filter test passes. A wrong policy version is a
compliance failure, not a quality dip, so those tests can't be averaged away.

Signs in through the Azure CLI (Search Index Data Reader is enough). Standard library only.
  python3 tools/knowledge/retrieval_test.py                         # run the tests
  python3 tools/knowledge/retrieval_test.py -q "time limit for a duplicate charge?" --as-of 2026-10-15
"""

import argparse
import json
import os
import ssl
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATASET = ROOT / "resources-foundry" / "evals" / "datasets" / "knowledge-retrieval.jsonl"
API = "2025-08-01-preview"   # querying through an index alias needs a preview API version
NO_ANSWER_SCORE = 2.0        # semantic reranker score, 0-4; calibrated on the no-answer tests

# The python.org build for macOS ships without CA certificates; use the system bundle when it exists.
TLS = ssl.create_default_context()
if os.path.exists("/etc/ssl/cert.pem"):
    TLS.load_verify_locations("/etc/ssl/cert.pem")


def azd_value(name):
    result = subprocess.run(["azd", "env", "get-value", name], cwd=ROOT / "resources-foundry",
                            capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else ""


def token():
    return subprocess.run(["az", "account", "get-access-token", "--resource", "https://search.azure.com",
                           "--query", "accessToken", "-o", "tsv"], capture_output=True, text=True, check=True).stdout.strip()


def build_filter(as_of=None, language=None, audience=None, doc_type=None):
    parts = []
    if as_of:
        when = f"{as_of}T00:00:00Z"
        parts.append(f"effective_from le {when} and (effective_to eq null or effective_to gt {when})")
    if language:
        parts.append(f"language eq '{language}'")
    if audience:
        parts.append(f"audience/any(a: a eq '{audience}')")
    if doc_type:
        parts.append(f"doc_type eq '{doc_type}'")
    return " and ".join(parts) or None


def search(endpoint, index, auth, question, flt, top=3):
    body = {
        "search": question,
        "vectorQueries": [{"kind": "text", "text": question, "fields": "chunk_vector", "k": 20}],
        "queryType": "semantic",
        "top": top,
        "select": "chunk_id,parent_path,section,version,language,doc_type",
    }
    if flt:
        body["filter"] = flt
    request = urllib.request.Request(f"{endpoint}/indexes/{index}/docs/search?api-version={API}",
                                     data=json.dumps(body).encode(), method="POST",
                                     headers={"Authorization": f"Bearer {auth}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, context=TLS) as response:
            return json.load(response)["value"]
    except urllib.error.HTTPError as e:
        sys.exit(f"ERROR: search returned HTTP {e.code}: {e.read().decode()}")


def matches(result, expect):
    for key, value in expect.items():
        actual = result.get(key) or ""
        if key == "section" and not actual.startswith(value):
            return False
        if key != "section" and actual != value:
            return False
    return True


def describe(result):
    return f"{result.get('@search.rerankerScore', 0):.2f}  {result['parent_path']}  v{result['version']}  {result['section']}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("-q", "--query", help="run one question instead of the tests")
    parser.add_argument("--as-of"), parser.add_argument("--language"), parser.add_argument("--audience")
    parser.add_argument("--doc-type")
    parser.add_argument("--index", default="maple-knowledge", help="index or alias (default: the alias)")
    parser.add_argument("--dataset", type=Path, default=DATASET)
    parser.add_argument("--min-hit", type=float, default=0.9)
    args = parser.parse_args()

    service = os.environ.get("SEARCH_SERVICE") or azd_value("AZURE_AI_SEARCH_SERVICE_NAME")
    if not service:
        sys.exit("ERROR: set SEARCH_SERVICE, or run where the azd environment has AZURE_AI_SEARCH_SERVICE_NAME")
    endpoint, auth = f"https://{service}.search.windows.net", token()

    if args.query:
        for result in search(endpoint, args.index, auth, args.query,
                             build_filter(args.as_of, args.language, args.audience, args.doc_type), top=5):
            print(describe(result))
        return

    tests = [json.loads(line) for line in args.dataset.read_text(encoding="utf-8").splitlines() if line.strip()]
    passed, failed_dates = 0, []
    for test in tests:
        flt = build_filter(test.get("as_of"), test.get("language"), test.get("audience"), test.get("doc_type"))
        results = search(endpoint, args.index, auth, test["question"], flt)
        if test.get("expect_none"):
            ok = all(r.get("@search.rerankerScore", 0) < NO_ANSWER_SCORE for r in results)
        else:
            ok = any(matches(r, test["expect"]) for r in results)
        passed += ok
        if not ok and test["group"] == "date_filter":
            failed_dates.append(test["id"])
        print(f"{'PASS' if ok else 'FAIL'}  {test['id']:8} {test['group']:12} {test['question']}")
        if not ok:
            print(f"        expected {test.get('expect', 'no answer')}")
            for result in results:
                print(f"        got  {describe(result)}")

    hit = passed / len(tests)
    print(f"\nhit@3 = {passed}/{len(tests)} = {hit:.0%} (gate {args.min_hit:.0%}); "
          f"date-filter failures: {len(failed_dates)}")
    if hit < args.min_hit or failed_dates:
        sys.exit(1)


if __name__ == "__main__":
    main()
