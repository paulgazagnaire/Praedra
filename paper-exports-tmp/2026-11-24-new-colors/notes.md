# Paper export — "new-colors" frames, 2026-11-24

Source: Paper file "Praedra" (01M4XB1NAX45Y7V9FT3DQMTQK1), page "UIs---V2--human" (p-3-1). All eight frames were on that page. Read-only export through Paper's local MCP server; nothing in Paper was changed.

## Files per frame

- `FRAME@1x.png`, `FRAME@2x.png` — Paper PNG export of the whole artboard.
- `FRAME-tree.json` — full node tree. Each node: id, name, type, x, y (relative to parent), worldX, worldY, width, height, visible, text, imageFill (URL if the node has an image fill) and `styles` = the node's computed CSS from Paper (font family/size/weight/line height/letter spacing/text transform, color, background colour, borders, border radius, opacity, padding, gap, flex direction, alignment, position). Colours, strokes and radii are in `styles`.
- `FRAME-styles.json` — the same computed styles as a flat map `nodeId -> {name, ...css}`.
- `FRAME-jsx.txt` — Paper's JSX export (inline styles, with node ids).
- `_scripts/` — the scripts used, raw node dumps (`*-raw.json`), Paper tree summaries (`*-tree-summary.txt`) and logs.

## Things to know before rebuilding

1. **Two artboards are named `fleet-new-frame-1-new-colors`.** The main one (id 12JJ-0, at canvas 1585,2669) sits in the row with the other new-colors frames and is exported under the plain name. A second copy (id 14G9-1, at canvas 3270,1574) is exported as `fleet-new-frame-1-new-colors-copy-14G9-1*`. I compared them: same PNG bytes, and the node trees differ only in the root's canvas position. Use either.
2. **Layer names are misleading.** Many text layers keep names from older designs (title layer named "CHARTER" holds "FLEET 1", labels named "Energy slots  3 of 15", "Shipyard", "Gleaner", "1x", "ADMIRAL"). Use the `text` field, not the name.
3. **Section headings and window titles are uppercased by CSS** (`textTransform: uppercase`), so the stored text is mixed case ("Fleet actions", "Charter", "characters") but it renders as "FLEET ACTIONS", "CHARTER", "CHARACTERS". Texts below are quoted verbatim as stored; the rendered case is in brackets where it differs.
4. **Fonts:** BitPotion (pixel font) for window titles at 48px/27px line height, section headings at 24px/30px with 0.05em tracking, and the whole top bar in ui-update at 24px/30px. Space Grotesk for everything else: 12px/16px body (weight 400, labels 500), 16px/20px tab headers in the infrastructure frame, 10px/12px meter counts, 24px for the window-control glyphs.
5. **Window controls are text glyphs, not icons:** "⋮⋮" (drag handle, letter-spacing -5px), "–" (fold) and "×" (close), Space Grotesk 24px #DFDFDF.
6. **Sprites (re your note that sprites must have a transparent background):** the 40×40 ship sprite used as a placeholder in every tile is a PNG image fill that is **fully opaque** — I checked the pixels: 0 transparent pixels, background baked in as #141416. The same holds for the 16px fighter/person glyph images in fleet frame 2. The tile behind it is #0B0B0D, so in-game sprites should be drawn with a transparent background and the tile colour supplied by the UI, not by the sprite. Per the brief, no icon or sprite images are exported here.
7. Shared window structure (all fleet and ground frames, 344 wide): header block (background #0B0B0D, padding 24px) with title + controls and a status line + 2px progress bar (filled part #5AA9F0 at 70%, rest #616161 at opacity 0.4); a bottom border #1E1E21; a tab strip of 33px-high tabs (padding 8px, 1px top border #1E1E21) laid on a #1E1E21 strip with 1px gaps, so the gaps read as dividers (selected tab background #131313, others #0B0B0D); the body background #131313 with 16px padding and 24px gap between sections. Header block: padding 24px, gap 24px. Segmented toggle: 1px #36363C border, radius 4px, padding 2px; selected segment background #36363C, radius 2px. Tiles are 96×84: a 96×48 #0B0B0D "illustration" box with a centred 40×40 sprite, then a 12px name below. Buttons are 40px high, 1px #36363C border, radius 4px, 12px text. Drop zones use a dashed #36363C outline.

## Frame by frame

### fleet-new-frame-1-new-colors (346×764) — Fleet window, Command tab
- Title "FLEET 1"; controls "⋮⋮" "–" "×".
- Status line: "Status:" (left, #DFDFDF, weight 500) — "Moving" "/" "ETA: 29 days" (right, #525252); progress bar ~70% blue.
- Tabs: "Command" (selected) | "Ships" "3" | "Fighters" "8/16" | "Troops" "4/24" (counts in #525252).
- Section "Fleet actions" [FLEET ACTIONS]: buttons "Bombardment" (with a small pixel dropdown chevron), "Invasion", "Move".
- Section "Fleet behavior" [FLEET BEHAVIOR]: segmented toggle "Defensive" (selected, background #36363C) | "Elusive".
- Section "Fleet command" [FLEET COMMAND]: three slots labelled "Admiral" (filled: sprite tile, name "Hostilia\nSaxa"), "General" and "Commander" (both empty dashed drop zones "Drop a leader here").
- Section "Passengers" [PASSENGERS]: 3×2 grid of leader tiles: "Cyprian\nGalena", "Quintus\nFerro", "Fidelis\nCensor", "Aurelia\nQuell", "Mantis\nDoran", "Spurius\nCato".
- This frame has a 1px #181B21 border around the whole artboard (hence 346 wide); the others do not.

### fleet-new-frame-2-new-colors (344×755) — Fleet window, Ships tab
- Same header and tabs ("Ships" selected).
- One row of three ship tiles, each: sprite, name "Mandate", a stat row with a fighter glyph "6/6" and a person glyph "0/3" (#454545), then hull "2750" "/2750" (#454545).
- Rest of the body empty.

### fleet-new-frame-3-new-colors (344×754) — Fleet window, Fighters tab
- Same header and tabs ("Fighters" selected).
- 3×3 grid of fighter tiles, each named "Gleaner".
- Buttons: "Unload 9 at Charter" and "Ground at Charter" (the latter with the 16px military pixel icon).

### fleet-new-frame-4-new-colors (344×754) — Fleet window, Troops tab (empty)
- Same header; tabs show "Troops" "0/24" (selected).
- Dashed drop zone, full width: "Drop regiments here to load them".
- Button "Ground at Charter" with the 16px military pixel icon.

### ground-new-frame-1-new-colors (344×754) — Ground window for planet Charter, Command tab
- Title "Charter" [CHARTER]; controls "⋮⋮" "–" "×".
- Status line: "Loyalty" (left) — "70%" (right, #525252); progress bar ~70% blue.
- Tabs: "Command" (selected) | "Troops" "88/88" | "Fighters" "88/88" | "Fleet" "1" (the last tab is clipped at the right edge).
- Section "Ground command" [GROUND COMMAND]: two empty slots "General" and "Commander", each "Drop a leader here".
- Section "characters" [CHARACTERS]: 3×2 grid: "Cyprian\nGalena", "Quintus\nFerro", "Fidelis\nCensor", "Aurelia\nQuell", "Mantis\nDoran", "Spurius\nCato".

### ground-new-frame-2-new-colors (344×754) — Ground window, Troops tab
- Same header and tabs as ground 1 ("Troops" selected).
- Section "Regiments" [REGIMENTS]: 3×3 grid, every tile "Pacifiers".
- Section "Special forces" [SPECIAL FORCES]: one row of three tiles "Gleaner".
- Button "Train troops" with the 16px military pixel icon.

### infrastructure-new-frame-1-new-colors (760×828) — Infrastructure window for Charter
- Title "CHARTER: INFRASTRUCTURE"; controls "⋮⋮" "–" "×".
- Meters row: "System energy" "12/12" with a segmented bar (blue-grey segments #909EBD / #4A5878, dark #252525, a small marker above), divider, "Raw material" "12/12" with a grey segmented bar (#C8C8C8 / #6A6A6A / #252525). Counts in 10px #525252.
- Right of the meters, buttons "Ground and military" (military icon) and "Fleets" (fleet icon).
- Category tabs (each with a 16px infrastructure icon, 16px/20px text, count in #454545): "Construction yards" "3" (selected; sub-line "Building:" … "29 days left", progress bar) | "Orbital shipyards" "5" (sub-line "Idle" … "-", #454545) | "Training facilities" "0" (count #676767; sub-line "No infrastructure" … "-"); then a column with two vector chevrons (next ">" and previous "<").
- Section "Buildings" [BUILDINGS] "3": three empty-looking 1px-bordered boxes, each with a 24px infrastructure icon.
- Section "Build" [BUILD]: 4×3 grid of build cards (background #0B0B0D-ish, 1px borders #1E1E21/#2D2E32), each with a 24px infrastructure icon, a name, and a cost row "20" [refined-material icon] "|" "80" [refined-material icon]. Names per row: "Mine", "Advanced Training\nfacility", "Advanced Training\nfacility", "Mordant Covenant II" (non-breaking space before "II"); the three rows are identical.

### ui-update-nov-24 (1440×810) — main game screen, top bar over the galaxy map
- Background: full-bleed image "Blue starry night" (1441×738 at y 40). The galaxy map you see (stars, nebula, grey sector polygons, lanes, blue/grey/orange system dots, the two fleet markers and the ringed selected system) is **baked into that image**, not built from nodes.
- Top bar (40px, black #000000, bottom border #181B21), left to right: hamburger pixel glyph; [refined-material-looking icon] "Layers"; "GOAL" "0/3"; search box "Search" with a magnifying-glass icon; then on the right [raw icon] "Raw" "9999", [refined icon] "Refined" "9999", [upkeep icon] "Upkeep" "9999/9999"; "DAY" "9999"; speed controls: pause glyph, "1x" (selected, #DFDFDF), "2x", "5x" (#84878E).
- All top-bar text is BitPotion 24px/30px; labels #84878E, values #DFDFDF.

## Other artboards on page "UIs---V2--human" whose names match new-colors / fleet / ground / infrastructure / ui-update

(The 8 exported frames and the duplicate `fleet-new-frame-1-new-colors` are left out.)

- fleet
- Ground
- icon-fleet
- icon-fleet-16px (four artboards with this name)
- icon-fleet-24px (three)
- icon-infrastructure
- icon-infrastructure-16px (four)
- icon-infrastructure-24px (three)
- Window - Ground
- Window - Fleet
- Window - Infrastructure
- infrastructure
- fleet-new-frame-1
- fleet-new-frame-2
- fleet-new-frame-3
- fleet-new-frame-4
- Fleet window - draft 3 - two columns - 2026-11-21
- Fleet window - draft 4 - one column - 2026-11-21
- Window Fleet 370
- Zone Fleet 370 Fighters
- Fleet window - as built - 2026-11-22
- Fleet strength - the 3 answers - 2026-11-22

## Icons and pictures (not exported)

Positions: x,y relative to the parent; worldX,worldY on the canvas. Pixel icons are frames of 1x1 rectangles; sprites are 40x40 frames with a PNG image fill.


### fleet-new-frame-1-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12KT-0 | Frame | Frame | 8×8 | 99,15 | 1702,2888 | Frame | pixel glyph built from 12 1x1 rectangles (e.g. dropdown chevron / hamburger menu) |
| 12LP-0 | IconPic | Frame | 40×40 | 28,4 | 1630,3092 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12M5-0 | IconPic | Frame | 40×40 | 28,4 | 1630,3236 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12M9-0 | IconPic | Frame | 40×40 | 28,4 | 1738,3236 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12MD-0 | IconPic | Frame | 40×40 | 28,4 | 1846,3236 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12MI-0 | IconPic | Frame | 40×40 | 28,4 | 1630,3336 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12MM-0 | IconPic | Frame | 40×40 | 28,4 | 1738,3336 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12MQ-0 | IconPic | Frame | 40×40 | 28,4 | 1846,3336 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |

### fleet-new-frame-2-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 128H-0 | IconPic | Frame | 40×40 | 28,4 | 1987,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 128K-0 | IconPic | Frame | 16×16 | 2,0 | 1963,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |
| 128N-0 | IconPic | Frame | 16×16 | 51,0 | 2012,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |
| 128U-0 | IconPic | Frame | 40×40 | 28,4 | 2095,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 128X-0 | IconPic | Frame | 16×16 | 2,0 | 2071,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |
| 1290-0 | IconPic | Frame | 16×16 | 51,0 | 2120,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |
| 1297-0 | IconPic | Frame | 40×40 | 28,4 | 2203,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 129A-0 | IconPic | Frame | 16×16 | 2,0 | 2179,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |
| 129D-0 | IconPic | Frame | 16×16 | 51,0 | 2228,2915 | Group: 6/6 | 16px stat glyph image (fighter silhouette or person silhouette, grey) |

### fleet-new-frame-3-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12AR-0 | IconPic | Frame | 40×40 | 28,4 | 2345,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12AV-0 | IconPic | Frame | 40×40 | 28,4 | 2453,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12AZ-0 | IconPic | Frame | 40×40 | 28,4 | 2561,2847 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12B4-0 | IconPic | Frame | 40×40 | 28,4 | 2345,2931 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12B8-0 | IconPic | Frame | 40×40 | 28,4 | 2453,2931 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12BC-0 | IconPic | Frame | 40×40 | 28,4 | 2561,2931 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12BH-0 | IconPic | Frame | 40×40 | 28,4 | 2345,3015 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12BL-0 | IconPic | Frame | 40×40 | 28,4 | 2453,3015 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12BP-0 | IconPic | Frame | 40×40 | 28,4 | 2561,3015 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12IU-0 | icon-military-16px | Frame | 16×16 | 8,11 | 2465,3115 | Frame | military icon 16px |

### fleet-new-frame-4-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12J4-0 | icon-military-16px | Frame | 16×16 | 8,11 | 2690,2959 | Frame | military icon 16px |

### ground-new-frame-1-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12PM-0 | IconPic | Frame | 40×40 | 28,4.0 | 1629,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12PQ-0 | IconPic | Frame | 40×40 | 28,4.0 | 1737,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12PU-0 | IconPic | Frame | 40×40 | 28,4.0 | 1845,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12PZ-0 | IconPic | Frame | 40×40 | 28,4.0 | 1629,4128 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12Q3-0 | IconPic | Frame | 40×40 | 28,4.0 | 1737,4128 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12Q7-0 | IconPic | Frame | 40×40 | 28,4.0 | 1845,4128 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |

### ground-new-frame-2-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12RX-0 | IconPic | Frame | 40×40 | 28,4 | 1987,3860 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12S1-0 | IconPic | Frame | 40×40 | 28,4 | 2095,3860 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12S5-0 | IconPic | Frame | 40×40 | 28,4 | 2203,3860 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SA-0 | IconPic | Frame | 40×40 | 28,4 | 1987,3944 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SE-0 | IconPic | Frame | 40×40 | 28,4 | 2095,3944 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SI-0 | IconPic | Frame | 40×40 | 28,4 | 2203,3944 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SN-0 | IconPic | Frame | 40×40 | 28,4.0 | 1987,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SR-0 | IconPic | Frame | 40×40 | 28,4.0 | 2095,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12SV-0 | IconPic | Frame | 40×40 | 28,4.0 | 2203,4028 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12UH-0 | IconPic | Frame | 40×40 | 28,4.0 | 1987,4156 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12UD-0 | IconPic | Frame | 40×40 | 28,4.0 | 2095,4156 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12U9-0 | IconPic | Frame | 40×40 | 28,4.0 | 2203,4156 | illustration | placeholder blue pixel-art ship sprite (same sprite reused for leaders, ships, fighters, regiments), opaque #141416 background |
| 12T1-0 | icon-military-16px | Frame | 16×16 | 8,11.0 | 1968,4256.0 | Frame | military icon 16px |

### infrastructure-new-frame-1-new-colors

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 12V0-0 | Frame | Frame | 3×5 | 77,0 | 1686,4673 | Frame | pixel glyph built from 9 1x1 rectangles (e.g. dropdown chevron / hamburger menu) |
| 12VX-0 | Frame | Frame | 3×5 | 47,0 | 1864,4673 | Frame | pixel glyph built from 9 1x1 rectangles (e.g. dropdown chevron / hamburger menu) |
| 13XT-0 | icon-military-16px | Frame | 16×16 | 8,11 | 2088,4660 | Frame | military icon 16px |
| 13YE-0 | icon-fleet-16px | Frame | 16×16 | 8,11 | 2252,4660 | Frame | fleet icon 16px |
| 12XE-0 | icon-infrastructure-16px | Frame | 16×16 | 0,2.0 | 1601,4747.0 | Frame | infrastructure icon 16px |
| 12Y3-0 | icon-infrastructure-16px | Frame | 16×16 | 0,2.0 | 1850,4747.0 | Frame | infrastructure icon 16px |
| 12YR-0 | icon-infrastructure-16px | Frame | 16×16 | 0,2.0 | 2082,4747.0 | Frame | infrastructure icon 16px |
| 12ZE-0 | SVG | SVG | 24×24 | 11,11.2 | 2310,4740.2 | Frame | vector chevron (tab strip scroll arrow) |
| 12ZH-0 | SVG | SVG | 24×24 | 11,11.2 | 2310,4787.8 | Frame | vector chevron (tab strip scroll arrow) |
| 12ZR-0 | icon-infrastructure-24px | Frame | 24×24 | 44,15.0 | 1646,4891.0 | Frame | infrastructure icon 24px |
| 1309-0 | icon-infrastructure-24px | Frame | 24×24 | 44,15.0 | 1768,4891.0 | Frame | infrastructure icon 24px |
| 130R-0 | icon-infrastructure-24px | Frame | 24×24 | 44,15.0 | 1890,4891.0 | Frame | infrastructure icon 24px |
| 131G-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1617,4999 | Frame | infrastructure icon 24px |
| 1322-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1636,5077.5 | Frame | refined-material icon 16px |
| 1334-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1705,5077.5 | Frame | refined-material icon 16px |
| 1345-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1799.2,4999 | Frame | infrastructure icon 24px |
| 134R-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1818.2,5077.5 | Frame | refined-material icon 16px |
| 135T-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1887.2,5077.5 | Frame | refined-material icon 16px |
| 136U-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1981.5,4999 | Frame | infrastructure icon 24px |
| 137G-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2000.5,5077.5 | Frame | refined-material icon 16px |
| 138I-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2069.5,5077.5 | Frame | refined-material icon 16px |
| 139J-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 2163.8,4999 | Frame | infrastructure icon 24px |
| 13A5-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2182.8,5077.5 | Frame | refined-material icon 16px |
| 13B7-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2251.8,5077.5 | Frame | refined-material icon 16px |
| 13C9-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1617,5128 | Frame | infrastructure icon 24px |
| 13CV-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1636,5206.5 | Frame | refined-material icon 16px |
| 13DX-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1705,5206.5 | Frame | refined-material icon 16px |
| 13EY-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1799.2,5128 | Frame | infrastructure icon 24px |
| 13FK-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1818.2,5206.5 | Frame | refined-material icon 16px |
| 13GM-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1887.2,5206.5 | Frame | refined-material icon 16px |
| 13HN-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 1981.5,5128 | Frame | infrastructure icon 24px |
| 13I9-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2000.5,5206.5 | Frame | refined-material icon 16px |
| 13JB-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2069.5,5206.5 | Frame | refined-material icon 16px |
| 13KC-0 | icon-infrastructure-24px | Frame | 24×24 | 0,0 | 2163.8,5128 | Frame | infrastructure icon 24px |
| 13KY-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2182.8,5206.5 | Frame | refined-material icon 16px |
| 13M0-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2251.8,5206.5 | Frame | refined-material icon 16px |
| 13N2-0 | icon-infrastructure-24px | Frame | 24×24 | 0,-0.0 | 1617,5257.0 | Frame | infrastructure icon 24px |
| 13NO-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1636,5335.5 | Frame | refined-material icon 16px |
| 13OQ-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1705,5335.5 | Frame | refined-material icon 16px |
| 13PR-0 | icon-infrastructure-24px | Frame | 24×24 | 0,-0.0 | 1799.2,5257.0 | Frame | infrastructure icon 24px |
| 13QD-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 1818.2,5335.5 | Frame | refined-material icon 16px |
| 13RF-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 1887.2,5335.5 | Frame | refined-material icon 16px |
| 13SG-0 | icon-infrastructure-24px | Frame | 24×24 | 0,-0.0 | 1981.5,5257.0 | Frame | infrastructure icon 24px |
| 13T2-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2000.5,5335.5 | Frame | refined-material icon 16px |
| 13U4-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2069.5,5335.5 | Frame | refined-material icon 16px |
| 13V5-0 | icon-infrastructure-24px | Frame | 24×24 | 0,-0.0 | 2163.8,5257.0 | Frame | infrastructure icon 24px |
| 13VR-0 | icon-refined-material-16px | Frame | 16×16 | 19,1.5 | 2182.8,5335.5 | Frame | refined-material icon 16px |
| 13WT-0 | icon-refined-material-16px | Frame | 16×16 | 20,1.5 | 2251.8,5335.5 | Frame | refined-material icon 16px |

### ui-update-nov-24

| id | name | type | w×h | x,y | worldX,worldY | parent | depicts |
|---|---|---|---|---|---|---|---|
| 14FG-1 | Blue starry night | Rectangle | 1441×738 | 0,40 | 1593,750 | ui-update-nov-24 | full-screen galaxy map background image (stars, nebula, sector polygons, lanes and system dots are baked into the image) |
| 143J-2 | pause-icon | Frame | 16×16 | 8,8 | 2815,722 | Frame | pause glyph (two bars) |
| 146D-2 | icon-raw-material-16px | Frame | 16×16 | 0,8 | 2349,722 | Frame | raw-material icon 16px |
| 145C-2 | icon-refined-material-16px | Frame | 16×16 | 0,8 | 2443,722 | Frame | refined-material icon 16px |
| 1479-2 | icon-upkeep-16px | Frame | 16×16 | 0,8 | 2561,722 | Frame | upkeep icon 16px |
| 14AV-2 | icon-refined-material-16px | Frame | 16×16 | 8,7 | 1646,721 | Frame | refined-material icon 16px |
| 14AB-2 | icon-magnifiying-16px | Frame | 16×16 | 264,7 | 2075,721 | Frame | magnifying glass (search) icon 16px |
| 14BX-2 | Frame | Frame | 16×16 | 8,8 | 1605,722 | Frame | pixel glyph built from 28 1x1 rectangles (e.g. dropdown chevron / hamburger menu) |

## Colours (all eight frames)

Computed-style values, normalised to hex (#RRGGBBAA when alpha < 1). Context `UI` = window chrome/text/borders; `icon/sprite pixels` = colours inside the pixel icons (the game keeps its own icons, so those matter less). Counts are node-property occurrences.

| hex | context | property | count | frames | example nodes |
|---|---|---|---|---|---|
| #DFDFDF | icon/sprite pixels | backgroundColor | 732 | fleet1, fleet3, fleet4, ground2, infra1 | Rectangle |
| #DFDFDF | UI | color | 175 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1, ui-update | FLEET 1; ⋮⋮; –; × |
| #DFDFDF | UI | backgroundColor | 12 | infra1 | Rectangle |
| #BABABA | icon/sprite pixels | backgroundColor | 413 | infra1 | Rectangle |
| #DDDDDD | icon/sprite pixels | backgroundColor | 128 | ui-update | Rectangle |
| #0B0B0D | UI | backgroundColor | 78 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | fleet; Frame; illustration; infrastructure |
| #84878E | icon/sprite pixels | backgroundColor | 62 | ui-update | Rectangle |
| #84878E | UI | color | 9 | ui-update | 2x; 5x; Raw; Refined |
| #1E1E21 | UI | borderTopColor | 28 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | Frame |
| #1E1E21 | UI | backgroundColor | 7 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | Frame |
| #525252 | UI | color | 34 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | Moving; /; ETA: 29 days; 3 |
| #454545 | UI | color | 19 | fleet2, infra1 | 6/6; 0/3; 2750; /2750 |
| #454545 | UI | backgroundColor | 2 | infra1 | Enemy |
| #909EBD | icon/sprite pixels | backgroundColor | 9 | infra1 | Rectangle |
| #909EBD | UI | backgroundColor | 9 | infra1 | Free; Used; You 70% |
| #36363C | UI | borderColor | 10 | fleet1, fleet3, fleet4, ground2, infra1 | Frame |
| #36363C | UI | outline | 5 | fleet1, fleet4, ground1 | Frame |
| #36363C | UI | backgroundColor | 1 | fleet1 | Frame |
| #131313 | UI | backgroundColor | 14 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | Frame |
| #C8C8C8 | icon/sprite pixels | backgroundColor | 9 | infra1 | Rectangle |
| #C8C8C8 | UI | backgroundColor | 5 | infra1 | Free; Used |
| #181B21 | UI | backgroundColor | 8 | ui-update | Rectangle |
| #181B21 | UI | borderColor | 1 | fleet1 | fleet-new-frame-1-new-colors |
| #181B21 | UI | borderBottomColor | 1 | ui-update | Rectangle |
| #000000 | UI | backgroundColor | 9 | ui-update | Rectangle; Frame |
| #AAADB5 | icon/sprite pixels | backgroundColor | 9 | ui-update | Rectangle |
| #616161 | UI | backgroundColor | 7 | fleet1, fleet2, fleet3, fleet4, ground1, ground2, infra1 | Enemy |
| #B7B7B7 | icon/sprite pixels | backgroundColor | 7 | ui-update | Rectangle |
| #5AA9F0 | UI | backgroundColor | 6 | fleet1, fleet2, fleet3, fleet4, ground1, ground2 | You 70% |
| #252525 | UI | backgroundColor | 6 | infra1 | Free |
| #4A5878 | UI | backgroundColor | 6 | infra1 | Free; Enemy |
| #6A6A6A | UI | backgroundColor | 5 | infra1 | Free |
| #2D2E32 | UI | borderColor | 3 | infra1 | Frame |
| #BEBEBE | icon/sprite pixels | backgroundColor | 3 | ui-update | Rectangle |
| #6F6F6F | icon/sprite pixels | backgroundColor | 3 | ui-update | Rectangle |
| #676767 | UI | color | 1 | infra1 | 0 |
