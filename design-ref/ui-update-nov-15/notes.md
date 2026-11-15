# ui-update-nov-15 — notes (read-only export, 2026-11-15)

## Where
- Paper file "Praedra" (01M4XB1NAX45Y7V9FT3DQMTQK1), page "UIs---V2--human" (p-3-1).
- Artboard "ui-update-nov-15" (SIG-0): exact name match. 1440 x 810, at world x 1592, y 710.
- 155 nodes: 27 frames, 111 rectangles (mostly the pixels of 4 icons), 17 text layers. No SVG nodes, no groups beyond frames.

## Paul's notes
- None. The frame has no annotation, arrow or label outside the UI. Every text is UI copy (see texts.txt).
- Comment threads: 0 on the frame, 0 on the page (all statuses).

## What the frame shows
The galaxy map (layer "GalaxyView", an image fill — the **same picture** as in "Four windows - Charter (Hand) - 2026-11-14": identical file-asset URL) with a **redesigned top bar (HUD)** over it. Nothing else: no windows.

New top bar, left to right (x in px from the frame's left edge; all boxes hang from the top edge, y 0):
| Box | x | w x h | Content |
|---|---|---|---|
| Menu | 16 | 50 x 49 | "≡" |
| Goal | 65 | 100 x 49 | "GOAL" + "0/3" |
| Search | 197 | 320 x 49 | "Search" + magnifier icon on the right (justify space-between) |
| Layers | 516 | 87 x 49 | "Layers" |
| Raw (new) | 679 | 120 x 49 | raw-material icon + "Raw" + "9999" |
| Refined (new) | 798 | 144 x 49 | refined-material icon + "Refined" + "9999" |
| Upkeep (new) | 941 | 179 x 49 | upkeep (wrench) icon + "Upkeep" + "9999/9999" |
| Day | 1152 | 97 x 49 | "DAY" + "9999" |
| Speed | 1248 | 176 x 49 | "▶", "1x", "2x", "5x" (four 40 x 40 cells, gap 2) |

Style of every box: outer frame background #0B0B0D, 1 px border #181B21 on left, right and bottom only (no top border, no radius), padding 4 px;
inner cell background #000000, height 40, padding-inline 16 px (13 px for the menu, none for the 40 x 40 speed cells), gap 4, centred.
A full-width strip "Rectangle" 1440 x 32 at the top: #0B0B0D with a 1 px bottom border #181B21. Boxes are 49 px tall, so they hang 17 px below the strip.
Text: BitPotion 24 px / 30 px line, weight 500 — labels #84878E, values #DFDFDF. Exceptions: "≡" Space Grotesk 22 px #B0B2B7; "▶" Space Grotesk 16 px / 22 px #84878E.
No speed button is highlighted (all #84878E on #000000).
Icons: 16 x 16 pixel art made of 1 px rectangles, #DDDDDD (see svgs/). They also exist as separate artboards on the page (icon-raw-material-16px, icon-refined-material-16px, icon-upkeep-16px, icon-magnifiying-16px near x 3079–3343, y 915–955).

## Is it a copy of a synced artboard?
Not a layer-by-layer copy: layer names differ (Paul's layers are "Frame"/"Text 1x"; the synced HUD uses "Box: …", "Button: …"). The only shared layer name is "GalaxyView".
It is a **redesign of the game HUD shown in "Four windows - Charter (Hand) - 2026-11-14"** (same background image, same items). Comparison by script (source/hud-compare.json, both get_jsx files kept):

| Item | Before (game, Four windows) | After (Paul, ui-update-nov-15) |
|---|---|---|
| Layout | 3 floating boxes at y 20 with 1 px #606060 border on all sides: Menu (x 20), Search+Layers (x 558, 325 wide), Goal+Day+speed (x 1041, 379 wide) | 9 separate boxes hanging from the top edge (y 0) under a 32 px dark strip, border #181B21 on 3 sides, inner #000000 cells |
| Font | JetBrains Mono 11–22 px | BitPotion 24 px (pixel font) for all labels and numbers |
| Value colour | #ECEBE8 | #DFDFDF |
| Menu | "≡" JetBrains Mono 22 px | "≡" Space Grotesk 22 px, same colour #B0B2B7 |
| Goal | right side, "GOAL 0/3" in one text | moved to the left, next to the menu; "GOAL" grey + "0/3" light, two texts |
| Search | "Search  ( / )" centre-left, divider line, Layers in same box | "Search" + magnifier icon, no "( / )" hint; Layers is its own box |
| Raw / Refined / Upkeep | **not shown** | **new** boxes with icons |
| Day | "DAY" 11 px above "0" 18 px (stacked) | "DAY 9999" on one line |
| Speed | ▶ 1x 2x 5x **10x**; 5x highlighted (#2E3036 bg, #ECEBE8 text) | ▶ 1x 2x 5x — **10x removed**, no highlighted state drawn |
| Dividers | 1 px #232529 lines between groups | none (boxes are separate) |

Removed: "( / )" search hint, 10x button, divider lines, selected-speed highlight. Added: top strip, Raw, Refined, Upkeep, magnifier icon.

## Other changes on the page since 2026-11-14 (not asked, for information)
- 58 artboards now (43 yesterday). New: "ui-update-nov-15"; 12 icon artboards near it (incl. new icon-raw-material-16px, icon-refined-material-16px, icon-upkeep-16px, icon-magnifiying-16px); a second "Sector window - Charter Sector (Hand)" (S5U-0) at x 1592, y -140 just above the new frame.
- The old "Sector window - Charter Sector (Hand)" (MNB-0) is gone; an artboard with the same name, size and position now has id RUQ-0 (probably a re-paste or duplicate-and-delete — inferred).
- "icon-hand-16px" N28-0 is now named "icon-magnifiying-16px" and moved 40 px up (y -3301); a new "icon-hand-16px" (SJY-0) sits at its old place (2428, -3261).
- The five synced artboards are as I left them (same names, sizes, positions).
