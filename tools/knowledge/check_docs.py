"""Checks Maple Bank's knowledge documents before they're indexed. Exits non-zero on any problem.

1. Front matter: every required field present, with allowed values.
2. Footer: every document says it's fictional.
3. Policy versions: effective date ranges don't overlap.
4. Rules match policy: each policy version's time-limit table and senior-approval threshold equal
   src/MafStarterPack.McpBanking/rules/dispute-rules.json, so the readable policy and the machine
   rules can't drift apart.
5. Personal data: nothing that looks like a full card number or an email address.
6. Prompt injection: no text that reads like instructions to a model. Indexed text ends up in the agents'
   prompts, so a poisoned document is an attack on every case. (A cheap pattern check; Prompt Shields is
   the stronger, model-based check, Day 17.)

Standard library only. Usage: python3 tools/knowledge/check_docs.py
"""

import json
import re
import sys
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs" / "maple-bank"
RULES = ROOT / "src" / "MafStarterPack.McpBanking" / "rules" / "dispute-rules.json"

REQUIRED = ["title", "doc_type", "dispute_types", "version", "effective_from", "effective_to", "owner",
            "approved_by", "audience", "language"]
DOC_TYPES = {"policy", "network_rule", "sop", "template", "faq"}
DISPUTE_TYPES = {"duplicate_charge", "incorrect_amount", "credit_not_processed", "cancelled_recurring",
                 "not_received", "unauthorised"}
AUDIENCES = {"analyst", "senior_analyst"}
LANGUAGES = {"en", "fr"}
FOOTER = "Maple Bank is fictional"
INJECTION = re.compile(
    r"ignore (all |any )?(the )?(previous|prior|above|earlier) (instructions|rules)"
    r"|disregard (the|your|all) (instructions|rules|policy)"
    r"|you are now\b|new instructions:|system prompt|</?(system|assistant)>|\[INST\]"
    r"|reveal (your|the) (instructions|prompt)|do not tell the (analyst|user)",
    re.I)

problems = []


def problem(path, message):
    problems.append(f"{path.relative_to(ROOT)}: {message}")


def front_matter(path, text):
    """Parses the simple `key: value` / `key: [a, b]` front matter these documents use."""
    match = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    if not match:
        problem(path, "no front matter")
        return {}
    fields = {}
    for line in match.group(1).splitlines():
        key, _, value = line.partition(":")
        value = value.strip()
        if value.startswith("[") and value.endswith("]"):
            fields[key.strip()] = [v.strip() for v in value[1:-1].split(",") if v.strip()]
        else:
            fields[key.strip()] = value.strip('"')
    return fields


def parse_date(path, fields, key, required=True):
    value = fields.get(key, "")
    if not value:
        if required:
            problem(path, f"{key} is empty")
        return None
    try:
        return date.fromisoformat(value)
    except ValueError:
        problem(path, f"{key} is not a date: {value}")
        return None


docs = sorted(DOCS.rglob("*.md"))
if not docs:
    problem(DOCS, "no documents found")

policies = []
for path in docs:
    text = path.read_text(encoding="utf-8")
    fields = front_matter(path, text)
    if not fields:
        continue

    for key in REQUIRED:
        if key not in fields:
            problem(path, f"missing front matter field '{key}'")
    if fields.get("doc_type") not in DOC_TYPES:
        problem(path, f"doc_type must be one of {sorted(DOC_TYPES)}")
    if not set(fields.get("dispute_types", [])) <= DISPUTE_TYPES:
        problem(path, f"unknown dispute_types: {sorted(set(fields['dispute_types']) - DISPUTE_TYPES)}")
    if not fields.get("audience") or not set(fields["audience"]) <= AUDIENCES:
        problem(path, f"audience must be a non-empty subset of {sorted(AUDIENCES)}")
    if fields.get("language") not in LANGUAGES:
        problem(path, f"language must be one of {sorted(LANGUAGES)}")
    start = parse_date(path, fields, "effective_from")
    end = parse_date(path, fields, "effective_to", required=False)
    if start and end and end <= start:
        problem(path, "effective_to must be after effective_from")

    if FOOTER not in text:
        problem(path, f"missing the footer saying '{FOOTER}'")

    body = text.split("\n---\n", 2)[-1]
    if re.search(r"\b(?:\d[ -]?){13,19}\b", body):
        problem(path, "contains something that looks like a full card number")
    if re.search(r"[\w.+-]+@[\w-]+\.[\w.]+", body):
        problem(path, "contains an email address")
    if match := INJECTION.search(body):
        problem(path, f"contains text that reads like a prompt injection: '{match.group(0)}'")

    if fields.get("doc_type") == "policy":
        policies.append((path, fields, start, end, text))

# Policy versions must not overlap.
spans = sorted((start, end or date.max, path) for path, _, start, end, _ in policies if start)
for (s1, e1, p1), (s2, e2, p2) in zip(spans, spans[1:]):
    if s2 < e1:
        problem(p2, f"effective dates overlap with {p1.relative_to(ROOT)}")

# Each policy version must match the machine-readable rules.
rules = json.loads(RULES.read_text(encoding="utf-8"))
rule_versions = {v["version"]: v for v in rules["policy_versions"]}
for path, fields, start, end, text in policies:
    version = fields.get("version")
    rule = rule_versions.get(version)
    if rule is None:
        problem(path, f"policy version {version} has no entry in {RULES.name}")
        continue
    if str(start) != rule["effective_from"] or (str(end) if end else None) != rule["effective_to"]:
        problem(path, f"effective dates differ from {RULES.name} version {version}")
    limits = {m.group(1): int(m.group(2)) for m in re.finditer(r"\(`(\w+)`\)\s*\|\s*(\d+) days", text)}
    if limits != rule["time_limit_days"]:
        problem(path, f"time limits {limits} differ from {RULES.name}: {rule['time_limit_days']}")
    threshold = re.search(r"Senior approval threshold: \$([\d,]+\.\d\d) CAD", text)
    if not threshold or float(threshold.group(1).replace(",", "")) != rule["senior_approval_over_cad"]:
        problem(path, f"senior approval threshold differs from {RULES.name}: {rule['senior_approval_over_cad']}")
    for dispute_type, rule_type in rules["dispute_types"].items():
        section = rule_type["policy_section"]
        if not re.search(rf"^#+ {re.escape(section)} ", text, re.M):
            problem(path, f"section {section} ({dispute_type}) named in {RULES.name} is missing")

if problems:
    print(f"{len(problems)} problem(s):")
    for p in problems:
        print(f"  - {p}")
    sys.exit(1)

print(f"OK: {len(docs)} documents, {len(policies)} policy versions match {RULES.name}.")
