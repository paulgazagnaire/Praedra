#!/usr/bin/env python3
"""Copy the Lore Master's per-system text into the game's data files.

specs/creative/08-planet-presets.json (in the shared project folder, praedra/specs/creative/) is the lore. The game reads two copies of it:
  data/design/system-lore.csv        the Lore tab of a system window (text)
  data/design/system-population.csv  the harvest report's head count (popB, as whole people)
Run this whenever 08 changes, then commit both files:
  python3 tools/lore/copy-system-lore.py /mnt/project-files/praedra/specs/creative/08-planet-presets.json
"""
import csv, json, os, sys

src = sys.argv[1] if len(sys.argv) > 1 else "/mnt/project-files/praedra/specs/creative/08-planet-presets.json"
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "data", "design")
systems = sorted(json.load(open(src, encoding="utf-8"))["systems"], key=lambda v: v["name"])

with open(os.path.join(root, "system-lore.csv"), "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f, lineterminator="\n")
    w.writerow(["system", "world", "from_space", "moons", "people", "resources", "story", "history", "source"])
    for v in systems:
        t = v["text"]
        w.writerow([v["name"], t["world"], t["from_space"], t["moons"], t["population"], t["resources"], t["lore"], t.get("history") or "",
                    "lore: specs/creative/08-planet-presets.json text"])

with open(os.path.join(root, "system-population.csv"), "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f, lineterminator="\n")
    w.writerow(["system", "people", "source"])
    for v in systems:
        w.writerow([v["name"], round(v["popB"] * 1_000_000_000), "lore: specs/creative/08-planet-presets.json popB"])

print(f"copied {len(systems)} systems into system-lore.csv and system-population.csv")
