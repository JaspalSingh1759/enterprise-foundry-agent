"""Splits Maple Bank's knowledge documents into chunks for the search index, one JSON file per chunk.

Why our own chunker instead of the search service's split skill: these documents have numbered sections
("4.2 Duplicate charge"), and a section is what analysts and the agents cite. Splitting every N characters
would cut sections in half and lose the section number.

- Documents under SMALL_DOC_CHARS (SOPs, letter templates) stay whole: their parts only make sense together.
- Longer documents (policy, network rules, FAQ) become one chunk per section (## or ###). A heading with no
  text of its own ("## 3 General rules") becomes part of its children's header instead.
- Every chunk starts with a header such as "Card Dispute Policy › 3 General rules › 3.1 Time limits", so the
  chunk still makes sense on its own and the header words count in keyword and vector search.
- A section longer than MAX_CHUNK_CHARS is split between paragraphs. Tables have no blank lines inside, so
  they are never split.
- Each chunk carries the document's front matter (version, effective dates, audience...) as filterable fields.

The search service embeds each chunk (skillset in resources-foundry/knowledge/).

Standard library only. Usage: python3 tools/knowledge/chunk_docs.py [output_dir]
"""

import json
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs" / "maple-bank"
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "resources-foundry" / "knowledge" / "build" / "chunks"

SMALL_DOC_CHARS = 2000
MAX_CHUNK_CHARS = 3000
HEADING = re.compile(r"^(#{2,3}) (.+)$")


def front_matter(text):
    match = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    fields = {}
    for line in match.group(1).splitlines():
        key, _, value = line.partition(":")
        value = value.strip()
        if value.startswith("[") and value.endswith("]"):
            fields[key.strip()] = [v.strip() for v in value[1:-1].split(",") if v.strip()]
        else:
            fields[key.strip()] = value.strip('"')
    return fields, text[match.end():]


def strip_title_and_footer(body):
    """Drops the '# Title' line (the title goes in every header) and the fictional-footer after the last rule."""
    body = body.rsplit("\n---\n", 1)[0]
    lines = body.strip().splitlines()
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
    return "\n".join(lines).strip()


def sections(body):
    """Yields (heading path, text) for each section that has text of its own."""
    path, lines, in_code = [], [], False

    def flush():
        text = "\n".join(lines).strip()
        if text:
            yield list(path), text

    for line in body.splitlines():
        if line.startswith("```"):
            in_code = not in_code
        match = None if in_code else HEADING.match(line)
        if match:
            yield from flush()
            lines = []
            level = len(match.group(1))  # 2 or 3
            path = path[:level - 2] + [match.group(2).strip()]
        else:
            lines.append(line)
    yield from flush()


def split_long(text):
    """Splits text into pieces of at most MAX_CHUNK_CHARS, between paragraphs."""
    pieces, current = [], ""
    for paragraph in text.split("\n\n"):
        if current and len(current) + len(paragraph) + 2 > MAX_CHUNK_CHARS:
            pieces.append(current)
            current = paragraph
        else:
            current = f"{current}\n\n{paragraph}" if current else paragraph
    return pieces + [current] if current else pieces


def iso(value):
    return f"{value}T00:00:00Z" if value else None


def chunks_for(path):
    fields, body = front_matter(path.read_text(encoding="utf-8"))
    body = strip_title_and_footer(body)
    title = fields["title"]
    rel = path.relative_to(DOCS).as_posix()

    if len(body) < SMALL_DOC_CHARS:
        parts = [([], body)]
    else:
        parts = [(heading_path, piece) for heading_path, text in sections(body) for piece in split_long(text)]

    for n, (heading_path, text) in enumerate(parts, start=1):
        header = " › ".join([title] + heading_path)
        yield {
            "chunk_id": f"{re.sub(r'[^A-Za-z0-9]+', '-', rel.removesuffix('.md'))}_{n:02d}",
            "parent_path": rel,
            "title": title,
            "section": heading_path[-1] if heading_path else "",
            "doc_type": fields["doc_type"],
            "dispute_types": fields["dispute_types"],
            "version": fields["version"],
            "effective_from": iso(fields["effective_from"]),
            "effective_to": iso(fields["effective_to"]),
            "language": fields["language"],
            "audience": fields["audience"],
            "owner": fields["owner"],
            "approved_by": fields["approved_by"],
            "chunk": f"{header}\n\n{text}",
        }


# Start from an empty folder so a removed section or document disappears from the upload, and then from the
# index (the upload deletes blobs that are gone; the indexer's deletion detection removes their chunks).
if OUT.exists():
    shutil.rmtree(OUT)
OUT.mkdir(parents=True)

count = 0
docs = sorted(DOCS.rglob("*.md"))
for path in docs:
    for chunk in chunks_for(path):
        (OUT / f"{chunk['chunk_id']}.json").write_text(json.dumps(chunk, ensure_ascii=False, indent=2), encoding="utf-8")
        count += 1

print(f"Wrote {count} chunks from {len(docs)} documents to {OUT.relative_to(ROOT)}")
