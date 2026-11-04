"""Grades Maple Bank's dispute agents against the ground truth (resources-foundry/evals/datasets/dispute-cases.jsonl).

For each case the agents run as a chain, the way the workflow will run them: intake -> evidence -> policy,
each receiving the earlier agents' JSON. Each agent is then graded on its own output field by field: no
judge model, just comparison with the answer the generator wrote down independently. Comparing structured
output with known answers is far more reliable than judging free text.

Also recorded per agent: which mcp-banking tools it called (the policy agent must call the rules engine),
tokens and latency, so model choices come with a cost.

Gates (plan section 8.4); the run fails if any is missed:
  intake    dispute_type correct >= 95%    fraud cases flagged possible_fraud = 100%
  evidence  primary transaction found >= 95%
  policy    action correct >= 95%          credit amount exact = 100%     cited section correct >= 90%
            agrees with the rules engine = 100%    called evaluate_dispute_rules = 100%

Usage:
  python3 tools/agent-evals/run_dispute_evals.py                        # all 40 cases, all agents
  python3 tools/agent-evals/run_dispute_evals.py --stage intake --intake-agent intake-agent-mini
  python3 tools/agent-evals/run_dispute_evals.py --cases CASE-0001,CASE-0007
"""

import argparse
import concurrent.futures
import json
import os
import random
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATASET = ROOT / "resources-foundry" / "evals" / "datasets" / "dispute-cases.jsonl"
RESULTS = ROOT / "resources-foundry" / "evals" / "build"

# USD per million tokens (input, output), for comparing models. Check current prices before quoting them.
PRICES = {"gpt-5-nano": (0.05, 0.40), "gpt-5-mini": (0.25, 2.00)}

TLS = ssl.create_default_context()
if os.path.exists("/etc/ssl/cert.pem"):  # the python.org build for macOS ships without CA certificates
    TLS.load_verify_locations("/etc/ssl/cert.pem")


class AgentFailed(Exception):
    pass


class Foundry:
    def __init__(self, endpoint):
        self.url = f"{endpoint.rstrip('/')}/openai/v1/responses"
        self.lock = threading.Lock()
        self.token, self.token_time = None, 0.0

    def _token(self):
        with self.lock:
            if time.time() - self.token_time > 1800:
                self.token = subprocess.run(["az", "account", "get-access-token", "--resource", "https://ai.azure.com",
                                             "--query", "accessToken", "-o", "tsv"],
                                            capture_output=True, text=True, check=True).stdout.strip()
                self.token_time = time.time()
            return self.token

    def run(self, agent, prompt):
        """Runs one agent turn. Returns (parsed JSON output, tool names called, usage, model, seconds)."""
        body = json.dumps({"agent_reference": {"type": "agent_reference", "name": agent}, "input": prompt}).encode()
        for attempt in range(7):
            started = time.time()
            request = urllib.request.Request(self.url, data=body, method="POST", headers={
                "Authorization": f"Bearer {self._token()}", "Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(request, context=TLS, timeout=300) as response:
                    result = json.load(response)
                break
            except urllib.error.HTTPError as e:
                detail = e.read().decode()[:300]
                # A rate limit is the model deployment's tokens-per-minute quota, shared by every case running in
                # parallel. Wait as long as the service asks, else back off exponentially, with jitter.
                if e.code in (429, 500, 502, 503) and attempt < 6:
                    wait = float(e.headers.get("Retry-After") or min(10 * 2 ** attempt, 120))
                    time.sleep(wait + random.uniform(0, 5))
                    continue
                raise AgentFailed(f"{agent}: HTTP {e.code} {detail}")
        seconds = time.time() - started

        if result.get("error"):
            raise AgentFailed(f"{agent}: {result['error']}")
        tools = [o.get("name") for o in result.get("output", []) if o.get("type") == "mcp_call"]
        tool_errors = [o.get("error") for o in result.get("output", []) if o.get("type") == "mcp_call" and o.get("error")]
        texts = [c.get("text", "") for o in result.get("output", []) if o.get("type") == "message" for c in o.get("content", [])]
        if not texts:
            raise AgentFailed(f"{agent}: no message in the response (status {result.get('status')}, tool errors {tool_errors})")
        try:
            output = json.loads(texts[-1])
        except ValueError:
            raise AgentFailed(f"{agent}: output is not JSON: {texts[-1][:200]}")
        return output, tools, result.get("usage") or {}, result.get("model", ""), seconds


def run_case(foundry, case, agents, stage):
    case_id = case["case_id"]
    record = {"case_id": case_id, "truth": case["truth"], "runs": {}}
    try:
        intake = step(record, "intake", foundry.run(agents["intake"], f"Case ID: {case_id}"))
        if stage == "intake":
            return record
        evidence = step(record, "evidence", foundry.run(
            agents["evidence"], f"Case ID: {case_id}\n\nIntake report:\n{json.dumps(intake, ensure_ascii=False)}"))
        if stage == "evidence":
            return record
        step(record, "policy", foundry.run(agents["policy"], (
            f"Case ID: {case_id}\n\nIntake report:\n{json.dumps(intake, ensure_ascii=False)}"
            f"\n\nEvidence report:\n{json.dumps(evidence, ensure_ascii=False)}")))
    except AgentFailed as e:
        record["error"] = str(e)
    return record


def step(record, name, result):
    output, tools, usage, model, seconds = result
    record["runs"][name] = {"output": output, "tools": tools, "model": model, "seconds": round(seconds, 1),
                            "input_tokens": usage.get("input_tokens", 0), "output_tokens": usage.get("output_tokens", 0)}
    return output


def money(amount):
    return round(float(amount or 0), 2)


def grade(records):
    """Returns {check name: (passed, total, gate)} and per-case failure notes."""
    checks, notes = {}, {}

    def check(name, gate, case_id, ok, note=""):
        passed, total, _ = checks.get(name, (0, 0, gate))
        checks[name] = (passed + bool(ok), total + 1, gate)
        if not ok:
            notes.setdefault(case_id, []).append(f"{name}: {note}")

    for r in records:
        t, runs, cid = r["truth"], r["runs"], r["case_id"]
        if "intake" in runs:
            o = runs["intake"]["output"]
            check("intake: dispute_type", 0.95, cid, o["dispute_type"] == t["dispute_type"],
                  f"{o['dispute_type']} (truth {t['dispute_type']})")
            if t["action"] == "route_to_fraud":
                check("intake: fraud flagged", 1.0, cid, o["possible_fraud"], "possible_fraud was false")
        if "evidence" in runs:
            o = runs["evidence"]["output"]
            found = set(o["transaction_ids"])
            check("evidence: primary transaction found", 0.95, cid, t["primary_transaction"] in found,
                  f"{sorted(found)} (truth {t['transactions']})")
            check("evidence: exact transaction set", None, cid, found == set(t["transactions"]),
                  f"{sorted(found)} (truth {sorted(t['transactions'])})")
        if "policy" in runs:
            o, tools = runs["policy"]["output"], runs["policy"]["tools"]
            check("policy: action", 0.95, cid, o["action"] == t["action"], f"{o['action']} (truth {t['action']})")
            if t["action"] == "recommend_credit":
                check("policy: credit amount exact", 1.0, cid,
                      o["action"] == t["action"] and abs(money(o["amount"]) - money(t["amount"])) < 0.01,
                      f"{o['action']} {money(o['amount'])} (truth {money(t['amount'])})")
            check("policy: cited section", 0.90, cid, t["policy_section"] in o["policy_sections"],
                  f"{o['policy_sections']} (truth {t['policy_section']})")
            check("policy: agrees with rules engine", 1.0, cid,
                  o["policy_version"] == t["policy_version"] and o["within_time_limit"] == t["within_window"]
                  and o["needs_senior_approval"] == t["needs_senior_approval"],
                  f"version {o['policy_version']}/{t['policy_version']}, in time {o['within_time_limit']}/{t['within_window']}, "
                  f"senior {o['needs_senior_approval']}/{t['needs_senior_approval']}")
            check("policy: called evaluate_dispute_rules", 1.0, cid, "evaluate_dispute_rules" in tools, f"tools: {tools}")
            check("policy: called search_policy", None, cid, "search_policy" in tools, f"tools: {tools}")
    return checks, notes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=["intake", "evidence", "policy"], default="policy",
                        help="run the chain up to this agent (default: all three)")
    parser.add_argument("--cases", help="comma-separated case IDs (default: all)")
    parser.add_argument("--intake-agent", default="intake-agent")
    parser.add_argument("--evidence-agent", default="evidence-agent")
    parser.add_argument("--policy-agent", default="policy-agent")
    parser.add_argument("--workers", type=int, default=2)  # more trips gpt-5-mini's 100K tokens/minute
    args = parser.parse_args()

    endpoint = os.environ.get("AZURE_AI_PROJECT_ENDPOINT") or subprocess.run(
        ["azd", "env", "get-value", "AZURE_AI_PROJECT_ENDPOINT"], cwd=ROOT / "resources-foundry",
        capture_output=True, text=True).stdout.strip()
    cases = [json.loads(line) for line in DATASET.read_text().splitlines() if line.strip()]
    if args.cases:
        wanted = set(args.cases.split(","))
        cases = [c for c in cases if c["case_id"] in wanted]
    agents = {"intake": args.intake_agent, "evidence": args.evidence_agent, "policy": args.policy_agent}

    foundry = Foundry(endpoint)
    print(f"Running {len(cases)} cases through {args.stage if args.stage == 'intake' else 'intake -> ' + args.stage} "
          f"({', '.join(agents[s] for s in ['intake', 'evidence', 'policy'][:['intake', 'evidence', 'policy'].index(args.stage) + 1])})")
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(args.workers) as pool:
        records = sorted(pool.map(lambda c: run_case(foundry, c, agents, args.stage), cases), key=lambda r: r["case_id"])

    RESULTS.mkdir(parents=True, exist_ok=True)
    out = RESULTS / f"dispute-evals-{datetime.now(timezone.utc):%Y%m%d-%H%M%S}.jsonl"
    out.write_text("\n".join(json.dumps(r, ensure_ascii=False) for r in records) + "\n")

    errors = [r for r in records if "error" in r]
    checks, notes = grade([r for r in records if "error" not in r])

    print(f"\n{'check':40} {'result':>12}  gate")
    failed_gates = []
    for name, (passed, total, gate) in checks.items():
        rate = passed / total if total else 0
        status = "" if gate is None else ("PASS" if rate >= gate else "FAIL")
        if status == "FAIL":
            failed_gates.append(name)
        print(f"{name:40} {passed:>3}/{total:<3} {rate:>4.0%}  {'' if gate is None else f'>= {gate:.0%}':7} {status}")

    print(f"\n{'agent':10} {'model':12} {'avg s':>6} {'in tok':>8} {'out tok':>8} {'USD/case':>9}")
    for stage_name in ["intake", "evidence", "policy"]:
        runs = [r["runs"][stage_name] for r in records if stage_name in r["runs"]]
        if not runs:
            continue
        model = runs[0]["model"]
        tin, tout = sum(x["input_tokens"] for x in runs) / len(runs), sum(x["output_tokens"] for x in runs) / len(runs)
        price = next((p for m, p in PRICES.items() if model.startswith(m)), None)
        cost = f"{(tin * price[0] + tout * price[1]) / 1e6:.5f}" if price else "?"
        print(f"{stage_name:10} {model[:12]:12} {sum(x['seconds'] for x in runs) / len(runs):6.1f} {tin:8.0f} {tout:8.0f} {cost:>9}")

    if notes:
        print("\nFailures by case:")
        for cid in sorted(notes):
            for n in notes[cid]:
                print(f"  {cid}  {n}")
    for r in errors:
        print(f"  {r['case_id']}  ERROR {r['error']}")

    print(f"\n{len(records)} cases in {time.time() - started:.0f}s, {len(errors)} errors; results: {out.relative_to(ROOT)}")
    if failed_gates or errors:
        print(f"GATE FAILED: {', '.join(failed_gates) or 'agent errors'}")
        sys.exit(1)


if __name__ == "__main__":
    main()
