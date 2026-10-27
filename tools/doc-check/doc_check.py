"""Warn about doc comments left on the wrong member (Ponytail, CI step "doc comments", warnings only).

Reads the XML doc file the compiler writes with GenerateDocumentationFile and prints a GitHub warning for every
member that carries more than one <summary>. That is what a doc comment looks like after code was inserted between
it and its member: the compiler silently merges both comments onto the next member (#174, #184, #186). Partial types
are skipped, because each of their files may document the type; they are found by scanning the source folder for
"partial class/record/struct/interface Name". Always exits 0.

Usage: python3 tools/doc-check/doc_check.py <path to Praedra.Core.xml> <source folder, e.g. core>
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

partial = set()
for path in pathlib.Path(sys.argv[2]).rglob("*.cs"):
    partial.update(re.findall(r"\bpartial\s+(?:class|record|struct|interface)\s+(\w+)", path.read_text(encoding="utf-8")))

found = 0
for member in ET.parse(sys.argv[1]).getroot().iter("member"):
    name = member.get("name", "")
    count = len(member.findall("summary"))
    if count < 2 or (name.startswith("T:") and name.rsplit(".", 1)[-1].split("`")[0] in partial):
        continue
    print(f"::warning title=Doc comment on the wrong member::{name} has {count} <summary> blocks: one was probably left behind when code was inserted or moved")
    found += 1
print(f"doc check: {found} member(s) with more than one <summary> (partial types skipped: {len(partial)})")
