#!/usr/bin/env python3
"""
Turn one window of a game screenshot into HTML that Paper's write_html turns into editable layers.

Input: the --dump-ui JSON and the 2x --screenshot PNG of the same frame (godot/Scripts/Ui/UiDump.cs).
- Text, boxes, outlines, lines and plain colour fills become real layers (fonts, sizes, colours as the game draws them;
  line breaks are the game's own).
- Anything the game draws by code or from a picture (icons, planets, gauges, the map) is cut from the screenshot
  and placed as an image layer.

Used as a module by build_frames.py; run alone it writes one window:
    dump2html.py DUMP.json SHOT.png ROOTKEY OUT.html --img-dir DIR --img-url PREFIX
"""
import argparse
import html
import json
import math
import os
import re

from PIL import Image

FAMILIES = {"Space Grotesk", "BitPotion", "JetBrains Mono", "Inter"}


def hexc(c):
    r, g, b, a = (max(0.0, min(1.0, v)) for v in c)
    h = "#%02x%02x%02x" % (round(r * 255), round(g * 255), round(b * 255))
    return h if a >= 0.999 else "rgba(%d,%d,%d,%s)" % (round(r * 255), round(g * 255), round(b * 255), ("%.3f" % a).rstrip("0").rstrip("."))


def px(v):
    v = round(v * 2) / 2
    return ("%d" % v if v == int(v) else "%.1f" % v) + "px"


def esc(s):
    return html.escape(s, quote=True)


def family(t):
    name = t["family"]
    for suffix in (" Medium", " Regular", " SemiBold", " Bold", " Light"):
        if name.endswith(suffix):
            name = name[: -len(suffix)]
    return name


class Window:
    def __init__(self, dump, shot_path, img_dir, img_url, tag, map_shot=None):
        self.d = dump
        self.nodes = dump["nodes"]
        self.kids = {}
        for n in self.nodes:
            self.kids.setdefault(n["p"], []).append(n)
        self.shot = Image.open(shot_path).convert("RGBA")
        self.scale = self.shot.width / dump["width"]
        # the map alone (every panel hidden): the map's picture must not carry the windows drawn over it
        self.map = Image.open(map_shot).convert("RGBA") if map_shot and os.path.exists(map_shot) else None
        self.img_dir, self.img_url, self.tag = img_dir, img_url, tag
        self.count = 0
        os.makedirs(img_dir, exist_ok=True)

    # ------------------------------------------------------------------ names
    def first_text(self, n, depth=0):
        if n.get("text") and n["text"].get("lines"):
            return " ".join(n["text"]["lines"]).strip()
        if depth > 6:
            return ""
        for c in self.kids.get(n["i"], []):
            t = self.first_text(c, depth + 1)
            if t:
                return t
        return ""

    def name(self, n, what):
        if n.get("full"):
            return n["full"][:50]
        t = self.first_text(n)
        base = {"group": "Group", "panel": "Box", "button": "Button", "image": n.get("cs") or "Image"}.get(what, what)
        if what == "image" and n.get("cs") in ("TextureRect", "Control", "PanelContainer"):
            base = "Picture"
        return ("%s: %s" % (base, t) if t else base)[:50]

    # ------------------------------------------------------------------ pieces
    def text_el(self, n, t, ox, oy, box=None):
        cm = [max(0, v) for v in ((box or {}).get("cm") or [0, 0, 0, 0])]   # Godot's -1 = unset = 0
        ax, ay = n["x"] + cm[0], n["y"] + cm[1]
        aw, ah = n["w"] - cm[0] - cm[2], n["h"] - cm[1] - cm[3]
        lines = t["lines"]
        lh = t["lineh"]                  # one line's height plus the label's line spacing
        sp = t.get("spacing", 0)
        total = lh * len(lines) - sp     # Godot puts the spacing only between lines
        maxw = max(t["widths"]) if t["widths"] else 0
        w = math.ceil(maxw) + 2
        ha = t["ha"]
        if ha == "Center":
            x = ax + aw / 2 - w / 2
            align = "center"
        elif ha == "Right":
            x = ax + aw - w
            align = "right"
        else:
            x, align = ax, "left"
        va = t["va"]
        y = ay + math.floor((ah - total) / 2) if va == "Center" else ay + ah - total if va == "Bottom" else ay
        # CSS gives every line the same line-height and centres the glyphs in it: start half a spacing higher
        y -= sp / 2
        total = lh * len(lines)
        fam = family(t)
        style = [
            "position:absolute", "left:" + px(x - ox), "top:" + px(y - oy), "width:" + px(w), "height:" + px(total),
            "font-family:'%s'" % fam, "font-weight:%d" % (500 if fam == "BitPotion" else t["weight"]), "font-size:%dpx" % t["size"],
            "line-height:" + px(lh), "color:" + hexc(t["color"]), "white-space:pre", "text-align:" + align,
        ]
        if t.get("underline"):
            style.append("text-decoration:underline")
        if t.get("letter"):
            style.append("letter-spacing:" + px(t["letter"]))
        label = lines[0][:50] if lines and lines[0] else "Text"
        return '<div layer-name="%s" style="%s">%s</div>' % (esc(label), ";".join(style), esc("\n".join(lines)))

    def box_el(self, box, x, y, w, h, name):
        if not box or ("bg" not in box and not any(box.get("bw") or [0])):
            return None
        em = box.get("em") or [0, 0, 0, 0]
        st = ["position:absolute", "left:" + px(x - em[0]), "top:" + px(y - em[1]),
              "width:" + px(w + em[0] + em[2]), "height:" + px(h + em[1] + em[3])]
        if box.get("bg"):
            st.append("background-color:" + hexc(box["bg"]))
        bw = box.get("bw") or [0, 0, 0, 0]
        if any(bw) and box.get("bc") and box["bc"][3] > 0.001:
            col = hexc(box["bc"])
            for side, wv in zip(("left", "top", "right", "bottom"), bw):
                if wv:
                    st.append("border-%s:%dpx solid %s" % (side, wv, col))
        elif not box.get("bg"):
            return None
        rad = box.get("rad") or [0, 0, 0, 0]
        if any(rad):
            st.append("border-radius:%s" % " ".join(px(r) for r in rad))
        if box.get("shadow"):
            size, col, sx, sy = box["shadow"]
            st.append("box-shadow:%s %s %s %s" % (px(sx), px(sy), px(size), hexc(col)))
        return '<div layer-name="%s" style="%s"></div>' % (esc(name), ";".join(st))

    def image_el(self, n, ox, oy, clip):
        x0, y0, x1, y1 = n["x"], n["y"], n["x"] + n["w"], n["y"] + n["h"]
        if clip:
            x0, y0, x1, y1 = max(x0, clip[0]), max(y0, clip[1]), min(x1, clip[2]), min(y1, clip[3])
        if x1 - x0 < 0.5 or y1 - y0 < 0.5:
            return None
        s = self.scale
        src = self.map if (self.map is not None and n.get("cs") == "GalaxyView") else self.shot
        crop = src.crop((round(x0 * s), round(y0 * s), round(x1 * s), round(y1 * s)))
        self.count += 1
        fname = "%s-%03d.png" % (self.tag, self.count)
        crop.save(os.path.join(self.img_dir, fname))
        st = ["position:absolute", "left:" + px(x0 - ox), "top:" + px(y0 - oy), "width:" + px(x1 - x0), "height:" + px(y1 - y0)]
        return '<img layer-name="%s" src="%s/%s" style="%s">' % (esc(self.name(n, "image")), self.img_url, fname, ";".join(st))

    # ------------------------------------------------------------------ the tree
    def emit(self, n, ox, oy, clip, parent_rect):
        """Returns a list of HTML elements for n, positioned against (ox, oy)."""
        k = n["kind"]
        rect = (n["x"], n["y"], n["w"], n["h"])
        if k == "image":
            e = self.image_el(n, ox, oy, clip)
            return [e] if e else []
        if k == "color":
            c = n.get("color")
            out = []
            if c and c[3] >= 0.005:
                st = ["position:absolute", "left:" + px(n["x"] - ox), "top:" + px(n["y"] - oy), "width:" + px(n["w"]), "height:" + px(n["h"]), "background-color:" + hexc(c)]
                out.append('<div layer-name="Fill" style="%s"></div>' % ";".join(st))
            for ch in self.kids.get(n["i"], []):   # a bar's fill drawn inside its track (a ship's hull bar)
                out.extend(self.emit(ch, ox, oy, clip, rect))
            return out
        if k == "line":
            ln = n.get("line")
            if ln:
                t = ln["thick"]
                if ln["vertical"]:
                    st = ["left:" + px(n["x"] + n["w"] / 2 - t / 2 - ox), "top:" + px(n["y"] - oy), "width:" + px(t), "height:" + px(n["h"])]
                else:
                    st = ["left:" + px(n["x"] - ox), "top:" + px(n["y"] + math.floor(n["h"] / 2 - t / 2) - oy), "width:" + px(n["w"]), "height:" + px(t)]
                return ['<div layer-name="Line" style="position:absolute;%s;background-color:%s"></div>' % (";".join(st), hexc(ln["color"]))]
            b = self.box_el(n.get("box"), n["x"] - ox, n["y"] - oy, n["w"], n["h"], "Line")
            return [b] if b else []
        if k in ("label", "link"):
            out = []
            b = self.box_el(n.get("box"), n["x"] - ox, n["y"] - oy, n["w"], n["h"], "Label box")
            if b:
                out.append(b)
            if n.get("text"):
                out.append(self.text_el(n, n["text"], ox, oy, n.get("box")))
            return out

        # containers, panels, buttons: their children are placed against their own corner
        sub_clip = clip
        if n.get("clip"):
            r = (n["x"], n["y"], n["x"] + n["w"], n["y"] + n["h"])
            sub_clip = r if not clip else (max(r[0], clip[0]), max(r[1], clip[1]), min(r[2], clip[2]), min(r[3], clip[3]))
        inner = []
        bg = self.box_el(n.get("box"), 0, 0, n["w"], n["h"], "Background") if k in ("panel", "button") else None
        if bg:
            inner.append(bg)
        if k == "button" and n.get("text"):
            inner.append(self.text_el(n, n["text"], n["x"], n["y"], n.get("box")))
        for c in self.kids.get(n["i"], []):
            inner.extend(self.emit(c, n["x"], n["y"], sub_clip, rect))
        if not inner:
            return []
        same_as_parent = parent_rect and all(abs(a - b) < 0.5 for a, b in zip(rect, parent_rect))
        if k == "none" and not n.get("clip") and (len(inner) < 2 or same_as_parent):
            # pass through: re-place the children against the parent's corner
            out = []
            for c in self.kids.get(n["i"], []):
                out.extend(self.emit(c, ox, oy, sub_clip, parent_rect))
            return out
        what = {"panel": "panel", "button": "button"}.get(k, "group")
        st = ["position:absolute", "left:" + px(n["x"] - ox), "top:" + px(n["y"] - oy), "width:" + px(n["w"]), "height:" + px(n["h"])]
        if n.get("clip"):
            st.append("overflow:hidden")
        return ['<div layer-name="%s" style="%s">%s</div>' % (esc(self.name(n, what)), ";".join(st), "".join(inner))]

    def root(self, key):
        roots = {r["key"]: r["i"] for r in self.d["roots"]}
        i = roots.get(key)
        if i is None:
            cand = [k for k in roots if k.startswith(key)]
            if not cand:
                raise SystemExit("no root %r in %s" % (key, list(roots)))
            i = roots[cand[0]]
        return self.nodes[i]

    def window_html(self, key, left, top, name=None):
        """The window as one layer named `name`, its top-left corner at (left, top) of the frame."""
        n = self.root(key)
        inner = []
        bg = self.box_el(n.get("box"), 0, 0, n["w"], n["h"], "Background")
        if bg:
            inner.append(bg)
        for c in self.kids.get(n["i"], []):
            inner.extend(self.emit(c, n["x"], n["y"], None, (n["x"], n["y"], n["w"], n["h"])))
        st = "position:absolute;left:%s;top:%s;width:%s;height:%s;overflow:hidden" % (px(left), px(top), px(n["w"]), px(n["h"]))
        nm = name or self.name(n, "panel")
        return '<div layer-name="%s" style="%s">%s</div>' % (esc(nm), st, "".join(inner)), n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dump")
    ap.add_argument("shot")
    ap.add_argument("key")
    ap.add_argument("out")
    ap.add_argument("--img-dir", required=True)
    ap.add_argument("--img-url", required=True)
    a = ap.parse_args()
    w = Window(json.load(open(a.dump)), a.shot, a.img_dir, a.img_url, os.path.splitext(os.path.basename(a.out))[0])
    h, n = w.window_html(a.key, 0, 0)
    page = '<div style="position:relative;width:%s;height:%s;background:#0b0b0d">%s</div>' % (px(n["w"]), px(n["h"]), h)
    open(a.out, "w").write(page)


if __name__ == "__main__":
    main()
