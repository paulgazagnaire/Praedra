"""Warn about doc comments left on the wrong member (Ponytail, CI step "doc comments", warnings only).

Reads the XML doc file the compiler writes with GenerateDocumentationFile and prints a GitHub warning for every
member that carries more than one <summary>. That is what a doc comment looks like after code was inserted between
it and its member: the compiler silently merges both comments onto the next member (#174, #184, #186). Types (T:) are
skipped, because each file of a partial class may document it. Always exits 0.

Usage: python3 tools/doc-check/doc_check.py <path to Praedra.Core.xml>
"""
import sys
import xml.etree.ElementTree as ET

found = 0
for member in ET.parse(sys.argv[1]).getroot().iter("member"):
    name = member.get("name", "")
    count = len(member.findall("summary"))
    if count > 1 and not name.startswith("T:"):
        print(f"::warning title=Doc comment on the wrong member::{name} has {count} <summary> blocks: one was probably left behind when code was inserted or moved")
        found += 1
print(f"doc check: {found} member(s) with more than one <summary>")
