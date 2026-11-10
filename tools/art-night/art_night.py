#!/usr/bin/env python3
"""Night art batch: read the prompts in the Praedra Lore Airtable base, make the
missing reference sheets and portraits with Google's Gemini image model, save
them to the shared folder with a dithered copy, and attach them in Airtable.

It only fills EMPTY "Reference sheet" and "Portrait" cells. It never replaces a
picture that is already there.

    python3 tools/art-night/art_night.py --plan            # what it would make, and the cost
    python3 tools/art-night/art_night.py --run --limit 2   # a small test
    python3 tools/art-night/art_night.py --run             # the whole night

Needs GEMINI_API_KEY and AIRTABLE_TOKEN in the environment. See README.md.
Standard library + Pillow + numpy only.
"""
import argparse, base64, datetime, io, json, os, re, sys, time, urllib.error, urllib.parse, urllib.request

import numpy as np
from PIL import Image

BASE = "appfoPpVnwpVT6kPV"  # Airtable "Praedra Lore"
GEMINI = "https://generativelanguage.googleapis.com/v1beta"
MODEL = os.environ.get("ART_MODEL", "gemini-3-pro-image-preview")  # "Nano Banana Pro"
# Google's list price for this model, 1K or 2K picture, checked 2026-11-10. Used for the budget guard only;
# the real hard limit is the monthly spend cap set in Google AI Studio.
PRICE_PER_IMAGE = float(os.environ.get("ART_PRICE", "0.14"))
OUT_ROOT = "/mnt/project-files/praedra/art/generated"
UNIT_KINDS = {"Troop", "Special force"}  # ships and fighters get their look from the Shipyard and Blender
# Phrases Paul has banned (Praevisio design rules 2026-11-04, no fascist symbols 2026-11-03).
# A prompt that contains one is skipped and reported, never sent.
BANNED = ["moulded panels", "molded panels", "blue lens", "fasces", "washing machine"]
MAX_ATTACH_BYTES = 5_000_000  # Airtable's direct upload limit


# ---------- small HTTP helpers ----------
def http(method, url, headers=None, body=None, timeout=300):
    data = json.dumps(body).encode() if body is not None else None
    h = {"Content-Type": "application/json", **(headers or {})}
    req = urllib.request.Request(url, data=data, headers=h, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def with_retries(fn, what, tries=5):
    for n in range(tries):
        try:
            return fn()
        except urllib.error.HTTPError as e:
            msg = e.read().decode(errors="replace")[:500]
            if e.code in (429, 500, 502, 503, 504) and n < tries - 1:
                wait = 15 * 2 ** n
                log(f"  {what}: HTTP {e.code}, retrying in {wait}s")
                time.sleep(wait)
                continue
            raise RuntimeError(f"{what}: HTTP {e.code}: {msg}") from None
        except (urllib.error.URLError, TimeoutError) as e:
            if n < tries - 1:
                time.sleep(15 * 2 ** n)
                continue
            raise RuntimeError(f"{what}: {e}") from None


LOG = []
def log(s):
    print(s, flush=True)
    LOG.append(s)


# ---------- Airtable ----------
def at_headers():
    return {"Authorization": "Bearer " + os.environ["AIRTABLE_TOKEN"]}


def at_records(table):
    recs, offset = [], None
    while True:
        q = {"pageSize": "100", **({"offset": offset} if offset else {})}
        d = with_retries(lambda: http("GET", f"https://api.airtable.com/v0/{BASE}/{urllib.parse.quote(table)}?" + urllib.parse.urlencode(q), at_headers()), "airtable read")
        recs += d["records"]
        offset = d.get("offset")
        if not offset:
            return recs


def at_record(table, rid):
    return with_retries(lambda: http("GET", f"https://api.airtable.com/v0/{BASE}/{urllib.parse.quote(table)}/{rid}", at_headers()), "airtable read")


def at_attach(rid, field, png_bytes, filename):
    """Upload straight into an attachment cell (Airtable's uploadAttachment endpoint, max 5 MB)."""
    if len(png_bytes) > MAX_ATTACH_BYTES:
        im = Image.open(io.BytesIO(png_bytes)).convert("RGB")
        buf = io.BytesIO(); im.save(buf, "JPEG", quality=92); png_bytes = buf.getvalue()
        filename = filename.rsplit(".", 1)[0] + ".jpg"
    ctype = "image/jpeg" if filename.endswith(".jpg") else "image/png"
    body = {"contentType": ctype, "file": base64.b64encode(png_bytes).decode(), "filename": filename}
    url = f"https://content.airtable.com/v0/{BASE}/{rid}/{urllib.parse.quote(field)}/uploadAttachment"
    with_retries(lambda: http("POST", url, at_headers(), body), "airtable upload")


def download(url):
    with urllib.request.urlopen(url, timeout=120) as r:
        return r.read()


# ---------- Gemini ----------
def gm_headers():
    return {"x-goog-api-key": os.environ["GEMINI_API_KEY"]}


def check_model():
    d = http("GET", f"{GEMINI}/models?pageSize=1000", gm_headers())
    names = [m["name"].split("/", 1)[1] for m in d.get("models", [])]
    if MODEL not in names:
        imgs = [n for n in names if "image" in n]
        sys.exit(f"Model {MODEL} is not offered to this key. Image models it offers: {imgs}. Set ART_MODEL to one of them.")


def generate(prompt, refs, aspect):
    """One picture. refs: list of image bytes sent with the prompt for consistency."""
    parts = [{"text": prompt}]
    for b in refs:
        parts.append({"inlineData": {"mimeType": "image/jpeg", "data": base64.b64encode(shrink(b)).decode()}})
    body = {"contents": [{"role": "user", "parts": parts}],
            "generationConfig": {"responseModalities": ["TEXT", "IMAGE"], "imageConfig": {"aspectRatio": aspect, "imageSize": "2K"}}}
    d = with_retries(lambda: http("POST", f"{GEMINI}/models/{MODEL}:generateContent", gm_headers(), body, timeout=600), "gemini")
    for c in d.get("candidates", []):
        for p in c.get("content", {}).get("parts", []):
            blob = p.get("inlineData") or p.get("inline_data")
            if blob:
                return base64.b64decode(blob["data"])
    reason = [c.get("finishReason") for c in d.get("candidates", [])] or d.get("promptFeedback")
    raise RuntimeError(f"no picture came back ({reason})")


def shrink(b, side=1536):
    im = Image.open(io.BytesIO(b)).convert("RGB")
    im.thumbnail((side, side))
    buf = io.BytesIO(); im.save(buf, "JPEG", quality=90)
    return buf.getvalue()


# ---------- dither: Portrait Maker's default look (tools/portrait-maker: Atkinson, Cold palette,
# 320 dots wide, dot 2, contrast 1.2). Change Portrait Maker's DEF and change this too. ----------
COLD = [(0x04, 0x06, 0x0c), (0x1b, 0x33, 0x50), (0x6e, 0x9b, 0xc0), (0xe6, 0xef, 0xf6)]

def dither(png_bytes, dots=320, dot=2, contrast=1.2):
    im = Image.open(io.BytesIO(png_bytes)).convert("RGB")
    h = round(im.height * dots / im.width)
    a = np.asarray(im.resize((dots, h), Image.LANCZOS), dtype=np.float32) / 255
    a = np.clip((a - 0.5) * contrast + 0.5, 0, 1)
    n = len(COLD)
    L = (0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]) * (n - 1)
    out = np.zeros((h, dots), dtype=np.uint8)
    for y in range(h):
        for x in range(dots):
            v = L[y, x]
            k = min(n - 1, max(0, int(np.floor(v)) + (1 if v - np.floor(v) > 0.5 else 0)))
            out[y, x] = k
            e = (v - k) / 8
            for dx, dy in ((1, 0), (2, 0), (-1, 1), (0, 1), (1, 1), (0, 2)):
                xx, yy = x + dx, y + dy
                if 0 <= xx < dots and yy < h:
                    L[yy, xx] += e
    pal = np.array(COLD, dtype=np.uint8)
    img = Image.fromarray(pal[out]).resize((dots * dot, h * dot), Image.NEAREST)
    buf = io.BytesIO(); img.save(buf, "PNG")
    return buf.getvalue()


# ---------- the plan ----------
def slug(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")[:60]


def banned_in(text):
    t = (text or "").lower()
    return [b for b in BANNED if b in t]


def build_plan():
    terms = {r["fields"].get("Name", "").strip(): r["fields"].get("Notes", "") for r in at_records("Terms")}

    def template_body(name):
        # the Terms note explains how to use the template; the prompt starts at "Create a single"
        note = terms.get(name, "")
        i = note.find("Create a single")
        return note[i:] if i >= 0 else None

    sheet_tpl = template_body("Reference sheet template")
    style = terms.get("Portrait style", "")
    m = re.search(r'"([^"]+)"', style)
    style = m.group(1) if m else None
    if not sheet_tpl or not style:
        sys.exit("Could not read 'Reference sheet template' or 'Portrait style' from the Terms table.")
    special = {"The Chair": "Reference sheet template: the Chair", "Speculators": "Reference sheet template: Speculators"}

    jobs, skipped = [], []
    for table in ("Characters", "Units"):
        for r in sorted(at_records(table), key=lambda r: r["fields"].get("Name", "")):
            f, name = r["fields"], r["fields"].get("Name", "")
            if table == "Units" and f.get("Kind") not in UNIT_KINDS:
                continue
            pp = f.get("Portrait prompt")
            if not pp:
                continue
            need_sheet, need_portrait = not f.get("Reference sheet"), not f.get("Portrait")
            if not (need_sheet or need_portrait):
                continue
            if name in special:
                sheet_prompt = template_body(special[name])
            else:
                sheet_prompt = sheet_tpl.replace("[the character's Look]", (f.get("Look") or "").strip()) if f.get("Look") else None
            portrait_prompt = pp.strip() + "\n\n" + style
            bad = sorted(set(banned_in(portrait_prompt) + (banned_in(sheet_prompt) if need_sheet else [])))
            if bad:
                skipped.append(f"{table}/{name}: prompt contains banned words {bad}")
                continue
            if need_sheet and not sheet_prompt:
                skipped.append(f"{table}/{name}: no Look to make a reference sheet from")
                continue
            jobs.append({"table": table, "id": r["id"], "name": name, "slug": slug(name),
                         "sheet": sheet_prompt if need_sheet else None,
                         "portrait": portrait_prompt if need_portrait else None})
    return jobs, skipped


def count_images(jobs, tries):
    return sum(tries * (bool(j["sheet"]) + bool(j["portrait"])) for j in jobs)


# ---------- the run ----------
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--plan", action="store_true", help="list what would be made and the cost, make nothing")
    ap.add_argument("--run", action="store_true", help="make the pictures")
    ap.add_argument("--limit", type=int, default=0, help="only the first N characters/units")
    ap.add_argument("--only", default="", help="only names containing this text")
    ap.add_argument("--tries", type=int, default=1, help="versions of each picture (the first one goes to Airtable)")
    ap.add_argument("--max-dollars", type=float, default=10.0, help="refuse to start above this estimate (default 10)")
    ap.add_argument("--no-airtable", action="store_true", help="save files only, do not attach in Airtable")
    a = ap.parse_args()
    if not (a.plan or a.run):
        ap.error("say --plan or --run")
    if not os.environ.get("AIRTABLE_TOKEN"):
        sys.exit("AIRTABLE_TOKEN is not set.")

    jobs, skipped = build_plan()
    if a.only:
        jobs = [j for j in jobs if a.only.lower() in j["name"].lower()]
    if a.limit:
        jobs = jobs[: a.limit]
    n = count_images(jobs, a.tries)
    cost = n * PRICE_PER_IMAGE
    log(f"Plan: {len(jobs)} characters/units, {n} pictures, about ${cost:.2f} at ${PRICE_PER_IMAGE}/picture ({MODEL}).")
    for j in jobs:
        what = " + ".join(w for w, on in (("reference sheet", j["sheet"]), ("portrait", j["portrait"])) if on)
        log(f"  {j['table']}: {j['name']}: {what}")
    for s in skipped:
        log(f"  SKIPPED {s}")
    if a.plan:
        return
    if cost > a.max_dollars:
        sys.exit(f"Estimate ${cost:.2f} is above --max-dollars {a.max_dollars}. Use --limit or raise it on purpose.")
    if not os.environ.get("GEMINI_API_KEY"):
        sys.exit("GEMINI_API_KEY is not set.")
    check_model()

    day = datetime.date.today().isoformat()
    out_dir = os.path.join(OUT_ROOT, day)
    os.makedirs(out_dir, exist_ok=True)
    made, failed, spent = [], [], 0

    def save(j, kind, k, data):
        base = os.path.join(out_dir, j["table"].lower(), f"{j['slug']}-{kind}-{k}")
        os.makedirs(os.path.dirname(base), exist_ok=True)
        with open(base + ".png", "wb") as fh:
            fh.write(data)
        with open(base + "-dither.png", "wb") as fh:
            fh.write(dither(data))
        return base + ".png"

    def cell_empty(j, field):
        return not at_record(j["table"], j["id"])["fields"].get(field)

    for j in jobs:
        log(f"{j['table']}: {j['name']}")
        try:
            sheet = None
            if j["sheet"]:
                for k in range(1, a.tries + 1):
                    data = generate(j["sheet"], [], "16:9"); spent += 1
                    path = save(j, "sheet", k, data); made.append(path); log(f"  sheet {k}: {path}")
                    if k == 1:
                        sheet = data
                        if not a.no_airtable and cell_empty(j, "Reference sheet"):
                            at_attach(j["id"], "Reference sheet", data, f"{j['slug']}-sheet-{day}.png")
            else:
                # attachment URLs expire after a few hours: read a fresh one just before downloading
                sheet = download(at_record(j["table"], j["id"])["fields"]["Reference sheet"][0]["url"])
            if j["portrait"]:
                for k in range(1, a.tries + 1):
                    data = generate(j["portrait"], [sheet], "3:4"); spent += 1
                    path = save(j, "portrait", k, data); made.append(path); log(f"  portrait {k}: {path}")
                    if k == 1 and not a.no_airtable and cell_empty(j, "Portrait"):
                        at_attach(j["id"], "Portrait", data, f"{j['slug']}-portrait-{day}.png")
        except Exception as e:  # one bad picture must not stop the night
            failed.append(f"{j['table']}/{j['name']}: {e}")
            log(f"  FAILED: {e}")

    report = os.path.join(out_dir, f"report-{datetime.datetime.now().strftime('%H%M')}.md")
    with open(report, "w") as fh:
        fh.write(f"# Night art batch {day}\n\nModel {MODEL}. {spent} pictures made, about ${spent * PRICE_PER_IMAGE:.2f}.\n\n")
        fh.write("## Failed\n" + "".join(f"- {f}\n" for f in failed) + "\n## Skipped\n" + "".join(f"- {s}\n" for s in skipped))
        fh.write("\n## Log\n```\n" + "\n".join(LOG) + "\n```\n")
    log(f"Done: {spent} pictures, {len(failed)} failed. Report: {report}")


if __name__ == "__main__":
    main()
