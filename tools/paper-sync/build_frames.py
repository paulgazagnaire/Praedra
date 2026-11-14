#!/usr/bin/env python3
"""
Build the HTML for the UI frames of Paul's Paper page "UIs---V2--human" from game screenshots (tools/paper-sync/README.md).

    build_frames.py RAW_DIR OUT_DIR --img-url paper-asset:///Users/.../img [--stories stories.json]

RAW_DIR holds, per scene, NAME.png (2x screenshot), NAME.json (--dump-ui) and NAME.map.png (the map alone).
OUT_DIR gets one HTML file per frame (the artboard's new children) and img/ with every picture they use.
"""
import argparse
import html
import json
import os

from dump2html import Window, px, esc

BG = "#111214"            # the page's artboard background (unchanged)
TEXT = "#7d8699"          # window look: readable text and small labels
ACTIVE = "#909ebd"        # window look: names and values
EDGE = "#606060"          # window look: the window's edge
PANEL = "#0b0b0d"
GOOD, WARN, BAD = "#5ccb8a", "#eab04a", "#ff6b6b"   # Ui.Good, Ui.Warn, Ui.Danger

FRAMES = [
    ("Window - Fleet", "Fleet window", [
        ("fleet-command", "Fleet", "1 · Command tab"),
        ("fleet-ships", "Fleet", "2 · Ships tab"),
        ("fleet-fighters", "Fleet", "3 · Fighters tab"),
        ("fleet-troops", "Fleet", "4 · Troops tab"),
        ("fleet-orders", "Fleet", "5 · Orders tab (new)"),
    ]),
    ("Window - Ground", "Ground window", [
        ("ground-leaders", "Ground Charter", "1 · Leaders tab (own system)"),
        ("ground-troops", "Ground Charter", "2 · Troops tab"),
        ("ground-fighters", "Ground Charter", "3 · Fighters tab"),
        ("ground-fleets", "Ground Charter", "4 · Fleets tab"),
        ("ground-lore", "Ground Charter", "5 · Lore tab"),
        ("ground-unexplored", "Ground Brushfield", "6 · Enemy / unexplored system"),
        ("ground-harvested", "Ground Brushfield", "7 · Harvested system"),
    ]),
    ("Window - Infrastructure", "Infrastructure window", [
        ("infra-ships", "Infra Charter", "1 · Your planet: two columns, Build on the right"),
        ("infra-facilities", "Infra Charter", "2 · Build: Facilities"),
        ("infra-busy", "Infra Charter", "3 · A yard at work"),
    ]),
    ("Window - Sector", "Sector window", [
        ("sector", "Sector", "1 · Sector window, half the screen"),
    ]),
]


def caption(text):
    return ('<div layer-name="Caption" style="font-family:\'Space Grotesk\';font-size:12px;line-height:16px;color:%s;white-space:pre">%s</div>'
            % (TEXT, esc(text)))


def column(win_html, cap):
    return ('<div layer-name="%s" style="display:flex;flex-direction:column;gap:8px;flex-shrink:0">%s%s</div>'
            % (esc(cap), caption(cap), win_html))


def chip(status):
    col = {"yes": GOOD, "partly": WARN, "no": BAD}[status]
    return ('<div layer-name="Status %s" style="display:flex;padding:0px 6px;border:1px solid %s;flex-shrink:0">'
            '<div style="font-family:\'Space Grotesk\';font-size:12px;line-height:16px;color:%s;white-space:pre">%s</div></div>' % (status, col, col, status))


def stories_panel(window_name, rows):
    n = len(rows)
    c = {s: sum(1 for r in rows if r["status"] == s) for s in ("yes", "partly", "no")}
    t = lambda s, size=13, col=ACTIVE, extra="": ('<div style="font-family:\'Space Grotesk\';font-size:%dpx;line-height:%dpx;color:%s;%s">%s</div>'
                                                   % (size, size + 5, col, extra, esc(s)))
    count = ('<div layer-name="Count" style="display:flex;flex-direction:row;gap:6px">%s%s%s%s</div>'
             % (t("%d stories ·" % n, 12, TEXT, "white-space:pre"), t("%d yes ·" % c["yes"], 12, GOOD, "white-space:pre"),
                t("%d partly ·" % c["partly"], 12, WARN, "white-space:pre"), t("%d no" % c["no"], 12, BAD, "white-space:pre")))
    head = ('<div layer-name="Header" style="display:flex;flex-direction:row;gap:16px">'
            '<div style="width:44px;flex-shrink:0">%s</div><div style="width:300px;flex-shrink:0">%s</div><div style="width:150px;flex-shrink:0">%s</div></div>'
            % (t("ID", 12, TEXT), t("STORY", 12, TEXT), t("TODAY", 12, TEXT)))
    body = []
    for r in rows:
        note = t(r["note"], 12, TEXT) if r.get("note") else ""
        body.append('<div layer-name="%s" style="display:flex;flex-direction:row;gap:16px;align-items:start">'
                    '<div style="width:44px;flex-shrink:0">%s</div><div style="width:300px;flex-shrink:0">%s</div>'
                    '<div style="width:150px;flex-shrink:0;display:flex;flex-direction:column;gap:4px;align-items:start">%s%s</div></div>'
                    % (esc(r["id"]), t(r["id"], 12, TEXT, "font-family:'JetBrains Mono'"), t(r["story"]), chip(r["status"]), note))
    return ('<div layer-name="User stories" style="display:flex;flex-direction:column;gap:16px;padding:24px;background-color:%s;border:1px solid %s;flex-shrink:0">'
            '%s%s%s%s<div layer-name="Stories" style="display:flex;flex-direction:column;gap:14px">%s</div></div>'
            % (PANEL, EDGE, t("USER STORIES", 12, TEXT), t("What the player can do in the %s" % window_name, 20, ACTIVE),
               count, head, "".join(body)))


def build(raw, out, img_url, stories):
    os.makedirs(out, exist_ok=True)
    img_dir = os.path.join(out, "img")
    made = []
    for frame, wname, scenes in FRAMES:
        cols = []
        for scene, key, cap in scenes:
            w = Window(json.load(open(os.path.join(raw, scene + ".json"))), os.path.join(raw, scene + ".png"), img_dir, img_url, scene)
            h, n = w.window_html(key, 0, 0, name="%s - %s" % (wname, cap.split(" · ", 1)[-1]))
            h = h.replace("position:absolute;left:0px;top:0px;", "position:relative;flex-shrink:0;", 1)
            cols.append(column(h, cap))
        parts = ['<div layer-name="Windows" style="display:flex;flex-direction:row;gap:24px;align-items:start;flex-shrink:0">%s</div>' % "".join(cols)]
        if stories and wname in stories:
            parts.append(stories_panel(wname, stories[wname]))
        doc = "".join(parts)
        fn = os.path.join(out, frame.replace(" ", "").replace("-", "_") + ".html")
        open(fn, "w").write(doc)
        made.append((frame, fn))
    # the four windows over the map, as the game shows them
    w = Window(json.load(open(os.path.join(raw, "four.json"))), os.path.join(raw, "four.png"), img_dir, img_url, "four",
               map_shot=os.path.join(raw, "four.map.png"))
    h, n = w.window_html("Screen", 0, 0, name="Screen - four windows over the map")
    h = h.replace("position:absolute;left:0px;top:0px;", "position:relative;flex-shrink:0;", 1)
    fn = os.path.join(out, "Four_windows.html")
    open(fn, "w").write(h)
    made.append(("Four windows - Charter (Hand)", fn))
    return made


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("raw")
    ap.add_argument("out")
    ap.add_argument("--img-url", required=True)
    ap.add_argument("--stories")
    a = ap.parse_args()
    stories = json.load(open(a.stories)) if a.stories else None
    for frame, fn in build(a.raw, a.out, a.img_url, stories):
        print(frame, fn, os.path.getsize(fn))


if __name__ == "__main__":
    main()
