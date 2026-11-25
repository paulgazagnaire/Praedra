#!/usr/bin/env python3
"""Build out/summary.csv, out/econ-<key>.jsonl and out/econ-counts.csv from raw/."""
import csv, json, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KEYWORDS = ["economy", "economic", "resource", "micromanag", "micro-manag", "busywork",
            "busy work", "tedious", "tedium", "spreadsheet", "logistic", "supply line",
            "trade", "market", "mineral", "alloy", "strategic resource", "luxury", "manpower",
            "freighter", "convoy", "deficit", "pops", "jobs", "planet management", "automat",
            "sector", "late game", "lategame", "late-game", "snowball", "income", "upkeep",
            "production"]
PATS = {k: re.compile(re.escape(k), re.I) for k in KEYWORDS}


def pct(a, b):
    return round(100.0 * a / b, 2) if b else ""


def main():
    games = json.load(open(os.path.join(ROOT, "ids.json")))
    out = os.path.join(ROOT, "out")
    summary, counts = [], []
    for g in games:
        key = g["key"]
        path = os.path.join(ROOT, "raw", key + ".jsonl")
        st_path = os.path.join(ROOT, "state", "raw-%s.json" % key)
        st = json.load(open(st_path)) if os.path.exists(st_path) else {}
        total = pos = m_total = m_pos = 0
        kw = {k: 0 for k in KEYWORDS}
        with open(os.path.join(out, "econ-%s.jsonl" % key), "w", encoding="utf-8") as fo:
            if os.path.exists(path):
                for ln in open(path, encoding="utf-8"):
                    if not ln.strip():
                        continue
                    r = json.loads(ln)
                    total += 1
                    pos += bool(r.get("voted_up"))
                    text = r.get("review") or ""
                    hits = [k for k, p in PATS.items() if p.search(text)]
                    if hits:
                        m_total += 1
                        m_pos += bool(r.get("voted_up"))
                        for k in hits:
                            kw[k] += 1
                        r["matched_keywords"] = hits
                        fo.write(json.dumps(r, ensure_ascii=False) + "\n")
        # Steam's helpful filter only covers a recent day window now, so the
        # 500 most helpful are taken from the full crawl instead.
        if os.path.exists(path):
            rows = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
            rows.sort(key=lambda r: (float(r.get("weighted_vote_score") or 0), r.get("votes_up") or 0),
                      reverse=True)
            with open(os.path.join(ROOT, "helpful", key + ".jsonl"), "w", encoding="utf-8") as fh:
                for r in rows[:500]:
                    fh.write(json.dumps(r, ensure_ascii=False) + "\n")
        qs = st.get("query_summary", {})
        summary.append({"game": g["steam_name"], "key": key, "appid": g["appid"],
                        "total_english_reviews": total, "pct_positive": pct(pos, total),
                        "steam_reported_total": qs.get("total_reviews", ""),
                        "crawl_complete": st.get("done", False),
                        "stop_reason": st.get("stop_reason", "")})
        c = {"game": g["steam_name"], "key": key, "total_reviews": total,
             "matching_reviews": m_total, "pct_of_all": pct(m_total, total),
             "pct_positive_matches": pct(m_pos, m_total), "pct_positive_overall": pct(pos, total)}
        c.update({"kw:" + k: v for k, v in kw.items()})
        counts.append(c)
    for name, rows in (("summary.csv", summary), ("econ-counts.csv", counts)):
        with open(os.path.join(out, name), "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
            w.writeheader()
            w.writerows(rows)
    print("wrote", len(summary), "games")


if __name__ == "__main__":
    main()
