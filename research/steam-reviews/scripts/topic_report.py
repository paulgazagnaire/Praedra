#!/usr/bin/env python3
"""Build analysis/ from raw/ using the taxonomy in scripts/topics.py.

Stdlib only; one streaming pass over raw/<game>.jsonl.

Outputs (analysis/):
  topic_stats.csv       game x topic and game x subtheme rows
  topic_pooled.csv      same, pooled with every game weighted equally
  phrases_<TOPIC>.csv   bigrams/trigrams separating negative from positive reviews
                        (log-odds ratio, informative Dirichlet prior; Monroe et al. 2008)
  quotes_<TOPIC>.md     most-upvoted negative/positive snippets per subtheme
  README.md             method, keyword lists, caveats
"""
import csv
import heapq
import json
import math
import os
import re
import statistics
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import topics as T  # noqa: E402

ROOT = T.ROOT
OUT = os.path.join(ROOT, "analysis")
MIN_GAME_N = 30      # a game enters pooled rate averages only with >= this many matches
MIN_NGRAM = 30       # minimum review count for a phrase
TOP_PHRASES = 60
QUOTES_PER_SIDE = 6
QUOTE_POOL = 40      # keep this many candidates per side so duplicates can be skipped

STOPWORDS = set("""
a about above after again against all am an and any are aren as at be because been before being
below between both but by can could couldn did do does doing down during each few for from further
had has have having he her here hers herself him himself his how i if in into is it its itself just
let me more most my myself of off on once only or other ought our ours ourselves out over own same
she should so some such than that the their theirs them themselves then there these they this those
through to too under until up very was we were what when where which while who whom why with would
you your yours yourself yourselves im ive id ill youre youve youll hes shes theyre theyve weve its
also get got gets really even much many one ones still well thing things way lot lots s t d ll re ve
m will may might must shall yes yeah ok oh etc like
""".split())
# Negations and contrast words are deliberately NOT stopwords: "not fun" must stay "not fun".
for _w in ("not", "no", "never", "nothing", "dont", "doesnt", "didnt", "cant", "cannot", "wont",
           "isnt", "wasnt", "arent", "werent", "nor", "without", "but"):
    STOPWORDS.discard(_w)

BBCODE = re.compile(r"\[/?[a-z0-9*=_ ]+\]", re.I)
TOKEN = re.compile(r"[a-z]+(?:'[a-z]+)?")
SENT_SPLIT = re.compile(r"(?<=[.!?])\s+|\n+")


def ngrams_of(text):
    """Set of bigrams and trigrams (stopwords removed, within sentence boundaries)."""
    grams = set()
    for sent in re.split(r"[.!?;:\n()\[\]\"]+", BBCODE.sub(" ", text.lower())):
        toks = [t.replace("'", "") for t in TOKEN.findall(sent)]
        toks = [t for t in toks if t not in STOPWORDS and len(t) > 1]
        for i in range(len(toks) - 1):
            grams.add(toks[i] + " " + toks[i + 1])
            if i + 2 < len(toks):
                grams.add(toks[i] + " " + toks[i + 1] + " " + toks[i + 2])
    return grams


def snippet(text, start, end, limit=300):
    """1-2 sentences around [start, end) of the original text, max `limit` chars."""
    spans, pos = [], 0
    for m in SENT_SPLIT.finditer(text):
        spans.append((pos, m.start()))
        pos = m.end()
    spans.append((pos, len(text)))
    idx = next((i for i, (a, b) in enumerate(spans) if a <= start < b or a <= start <= b), 0)
    a, b = spans[idx]
    s = text[a:b].strip()
    if len(s) < 120 and idx + 1 < len(spans):
        nxt = text[spans[idx + 1][0]:spans[idx + 1][1]].strip()
        if len(s) + 1 + len(nxt) <= limit:
            s = s + " " + nxt
    s = BBCODE.sub("", s)
    s = re.sub(r"\s+", " ", s).strip()
    if len(s) > limit:
        # keep the keyword inside the window
        kw = BBCODE.sub("", text[start:end])
        k = s.lower().find(kw.lower())
        lo = max(0, min(k - limit // 3, len(s) - limit)) if k >= 0 else 0
        s = ("…" if lo > 0 else "") + s[lo:lo + limit - 2].strip() + "…"
    return s


WORD = re.compile(r"\w+")
LEN_BUCKETS = (20, 50, 150, 400)  # word-count bucket edges for the length control


def len_bucket(text):
    n = len(WORD.findall(text))
    return sum(n >= e for e in LEN_BUCKETS)


def pct(a, b):
    return round(100.0 * a / b, 2) if b else ""


def med_h(xs):
    return round(statistics.median(xs) / 60.0, 1) if xs else ""


# Phrases made only of verdict words say nothing about the topic; the *_TOPICAL lists drop any
# n-gram containing one of these so mechanic-specific phrases surface.
GENERIC = set("""
recommend recommended recommending recommendation worth buy bought buying purchase purchased waste
wasted money refund refunded fun great good bad amazing awesome best worst love loved hate hated
review reviews reviewer game games play played playing player players hours hour time times price
priced sale dlc dlcs fantastic excellent terrible awful horrible enjoy enjoyed enjoyable like liked
pretty nice cool definitely highly absolutely overall totally honestly seriously
strategy series sid meiers civilization civ hearts iron iv stellaris paradox endless sins solar
empire europa universalis victoria humankind dune galactic civilizations distant worlds master orion
supreme commander forged alliance brave new kings gods
""".split())


def log_odds(neg, pos, min_count):
    """Monroe et al. (2008) log-odds with informative Dirichlet prior.

    Prior alpha_w = combined (neg+pos) count of w within the topic, so alpha_0 = n_neg + n_pos.
    Returns list of (gram, z, delta, y_neg, y_pos), z > 0 means more NEGATIVE.
    """
    n_i, n_j = sum(neg.values()), sum(pos.values())
    a0 = n_i + n_j
    rows = []
    for w in set(neg) | set(pos):
        yi, yj = neg.get(w, 0), pos.get(w, 0)
        if yi + yj < min_count:
            continue
        aw = yi + yj
        d = (math.log((yi + aw) / (n_i + a0 - yi - aw)) -
             math.log((yj + aw) / (n_j + a0 - yj - aw)))
        var = 1.0 / (yi + aw) + 1.0 / (yj + aw)
        rows.append((w, d / math.sqrt(var), d, yi, yj))
    return rows


def main():
    os.makedirs(OUT, exist_ok=True)
    games = [g["key"] for g in T.games()]
    keys = []  # (topic, subtheme) with subtheme "ALL" for topic rows
    for t, subs in T.TAXONOMY.items():
        keys.append((t, "ALL"))
        keys += [(t, s) for s in subs]
    keys.append(("LATE_GAME", "ALL"))

    g_n, g_pos, g_pt, g_late = Counter(), Counter(), defaultdict(list), Counter()
    m_n, m_pos, m_late = Counter(), Counter(), Counter()
    m_pt = defaultdict(list)
    ng_neg = {t: Counter() for t in T.TAXONOMY}
    ng_pos = {t: Counter() for t in T.TAXONOMY}
    tdocs = Counter()
    quotes = defaultdict(list)  # (topic, sub, voted_up) -> min-heap of (votes, id, payload)

    gb_n, gb_pos = Counter(), Counter()  # (game, length bucket)
    m_b = defaultdict(Counter)            # (game, topic, sub) -> length bucket counts
    done = 0
    for game, r in T.iter_reviews():
        text = r.get("review") or ""
        up = bool(r.get("voted_up"))
        pt = int(r.get("playtime_forever") or 0)
        g_n[game] += 1
        g_pos[game] += up
        g_pt[game].append(pt)
        lb = len_bucket(text)
        gb_n[(game, lb)] += 1
        gb_pos[(game, lb)] += up
        ct = T.clean(text)
        late = bool(T.LATE_GAME.search(ct))
        g_late[game] += late
        hit_topics = set()
        for (topic, sub) in T.COMPILED:
            m, _ = T.find(topic, sub, ct, game)
            if not m:
                continue
            hit_topics.add(topic)
            k = (game, topic, sub)
            m_n[k] += 1
            m_pos[k] += up
            m_late[k] += late
            m_pt[k].append(pt)
            m_b[k][lb] += 1
            votes = int(r.get("votes_up") or 0)
            if votes > 0:
                h = quotes[(topic, sub, up)]
                item = (votes, r["recommendationid"], (game, pt, up, text, m.start(), m.end()))
                if len(h) < QUOTE_POOL:
                    heapq.heappush(h, item)
                elif votes > h[0][0]:
                    heapq.heapreplace(h, item)
        if late:
            hit_topics_late = ("LATE_GAME", "ALL")
            k = (game,) + hit_topics_late
            m_n[k] += 1
            m_pos[k] += up
            m_late[k] += 1
            m_pt[k].append(pt)
            m_b[k][lb] += 1
        if hit_topics:
            grams = ngrams_of(text)
            for topic in hit_topics:
                k = (game, topic, "ALL")
                m_n[k] += 1
                m_pos[k] += up
                m_late[k] += late
                m_pt[k].append(pt)
                m_b[k][lb] += 1
                tdocs[topic] += 1
                (ng_pos if up else ng_neg)[topic].update(grams)
        done += 1
        if done % 100000 == 0:
            print("…", done, file=sys.stderr)

    def expected(k):
        """Recommend % expected for matches of k given only their game and review length."""
        n = m_n[k]
        if not n:
            return None
        g = k[0]
        return 100.0 * sum(c * gb_pos[(g, b)] / gb_n[(g, b)] for b, c in m_b[k].items()) / n

    # ---- topic_stats.csv
    cols = ["game", "topic", "subtheme", "n", "pct_of_game_reviews", "pct_recommend_matches",
            "pct_recommend_game", "lift_pts", "pct_recommend_expected_len", "lift_len_adj_pts",
            "median_playtime_h_matches",
            "median_playtime_h_game", "pct_matches_late_game", "game_reviews"]
    stats_rows = []
    gmed = {g: statistics.median(g_pt[g]) / 60.0 if g_pt[g] else None for g in games}
    for g in games:
        for (t, s) in keys:
            k = (g, t, s)
            n = m_n[k]
            rec_m = pct(m_pos[k], n)
            rec_g = pct(g_pos[g], g_n[g])
            stats_rows.append({
                "game": g, "topic": t, "subtheme": s, "n": n,
                "pct_of_game_reviews": pct(n, g_n[g]),
                "pct_recommend_matches": rec_m, "pct_recommend_game": rec_g,
                "lift_pts": round(rec_m - rec_g, 2) if n else "",
                "pct_recommend_expected_len": round(expected(k), 2) if n else "",
                "lift_len_adj_pts": round(rec_m - expected(k), 2) if n else "",
                "median_playtime_h_matches": med_h(m_pt[k]),
                "median_playtime_h_game": round(gmed[g], 1) if gmed[g] is not None else "",
                "pct_matches_late_game": pct(m_late[k], n),
                "game_reviews": g_n[g],
            })
    with open(os.path.join(OUT, "topic_stats.csv"), "w", newline="") as fo:
        w = csv.DictWriter(fo, fieldnames=cols)
        w.writeheader()
        w.writerows(stats_rows)

    # ---- topic_pooled.csv (each game weighted equally)
    pcols = ["topic", "subtheme", "n_total", "n_games_with_matches", "n_games_rated",
             "pct_of_reviews", "pct_recommend_matches", "pct_recommend_overall", "lift_pts",
             "pct_recommend_expected_len", "lift_len_adj_pts",
             "median_playtime_h_matches", "median_playtime_h_overall", "pct_matches_late_game",
             "pct_recommend_matches_unweighted"]
    pooled = []
    for (t, s) in keys:
        per = [(g, m_n[(g, t, s)]) for g in games]
        rated = [g for g, n in per if n >= MIN_GAME_N]
        share = statistics.mean(100.0 * n / g_n[g] for g, n in per if g_n[g])
        row = {"topic": t, "subtheme": s, "n_total": sum(n for _, n in per),
               "n_games_with_matches": sum(1 for _, n in per if n),
               "n_games_rated": len(rated), "pct_of_reviews": round(share, 2)}
        tot_n = sum(n for _, n in per)
        row["pct_recommend_matches_unweighted"] = pct(sum(m_pos[(g, t, s)] for g in games), tot_n)
        if rated:
            rm = statistics.mean(100.0 * m_pos[(g, t, s)] / m_n[(g, t, s)] for g in rated)
            rg = statistics.mean(100.0 * g_pos[g] / g_n[g] for g in rated)
            ex = statistics.mean(expected((g, t, s)) for g in rated)
            row.update({
                "pct_recommend_matches": round(rm, 2), "pct_recommend_overall": round(rg, 2),
                "lift_pts": round(rm - rg, 2),
                "pct_recommend_expected_len": round(ex, 2),
                "lift_len_adj_pts": round(rm - ex, 2),
                "median_playtime_h_matches": round(statistics.median(
                    statistics.median(m_pt[(g, t, s)]) / 60.0 for g in rated), 1),
                "median_playtime_h_overall": round(statistics.median(gmed[g] for g in rated), 1),
                "pct_matches_late_game": round(statistics.mean(
                    100.0 * m_late[(g, t, s)] / m_n[(g, t, s)] for g in rated), 2),
            })
        pooled.append(row)
    with open(os.path.join(OUT, "topic_pooled.csv"), "w", newline="") as fo:
        w = csv.DictWriter(fo, fieldnames=pcols)
        w.writeheader()
        w.writerows(pooled)

    # ---- phrases_<TOPIC>.csv
    for t in T.TAXONOMY:
        rows = log_odds(ng_neg[t], ng_pos[t], MIN_NGRAM)
        n_neg_docs = sum(m_n[(g, t, "ALL")] - m_pos[(g, t, "ALL")] for g in games)
        n_pos_docs = sum(m_pos[(g, t, "ALL")] for g in games)
        neg = sorted(rows, key=lambda x: -x[1])[:TOP_PHRASES]
        pos = sorted(rows, key=lambda x: x[1])[:TOP_PHRASES]
        with open(os.path.join(OUT, "phrases_%s.csv" % t), "w", newline="") as fo:
            w = csv.writer(fo)
            w.writerow(["direction", "rank", "ngram", "z", "log_odds_delta", "n_neg_reviews",
                        "n_pos_reviews", "pct_of_neg_reviews", "pct_of_pos_reviews"])
            topical = [x for x in rows if not (set(x[0].split()) & GENERIC)]
            tneg = sorted(topical, key=lambda x: -x[1])[:TOP_PHRASES]
            tpos = sorted(topical, key=lambda x: x[1])[:TOP_PHRASES]
            for label, lst in (("NEGATIVE", neg), ("POSITIVE", pos),
                               ("NEGATIVE_TOPICAL", tneg), ("POSITIVE_TOPICAL", tpos)):
                for i, (gram, z, d, yi, yj) in enumerate(lst, 1):
                    w.writerow([label, i, gram, round(z, 2), round(d, 3), yi, yj,
                                pct(yi, n_neg_docs), pct(yj, n_pos_docs)])

    # ---- quotes_<TOPIC>.md
    for t, subs in T.TAXONOMY.items():
        lines = ["# %s: most-upvoted review snippets\n" % t,
                 "Per subtheme: the %d most-upvoted negative and positive reviews that match it. "
                 "Snippet = the 1-2 sentences holding the first keyword match (max 300 chars). "
                 "Playtime = total hours on record when crawled, not at review time. "
                 "No usernames are kept.\n" % QUOTES_PER_SIDE]
        for s in subs:
            lines.append("\n## %s\n" % s)
            lines.append("Keywords: `%s`\n" % T.TAXONOMY[t][s][0])
            for up, label in ((False, "Negative (not recommended)"), (True, "Positive (recommended)")):
                lines.append("\n### %s\n" % label)
                cands = sorted(quotes[(t, s, up)], key=lambda x: (-x[0], x[1]))
                seen, k = set(), 0
                for votes, rid, (game, pt, upv, text, a, b) in cands:
                    sn = snippet(text, a, b)
                    key = sn.lower()[:120]
                    if key in seen or not sn:
                        continue
                    seen.add(key)
                    k += 1
                    lines.append("%d. **%s** · %d helpful votes · %.0f h played · recommends: %s\n"
                                 "   > %s\n" % (k, game, votes, pt / 60.0, "yes" if upv else "no",
                                               sn.replace("\n", " ")))
                    if k >= QUOTES_PER_SIDE:
                        break
                if k == 0:
                    lines.append("_No upvoted matches._\n")
        with open(os.path.join(OUT, "quotes_%s.md" % t), "w", encoding="utf-8") as fo:
            fo.write("\n".join(lines))

    with open(os.path.join(OUT, "_run.json"), "w") as fo:
        json.dump({"reviews": sum(g_n.values()), "games": len(games),
                   "topic_reviews": dict(tdocs)}, fo, indent=1)
    print("done", sum(g_n.values()), dict(tdocs))


if __name__ == "__main__":
    main()
