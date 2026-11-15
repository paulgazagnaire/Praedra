# Paper export — "ui-update-nov-15" (Praedra / UIs---V2--human) — 2026-11-15

Read only: nothing in Paper was changed. Tool output written by `_scripts/` calling Paper's local MCP endpoint (127.0.0.1:29979).

- notes.md — what the frame shows, Paul's notes (none), and the before/after comparison with the game HUD.
- frame@2x.png — whole frame at 2x (2880 x 1620).
- children/NN_<layer>_<id>@2x.png — each of the 11 direct children at 2x.
- jsx.txt — get_jsx, inline styles, with node ids and root position.
- tree.json — every node: id, name, type, x, y (relative), worldX/Y, width, height, visibility, text, all computed styles (fills, borders, padding, gap, alignment, font family/size/weight, line height, letter spacing, colours).
- tree-summary.txt, texts.txt — get_tree_summary (depth 10), every text verbatim.
- svgs/<icon>.svg — the 4 icons rebuilt as pixel-exact SVG from their 1 px rectangles (Paper exports SVG only for SVG nodes; these icons are rectangles). svgs/<icon>@8x.png — Paper's own render at 8x, for checking.
- images/galaxyview_SGY-0.png — the background map picture at full resolution (2880 x 1620), same file as in "Four windows".
- source@1x.png — "Four windows - Charter (Hand) - 2026-11-14" at 1x (the game HUD this frame redesigns).
- source/four-windows.jsx.txt — get_jsx of that artboard; source/hud-compare.json — top-bar texts of both, with position, size, font and colour (input of the table in notes.md).
- _page/ — get_basic_info and page index of UIs---V2--human, comment listings (empty).
- _scripts/ — the scripts.
