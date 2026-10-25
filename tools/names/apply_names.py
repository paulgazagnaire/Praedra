#!/usr/bin/env python3
"""Swap the Classic placeholder names for Praedra names, using data/design/name-migrations.csv (kind, old, new).

Run from the repository root:  python3 -I tools/names/apply_names.py [--check]

What it rewrites:
- every CSV under data/classic and data/design (not name-migrations.csv itself): a cell equal to an old name becomes the new name, and whole old names inside a list or sentence cell are swapped too, except in a "kind" column (mission kinds such as Dagobah are not places);
- every C# file under core, core.tests, sim, sim.tests and godot/Scripts: old names inside string literals
  and comments, as whole names (longest first, so "Imperial II Star Destroyer" wins over "Imperial Star Destroyer").
C# identifiers are never touched (old system or ship words inside names): renaming those is a separate, reviewed change.
--check only reports what it would change and exits 1 if anything is left.

Run it again after copying a new data file from the shared specs (the shared files may still use the old names); it changes nothing
in files that are already renamed. Old saves are renamed on load from the same table (core/Setup/NameMigrations.cs).
"""
import csv
import io
import os
import re
import sys

ROOT = os.getcwd()
TABLE = os.path.join("data", "design", "name-migrations.csv")
# The word table and the cut list hold old names on purpose.
KEEP = {os.path.join("data", "design", "word-migrations.csv"), os.path.join("data", "design", "cut-classes.csv")}
# Columns whose cells are never names of places or units.
SKIP_COLUMNS = {"kind"}
# Files that must keep old names on purpose (the save-migration test writes an old save).
SKIP_FILES = {os.path.join("core.tests", "Setup", "NameMigrationTests.cs")}
CODE_DIRS = ["core", "core.tests", "sim", "sim.tests", os.path.join("godot", "Scripts")]


def load_table():
    with open(os.path.join(ROOT, TABLE), newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    m = {r["old"]: r["new"] for r in rows}
    if len(m) != len(rows):
        sys.exit("name-migrations.csv: an old name appears twice")
    clash = set(m) & set(m.values())
    if clash:
        sys.exit(f"name-migrations.csv: new names that are also old names: {sorted(clash)}")
    return m


def csv_files():
    for d in [os.path.join("data", "classic"), os.path.join("data", "design")]:
        for dp, _, fn in os.walk(os.path.join(ROOT, d)):
            for f in sorted(fn):
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, ROOT)
                if f.endswith(".csv") and rel != TABLE and rel not in KEEP:
                    yield p


def code_files():
    for d in CODE_DIRS:
        for dp, dn, fn in os.walk(os.path.join(ROOT, d)):
            dn[:] = [x for x in dn if x not in ("bin", "obj", ".godot")]
            for f in sorted(fn):
                if f.endswith(".cs") and os.path.relpath(os.path.join(dp, f), ROOT) not in SKIP_FILES:
                    yield os.path.join(dp, f)


def rewrite_csv(text, m, pat):
    out = io.StringIO()
    rows = list(csv.reader(io.StringIO(text)))
    changed = 0
    w = csv.writer(out, lineterminator="\r\n" if "\r\n" in text else "\n")
    skip = {i for i, h in enumerate(rows[0]) if h in SKIP_COLUMNS} if rows else set()
    for n, row in enumerate(rows):
        new = []
        for i, cell in enumerate(row):
            if n > 0 and i not in skip and cell in m:
                new.append(m[cell]); changed += 1
            elif n > 0 and i not in skip:
                # a list or a sentence ("Imperial Star Destroyer*2;TIE Fighter*4"): whole names inside it
                cell2, k = pat.subn(lambda mo: m[mo.group(0)], cell)
                new.append(cell2); changed += k
            else:
                new.append(cell)
        w.writerow(new)
    return out.getvalue(), changed


# A string literal ("..." or @"..." or $"..."), a // comment, or anything else, scanned left to right so a // inside a string
# is not taken for a comment.
TOKEN = re.compile(r'(?P<str>[$@]*"(?:[^"\\\n]|\\.)*")|(?P<com>//[^\n]*)|(?P<chr>\'(?:[^\'\\\n]|\\.)\')', re.S)


def rewrite_code(text, pat, m):
    changed = 0

    def sub_names(s):
        nonlocal changed
        def one(mo):
            nonlocal changed
            changed += 1
            return m[mo.group(0)]
        return pat.sub(one, s)

    out, pos = [], 0
    for mo in TOKEN.finditer(text):
        out.append(text[pos:mo.start()])
        tok = mo.group(0)
        out.append(sub_names(tok) if mo.group("str") or mo.group("com") else tok)
        pos = mo.end()
    out.append(text[pos:])
    return "".join(out), changed


def main():
    check = "--check" in sys.argv
    m = load_table()
    names = sorted(m, key=len, reverse=True)
    # a name starts after a non-word character, or right after a \n / \r / \t escape inside a string ("...\nBespin,...")
    pat = re.compile(r"(?:(?<=\\[nrt])|(?<![\w.'-]))(?:" + "|".join(re.escape(n) for n in names) + r")(?![\w'-])")
    total = 0
    for p in list(csv_files()) + list(code_files()):
        text = open(p, encoding="utf-8").read()
        new, n = rewrite_csv(text, m, pat) if p.endswith(".csv") else rewrite_code(text, pat, m)
        if n and new != text:
            total += n
            print(f"{os.path.relpath(p, ROOT)}: {n}")
            if not check:
                open(p, "w", encoding="utf-8", newline="").write(new)
    print(f"{'would change' if check else 'changed'} {total} names")
    return 1 if check and total else 0


if __name__ == "__main__":
    sys.exit(main())
