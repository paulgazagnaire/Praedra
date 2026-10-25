#!/usr/bin/env python3
"""Swap the Classic placeholder WORDS (side names, the Death Star, the tractor beam...) inside identifiers, strings and comments, using
data/design/word-migrations.csv (kind, old, new). apply_names.py swaps whole names (systems, units, characters); this tool swaps words
that live inside longer identifiers too: Side.Empire -> Side.Hand, EmpireWinsOnHqLost -> HandWinsOnHqLost, "the Empire's" -> "the Hand's".

Run from the repository root:  python3 -I tools/names/rename_words.py [--check]

Table kinds, applied in this order:
- phrase: exact text, anywhere (prose fixes such as "an Imperial" -> "a Hand"), longest first;
- identifier: a whole identifier or word (\\b...\\b), longest first;
- word: a word inside an identifier. "Empire" matches in EmpireWins, aIsEmpire, Side.Empire, "the Empire's", not in "Empires"
  (an upper-case word must not be followed by a lower-case letter); "empire" matches at the start of an identifier or after "_"
  (empireAi, is_empire), never inside a longer lower-case word; "EMPIRE" likewise for upper case.
The same word rule renames the keys and identifier-like values of an old save on load (core/Setup/NameMigrations.cs, Words), so a
property renamed here still reads from a save written before.

What it rewrites: the C# files under core, core.tests, sim, sim.tests, importer, importer.tests and godot/Scripts, the CSVs under
data/classic and data/design (headers too, not data/classic/raw), and the Markdown files listed in DOCS. It never touches
data/classic/raw (Classic's own files), the migration tables, or SKIP_FILES. The importer still reads Classic's raw headers
(is_alliance...): those strings are kept in importer/ by RAW_KEEP.
--check only reports what it would change and exits 1 if anything is left.
"""
import csv
import os
import re
import sys

ROOT = os.getcwd()
TABLE = os.path.join("data", "design", "word-migrations.csv")
NAMES = os.path.join("data", "design", "name-migrations.csv")
CUT = os.path.join("data", "design", "cut-classes.csv")  # lists cut classes by their old names on purpose
RAW = os.path.join("data", "classic", "raw") + os.sep
CODE_DIRS = ["core", "core.tests", "sim", "sim.tests", "importer", "importer.tests", os.path.join("godot", "Scripts")]
DOCS = [os.path.join("sim", "README.md"), os.path.join("data", "README.md"), os.path.join("godot", "README.md"), "README.md", "CLAUDE.md"]
# Files that must keep old words on purpose (the save-migration tests write an old save).
SKIP_FILES = {os.path.join("core.tests", "Setup", "NameMigrationTests.cs"), os.path.join("core.tests", "Setup", "WordMigrationTests.cs")}
# Classic's raw column names the importer reads (data/classic/raw keeps Classic's headers word for word); kept in importer/ only, which
# writes them out under the new words (DropInStage.OutHeader).
RAW_KEEP = ["is_alliance", "is_empire", "tractor_beam", "jedi_", "known_jedi"]
RAW_KEEP_DIRS = ("importer" + os.sep,)


def load_table():
    with open(os.path.join(ROOT, TABLE), newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    by = {"phrase": {}, "identifier": {}, "word": {}}
    for r in rows:
        if r["kind"] not in by:
            sys.exit(f"{TABLE}: unknown kind '{r['kind']}'")
        if r["old"] in by[r["kind"]]:
            sys.exit(f"{TABLE}: '{r['old']}' appears twice")
        by[r["kind"]][r["old"]] = r["new"]
    return by


def word_pattern(words):
    """One regex for the word kind (see the module comment)."""
    parts = []
    for w in sorted(words, key=len, reverse=True):
        e = re.escape(w)
        if w[0].isupper() and not w.isupper():
            parts.append(rf"{e}(?![a-z])")
        elif w.isupper():
            parts.append(rf"(?<![A-Z]){e}(?![A-Z])")
        else:
            parts.append(rf"(?<![A-Za-z]){e}(?![a-z])")
    return re.compile("|".join(parts)) if parts else None


def make_rewriter(by):
    phrases = sorted(by["phrase"], key=len, reverse=True)
    ident = re.compile(r"\b(?:" + "|".join(re.escape(k) for k in sorted(by["identifier"], key=len, reverse=True)) + r")\b") if by["identifier"] else None
    words = word_pattern(by["word"])
    keep = re.compile("|".join(re.escape(k) for k in RAW_KEEP))

    def rewrite(text, raw_keep=True):
        n = 0
        # protect the raw column names
        held = []
        def hold(m):
            held.append(m.group(0)); return f"\x00{len(held) - 1}\x00"
        if raw_keep:
            text = keep.sub(hold, text)
        for p in phrases:
            c = text.count(p)
            if c:
                text = text.replace(p, by["phrase"][p]); n += c
        if ident:
            text, k = ident.subn(lambda m: by["identifier"][m.group(0)], text); n += k
        if words:
            text, k = words.subn(lambda m: by["word"][m.group(0)], text); n += k
        text = re.sub("\x00(\\d+)\x00", lambda m: held[int(m.group(1))], text)
        return text, n
    return rewrite


def files():
    for d in CODE_DIRS:
        for dp, dn, fn in os.walk(os.path.join(ROOT, d)):
            dn[:] = [x for x in dn if x not in ("bin", "obj", ".godot")]
            for f in sorted(fn):
                p = os.path.join(dp, f)
                if f.endswith(".cs") and os.path.relpath(p, ROOT) not in SKIP_FILES:
                    yield p
    for d in [os.path.join("data", "classic"), os.path.join("data", "design")]:
        for dp, _, fn in os.walk(os.path.join(ROOT, d)):
            for f in sorted(fn):
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, ROOT)
                if f.endswith(".csv") and rel not in (TABLE, NAMES, CUT) and not rel.startswith(RAW):
                    yield p
    for d in DOCS:
        if os.path.exists(os.path.join(ROOT, d)):
            yield os.path.join(ROOT, d)


def main():
    check = "--check" in sys.argv
    rewrite = make_rewriter(load_table())
    total = 0
    for p in files():
        with open(p, newline="", encoding="utf-8") as f:
            text = f.read()
        new, n = rewrite(text, os.path.relpath(p, ROOT).startswith(RAW_KEEP_DIRS))
        if n and new != text:
            total += n
            print(f"{os.path.relpath(p, ROOT)}: {n}")
            if not check:
                with open(p, "w", newline="", encoding="utf-8") as f:
                    f.write(new)
    print(f"{'would change' if check else 'changed'} {total} words")
    sys.exit(1 if check and total else 0)


if __name__ == "__main__":
    main()
