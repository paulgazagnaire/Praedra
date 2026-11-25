#!/usr/bin/env python3
"""Resolve and confirm Steam app ids; writes ids.json."""
import json, time, urllib.parse, urllib.request, os

UA = {"User-Agent": "steam-reviews-research/1.0"}
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

GAMES = [
    ("Stellaris", 281990, None),
    ("Endless Space 2", 392110, None),
    ("Endless Space", 208140, None),
    ("Sins of a Solar Empire", 204880, None),
    ("Sins of a Solar Empire II", 1575940, None),
    ("Galactic Civilizations III", 226860, None),
    ("Galactic Civilizations IV", None, "Galactic Civilizations IV"),
    ("Distant Worlds: Universe", 261470, None),
    ("Distant Worlds 2", 1531540, None),
    ("Hearts of Iron IV", 394360, None),
    ("Europa Universalis IV", 236850, None),
    ("Victoria 3", 529340, None),
    ("Star Ruler 2", 282590, None),
    ("Civilization V", 8930, None),
    ("Civilization VI", 289070, None),
    ("Supreme Commander: Forged Alliance", 9420, None),
    ("Master of Orion 1+2 classic", None, "Master of Orion 1"),
    ("Master of Orion: Conquer the Stars", None, "Master of Orion"),
]


def get(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=30) as r:
                return json.loads(r.read().decode("utf-8"))
        except Exception as e:
            print("  retry", attempt, e)
            time.sleep(2 * (attempt + 1))
    raise RuntimeError(url)


def details(appid):
    d = get(f"https://store.steampowered.com/api/appdetails?appids={appid}&filters=basic")
    e = d.get(str(appid), {})
    return e.get("data") if e.get("success") else None


def search(term):
    q = urllib.parse.quote(term)
    d = get(f"https://store.steampowered.com/api/storesearch/?term={q}&cc=us&l=english")
    return [(i["id"], i["name"]) for i in d.get("items", [])]


out = []
for label, appid, term in GAMES:
    cands = []
    if term:
        cands = search(term)
        print(label, "search ->", cands)
        time.sleep(1.5)
    out.append({"game": label, "appid": appid, "search_term": term, "search_results": cands})

json.dump(out, open(os.path.join(ROOT, "state", "search.json"), "w"), indent=1)

for g in out:
    if g["appid"]:
        d = details(g["appid"])
        g["steam_name"] = d.get("name") if d else None
        g["type"] = d.get("type") if d else None
        print(g["game"], g["appid"], "->", g["steam_name"])
        time.sleep(1.5)
json.dump(out, open(os.path.join(ROOT, "state", "search.json"), "w"), indent=1)
