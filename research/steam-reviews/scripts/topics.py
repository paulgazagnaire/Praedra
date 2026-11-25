#!/usr/bin/env python3
"""Topic taxonomy and tagger for the Steam review corpus.

Stdlib only. Streams raw/<game>.jsonl line by line.

Usage:
  python3 scripts/topics.py tag         # write out/topic_tags.jsonl (one line per tagged review)
  python3 scripts/topics.py spotcheck   # write out/spotcheck.md: 20 random matches per subtheme

Each subtheme is (include, exclude). `exclude` phrases are blanked out of the text
before `include` is searched, so "pops up" never counts as "pops" and "trade-off"
never counts as "trade". All regexes are case-insensitive and word-bounded.
"""
import json
import os
import random
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def rx(body):
    return re.compile(r"\b(?:%s)\b" % body, re.I)


# Phrases removed before any subtheme is searched (known global false positives).
GLOBAL_EXCLUDE = rx(
    r"pops? up|popping up|production values?|production quality|trade[- ]offs?|"
    r"trading cards?|steam cards?|card drops?"
)

# topic -> subtheme -> (include, exclude or None, games-only or None)
TAXONOMY = {
    "LOGISTICS": {
        "supply": (
            r"supply lines?|supply range|supply limits?|supply system|supply depots?|"
            r"supply chains?|out of supply|attrition",
            None, None),
        "transport": (
            r"freighters?|convoys?|cargo|shipping lanes?|hauling|transport ships?|"
            r"troop transports?",
            None, None),
        "fuel_range": (
            r"fuel|refuel(?:s|ing|ed|led|ling)?|fuel range|range limits?",
            r"fuel(?:ed|led|s)? (?:by|my|the|your|his|her|their|this|you|me|us) \w+|add(?:s|ing)? fuel|"
            r"fuel (?:to|for) the fire|as fuel",
            None),
        "trade_routes": (r"trade routes?", None, None),
        "chokepoints_lanes": (
            r"choke ?points?|hyper ?lanes?|star ?lanes?|phase lanes?|warp lanes?",
            None, None),
        "logistics": (r"logistics?|logistical(?:ly)?", None, None),
    },
    "ECONOMY": {
        "resources": (
            r"resources?|minerals?|strategic resources?|luxury resources?|luxur(?:y|ies)",
            r"resource[- ](?:hog|hungry|intensive|heavy)|(?:system|pc|computer|cpu|gpu|ram) resources?|"
            r"resources? (?:on|of) (?:my|your|the) (?:pc|computer|system)|"
            r"(?:online|external|learning|community) resources?|luxury of",
            None),
        "income_money": (
            r"income|credits|gold|dust|tax(?:es|ed|ation)?",
            r"end credits|credits roll\w*|(?:collect|gather|bit)(?:s|ing|e|es)? (?:the )?dust|"
            r"bites? the dust|gold (?:edition|version)|"
            r"worth (?:its|it's|it's) weight in gold|pure gold|gold ?mine|golden|gold standard|"
            r"(?:thanks?|thx) (?:for|4) (?:the )?gold|games? with gold|(?:the )?dust (?:has |have )?settle\w*",
            None),
        "upkeep_deficit": (
            r"upkeep|maintenance|deficits?|debts?|bankrupt\w*",
            r"maintenance (?:mode|patch|update|release)s?|(?:server|scheduled) maintenance",
            None),
        "market_trade": (
            r"markets?|trading|trade|(?:market|goods?|commodity|resource|trade|buy|sell) prices",
            r"(?:on|in) the market|market(?:ing)? (?:for|of) (?:4x|strategy|games?)|"
            r"(?:dlc|game|steam|sale|full|these|current) prices|pricing (?:model|of the dlc)|marketing|"
            r"trade routes?|horse ?trading",
            None),
        "micromanagement": (
            r"micro ?-?manag\w*|tedious|tedium|chores?|busy ?work|spreadsheets?|"
            r"click(?:s|ing) a lot",
            None, None),
        "automation": (
            r"automat(?:ion|ed|e|es|ing|ions)|governors?|ai[- ]manag(?:ed|ement)|let(?:s|ting)? (?:the )?ai manage|sectors? ai|"
            r"auto ?-?manage\w*",
            None, None),
        "planet_city_mgmt": (
            r"planet(?:ary)? management|city management|colony management|districts?|"
            r"jobs|buildings|pops",
            r"(?:great|good|nice|amazing|excellent|awesome|fantastic) jobs",
            None),
    },
    "MILITARY": {
        "fleet_caps": (
            r"doom ?-?stacks?|death ?-?stacks?|stacks? of doom|fleet cap\w*|naval capacity|"
            r"naval cap|command limits?|unit caps?|unit stack\w*|stack(?:s|ing)? (?:of|your|their) "
            r"(?:units|ships|armies|army|fleets?)|1 ?upt|one unit per tile|stack\w* units",
            None, None),
        "production": (
            r"build queues?|production queues?|production|factor(?:y|ies)|shipyards?|"
            r"build times?",
            r"production (?:company|studio|team|budget)|(?:game|tv|film|movie) production|"
            r"post[- ]production|in production|(?:dlc|content) production|production line of dlc|"
            r"means of production",
            None),
        "manpower": (r"manpower|recruit\w*|conscript\w*",
                     r"recruit\w* (?:new |more |some )?(?:friends|players|people|members)", None),
        "ship_unit_design": (
            r"ship (?:design(?:er|s|ing)?|builder|editor|customi[sz]ation)|"
            r"unit design\w*|designer|loadouts?|custom ships?|design(?:ing)? (?:your|my) (?:own )?ships",
            r"(?:game|level|lead|sound|ui|narrative|original|the|its|their|these|those) designers?|"
            r"designers? (?:of|at|behind|are|have|who|that|clearly|really|seem)",
            None),
        "army_invasion": (
            r"invasions?|invad\w*|ground combat|ground battles?|troops|garrisons?|"
            r"armies|ground forces",
            None, None),
        "ai_military": (
            r"ai (?:fleets?|armies|army|navy|navies|ships|units|military)|"
            r"ai (?:doesn'?t|does not|never|won'?t|will not|can'?t|cannot|rarely) "
            r"(?:\w+ )?(?:attack|invade|declare war|fight)\w*|ai cheat\w*|"
            r"(?:enemy|the) ai (?:attacks?|invades?|declares war)",
            None, None),
    },
    "COMBAT": {
        "tactical_battles": (
            r"tactical|real[- ]?time battles?|battle ?maps?|positioning|formations?|"
            r"tactical (?:combat|battles?)",
            r"tactical (?:pause|nukes?|nuclear|bombs?|missiles?|advantage)|"
            r"(?:city|settlement|starting) positioning",
            None),
        "auto_resolve": (
            r"auto ?-?resolv\w*|auto ?-?battles?|auto ?-?combat|battle cards?|"
            r"battle tactics? cards?|cards",
            None, None),  # bare "cards" only in Endless Space games, see GAME_ONLY
        "counters_rps": (
            r"rock[- ]paper[- ]scissors?|hard[- ]counter\w*|soft[- ]counter\w*|counter[- ]?play|"
            r"weapon triangle|counter (?:system|mechanics?|units?|ships?|weapons?)|"
            r"counters? (?:to|for|against) (?:\w+ ){0,2}(?:units?|ships?|weapons?|armou?r|shields?|"
            r"every\w*)|"
            r"(?:units?|ships?|weapons?|designs?|types?) (?:that |which )?counter(?:s|ed)?|"
            r"counter(?:s|ed|ing)? (?:the |an |enemy |their |your |each other'?s? |specific |different )"
            r"(?:\w+ )?(?:units?|ships?|fleets?|weapons?|designs?|compositions?|strateg\w+|armies|"
            r"army|builds?|doctrines?)",
            r"counter[- ]?(?:intuitive|productive|part|strike|espionage|intelligence|offer)s?|"
            r"over the counter|counter-?point|turn counter|(?:a|the) counter (?:on|at|for|in) the",
            None),
        "combat_depth": (
            r"combat (?:is|was|feels|felt|seems|system is) (?:\w+ ){0,2}"
            r"(?:shallow|boring|fun|great|deep|dull|bland|simple|simplistic|lacking|"
            r"engaging|exciting|satisfying|meh|bad|terrible|awful|good|amazing|lackluster|"
            r"uninspired|basic|clunky|weak|solid)|combat systems?|combat mechanics?|"
            r"combat depth|depth (?:of|to|in) (?:the )?combat",
            None, None),
        "numbers_win": (
            r"bigger (?:fleet|army|stack|doomstack|number)s? (?:always |just |simply )?wins?|"
            r"numbers game|just numbers|(?:whoever|who) has (?:the )?(?:more|bigger|largest|biggest) "
            r"(?:\w+ )?(?:fleet|army|ships|units|stack)s?|(?:biggest|largest) (?:fleet|army|stack) wins|"
            r"(?:just|simply|only) (?:a )?(?:comparison|comparing) (?:of )?(?:numbers|fleet power|"
            r"strength)|(?:compar\w*|matter of|all about|comes down to) (?:the )?(?:fleet|military|army) "
            r"(?:power|strength)|bigger number wins?",
            None, None),
        "retreat_pursuit": (
            r"retreat\w*|disengag\w*|emergency ftl|"
            r"(?:pursu\w*|chas(?:e|es|ed|ing)) (?:down )?(?:the |an |enemy |their |your |retreating |"
            r"fleeing )?(?:\w+ )?(?:fleets?|armies|army|ships?|units?|enemy|enemies|doomstacks?)|"
            r"(?:fleets?|ships?|armies|army|units?) (?:\w+ ){0,2}(?:run(?:s|ning)? away|chas\w*)",
            r"(?:the )?(?:victory|goals?|achievements?|high scores?|win conditions?)",
            None),
    },
}

# Subthemes whose bare words are only meaningful in some games.
GAME_ONLY = {
    # ES2 (and ES1) resolve battles with battle-tactic cards; elsewhere "cards" is noise.
    ("COMBAT", "auto_resolve"): (r"cards", {"endless-space", "endless-space-2"}),
}

LATE_GAME = rx(r"late[- ]?game|lategame|end[- ]?game|mid[- ]?game|midgame")

COMPILED = {}
for _t, _subs in TAXONOMY.items():
    for _s, (_inc, _exc, _) in _subs.items():
        COMPILED[(_t, _s)] = (rx(_inc), rx(_exc) if _exc else None)
GAME_ONLY_RX = {k: (rx(v[0]), v[1]) for k, v in GAME_ONLY.items()}


def _blank(m):
    return " " * len(m.group(0))


def clean(text):
    """Blank out global false positives, keeping character offsets aligned with the original."""
    return GLOBAL_EXCLUDE.sub(_blank, text)


def find(topic, sub, text, game):
    """Return the first include match object for (topic, sub) in pre-cleaned text, or None."""
    inc, exc = COMPILED[(topic, sub)]
    if exc is not None:
        text = exc.sub(_blank, text)
    for m in inc.finditer(text):
        go = GAME_ONLY_RX.get((topic, sub))
        if go and go[0].fullmatch(m.group(0)) and game not in go[1]:
            continue
        return m, text
    return None, text


def tag(text, game):
    """Return ({topic: [subthemes]}, late_game_bool) for one review."""
    t = clean(text or "")
    out = {}
    for (topic, sub) in COMPILED:
        m, _ = find(topic, sub, t, game)
        if m:
            out.setdefault(topic, []).append(sub)
    return out, bool(LATE_GAME.search(t))


def games():
    return json.load(open(os.path.join(ROOT, "ids.json")))


def iter_reviews():
    """Yield (game_key, review dict) streaming every raw/<game>.jsonl line by line."""
    for g in games():
        path = os.path.join(ROOT, "raw", g["key"] + ".jsonl")
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as fh:
            for ln in fh:
                if ln.strip():
                    yield g["key"], json.loads(ln)


def cmd_tag():
    out = os.path.join(ROOT, "out", "topic_tags.jsonl")
    n = 0
    with open(out, "w", encoding="utf-8") as fo:
        for game, r in iter_reviews():
            tags, late = tag(r.get("review"), game)
            fo.write(json.dumps({"id": r["recommendationid"], "game": game, "tags": tags,
                                 "late": late}) + "\n")
            n += 1
    print("tagged", n, "->", out)


def cmd_spotcheck(k=20, seed=7):
    """Reservoir-sample k matches per subtheme and print the matched context."""
    rnd = random.Random(seed)
    res = {key: [] for key in COMPILED}
    seen = {key: 0 for key in COMPILED}
    for game, r in iter_reviews():
        t = clean(r.get("review") or "")
        for key in COMPILED:
            m, tt = find(key[0], key[1], t, game)
            if not m:
                continue
            seen[key] += 1
            a, b = max(0, m.start() - 70), min(len(tt), m.end() + 70)
            ctx = (game, tt[a:m.start()] + "[[" + m.group(0) + "]]" + tt[m.end():b])
            ctx = (ctx[0], ctx[1].replace("\n", " "))
            if len(res[key]) < k:
                res[key].append(ctx)
            else:
                j = rnd.randrange(seen[key])
                if j < k:
                    res[key][j] = ctx
    path = os.path.join(ROOT, "out", "spotcheck.md")
    with open(path, "w", encoding="utf-8") as fo:
        for key, items in res.items():
            fo.write("\n## %s / %s  (n=%d)\n" % (key[0], key[1], seen[key]))
            for g, c in items:
                fo.write("- %s: %s\n" % (g, c))
    print("wrote", path)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "tag"
    {"tag": cmd_tag, "spotcheck": cmd_spotcheck}[cmd]()
