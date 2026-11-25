#!/usr/bin/env python3
"""Crawl Steam English reviews for every game in ids.json.

Phase 1: 500 most helpful reviews (filter=all, 5 pages) -> helpful/<key>.jsonl
Phase 2: all English reviews (filter=recent, cursor walk) -> raw/<key>.jsonl
Resumable: per-game state in state/<phase>-<key>.json. Standard library only.
"""
import json, os, sys, time, urllib.parse, urllib.request, urllib.error

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UA = {"User-Agent": "steam-reviews-research/1.0"}
SLEEP = 1.5
BASE = ("https://store.steampowered.com/ajaxappreviews/{appid}?json=1&language=english"
        "&filter={filt}&purchase_type=all&review_type=all&num_per_page=100&cursor={cursor}")


def log(*a):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), *a, flush=True)


def fetch(url):
    delay = 5
    for attempt in range(10):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=60) as r:
                d = json.loads(r.read().decode("utf-8"))
            if d.get("success") != 1:
                raise RuntimeError("success=%r" % d.get("success"))
            return d
        except urllib.error.HTTPError as e:
            wait = 60 if e.code == 429 else delay
            log("  HTTP", e.code, "attempt", attempt, "sleep", wait)
        except Exception as e:
            wait = delay
            log("  error", repr(e)[:200], "attempt", attempt, "sleep", wait)
        time.sleep(wait)
        delay = min(delay * 2, 300)
    raise RuntimeError("giving up on " + url)


def row(r):
    a = r.get("author", {})
    return {
        "recommendationid": r.get("recommendationid"),
        "voted_up": r.get("voted_up"),
        "votes_up": r.get("votes_up"),
        "weighted_vote_score": r.get("weighted_vote_score"),
        "playtime_forever": a.get("playtime_forever"),
        "timestamp_created": r.get("timestamp_created"),
        "review": r.get("review"),
    }


def load_state(path):
    if os.path.exists(path):
        return json.load(open(path))
    return {"cursor": "*", "done": False, "pages": 0, "count": 0, "seen_cursors": []}


def save_state(path, st):
    tmp = path + ".tmp"
    json.dump(st, open(tmp, "w"))
    os.replace(tmp, path)


def crawl(game, phase):
    key, appid = game["key"], game["appid"]
    outdir = "helpful" if phase == "helpful" else "raw"
    out_path = os.path.join(ROOT, outdir, key + ".jsonl")
    st_path = os.path.join(ROOT, "state", "%s-%s.json" % (phase, key))
    st = load_state(st_path)
    if st["done"]:
        log(phase, key, "already done,", st["count"], "reviews")
        return
    seen = set()
    if os.path.exists(out_path):
        # Drop a possibly half-written last line, then reload ids for dedupe.
        lines = open(out_path, encoding="utf-8").read().split("\n")
        good = []
        for ln in lines:
            if not ln.strip():
                continue
            try:
                seen.add(json.loads(ln)["recommendationid"])
                good.append(ln)
            except Exception:
                pass
        with open(out_path, "w", encoding="utf-8") as f:
            f.write("".join(l + "\n" for l in good))
    seen_cursors = set(st["seen_cursors"])
    filt = "all" if phase == "helpful" else "recent"
    max_pages = 5 if phase == "helpful" else None
    empty_streak = 0
    log(phase, key, appid, "start/resume at page", st["pages"], "have", len(seen))
    while True:
        if max_pages is not None and st["pages"] >= max_pages:
            st["stop_reason"] = "page limit"
            break
        cursor = st["cursor"]
        d = fetch(BASE.format(appid=appid, filt=filt, cursor=urllib.parse.quote(cursor, safe="")))
        if st["pages"] == 0 and "total_reviews" in d.get("query_summary", {}):
            st["query_summary"] = d["query_summary"]
        revs = d.get("reviews", [])
        new = [r for r in revs if r.get("recommendationid") not in seen]
        with open(out_path, "a", encoding="utf-8") as f:
            for r in new:
                seen.add(r["recommendationid"])
                f.write(json.dumps(row(r), ensure_ascii=False) + "\n")
        st["pages"] += 1
        st["count"] = len(seen)
        nxt = d.get("cursor")
        seen_cursors.add(cursor)
        st["seen_cursors"] = list(seen_cursors)[-50:] if phase == "helpful" else list(seen_cursors)
        if st["pages"] % 50 == 0:
            log(phase, key, "page", st["pages"], "total", len(seen))
        if not revs:
            st["stop_reason"] = "empty page"
            save_state(st_path, st)
            break
        empty_streak = 0 if new else empty_streak + 1
        if not nxt or nxt in seen_cursors:
            st["stop_reason"] = "cursor repeat" if nxt else "no cursor"
            save_state(st_path, st)
            break
        if empty_streak >= 3:
            st["stop_reason"] = "3 pages with no new reviews"
            save_state(st_path, st)
            break
        st["cursor"] = nxt
        save_state(st_path, st)
        time.sleep(SLEEP)
    st["done"] = True
    st["finished_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
    save_state(st_path, st)
    log(phase, key, "DONE", len(seen), "reviews,", st.get("stop_reason"))
    time.sleep(SLEEP)


def main():
    games = json.load(open(os.path.join(ROOT, "ids.json")))
    phases = sys.argv[1:] or ["helpful", "raw"]
    for phase in phases:
        for g in games:
            try:
                crawl(g, phase)
            except Exception as e:
                log("FAILED", phase, g["key"], repr(e))
    log("ALL PHASES FINISHED")


if __name__ == "__main__":
    main()
