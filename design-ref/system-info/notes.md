# system-info: design reference from Paper (temporary)

Source: Paper file "Praedra", page "UIs---V2--human", frame "system-info" (one frame with that name on the page).
Size 96 x 133 px, background #0B0B0D, clips its content. Fetched 2026-11-11, read only.

## Paul's notes

- No comment threads exist on this page (Paper list_comment_threads returned none, open or resolved).
- No text annotation sits next to the frame. The only loose text layers on the page are two "Charter" labels near an older tile iteration (world 616,-3462 and 516,-3780), not near system-info.
- The layer names are the only notes. The meaningful ones: "energy-scale" / "energy", "ressources-scale" / "ressources" (sic), segments named "Free" and "Used", and in the right bar "You 70%" and "Enemy".

## Files

- png/system-info@2x.png, @4x.png, @8x.png: the whole frame.
- png/children/: each part at 4x (right-energy-gauge = Paper layer "right", left-resources-gauge = layer "left", control-bar = the unnamed layer "Frame" on the right, the four icons), the planet image at 4x, the hidden "Dark pixel planet" at 2x, and the two raw image assets as stored by Paper (asset-01M75ENM... = the visible planet, asset-01M75DVN... = the hidden dark planet).
- png/context/: four neighbouring frames on the same page (earlier iterations of the same tile, some with the bars under the planet). Context only, not the design.
- jsx.txt: raw Paper get_jsx output, every style.
- tree-summary.txt: raw Paper get_tree_summary output (names, types, sizes, hidden flag).
- tree.json: the full layer tree (id, name, type, every style, text) built from the two above. Values match Paper get_computed_styles.
- svgs/: Paper holds no vector layers in this frame. Every icon, marker and corner is built from 1 px-grid rectangles; these SVGs rebuild each one exactly from those rectangles.

No strokes, corner radii or effects (shadows, blurs) exist on any layer.

## Layout (x, y in px from the frame's top-left)

| Part | Paper layer | Box | Notes |
|---|---|---|---|
| System name | Text "CHARTER" | x25 y0, 46x20 | BitPotion 16px, weight 500, line height 20px, letter spacing 0.1em, #03C93B, centred |
| Planet picture | "Screenshot 2026-11-11 at 15.02.21.png" | x0 y30, 96x96 | image, cover, centred. A pixelated Earth-like globe; a placeholder screenshot |
| Hidden planet | "Dark pixel planet" (hidden) | x19 y40, 56x56 | a dark red-brown pixel planet; probably an alternative or a variant for another planet type (inferred) |
| Resources gauge | "left" > "ressources-scale" | x7 y42, 10x71 | panel #000000 at 60% (#00000099) |
| Energy gauge | "right" > "energy-scale" | x19 y42, 10x71 | panel #00000099 |
| Control bar | unnamed "Frame" | x77 y42, 8x72 | panel #00000099, vertical flex, centred, gap 31 |
| Infrastructure icon | icon-infrastructure-16px | x10 y22, 16x16 | #009126 |
| Fleet icon | icon-fleet-16px | x73 y22, 16x16 | #009126 |
| Military icon | icon-military-16px | x10 y117, 16x16 | #009126 |
| Hand icon | icon-hand-16px | x73 y117, 16x16 | #009126 |

Both gauges sit on the left side of the planet: resources outside (x7), energy inside (x19). Paper names them "left" and "right" for their position relative to each other, not relative to the tile. Layer order (bottom to top): planet image, energy gauge, resources gauge, icons, control bar, name.

### The two gauges

Each gauge is 15 segments, 5x3 px each, 1 px gap, stacked top to bottom (flex column, gap 1) = 59 px tall, inside a 10x71 panel:
- A 4x4 corner bracket at the top (three 2x2 squares #555555, opacity 0.7) and at the bottom (three 2x2 squares #474747), on the outer edge, forming L-shaped brackets.
- The segment column starts 6 px below the panel top.

Resources gauge (outer, x7): column at x+5 inside the panel (the segments hug the planet side).
- top 6 segments: #555555 at opacity 0.6 (named "Free")
- next 4: #874300 dark orange (named "Free")
- bottom 5: #FFA942 bright orange (named "Used")
- marker: 3x5 pixel arrow pointing right, #FFA942, at the panel's left edge, top 39 in the scale, centred on segment 11 (the first bright one).

Energy gauge (inner, x19): column at x+0.
- top 1 segment: #555555 at opacity 0.6 (named "Free")
- next 7: #325697 dark blue (named "Free")
- bottom 7: #A1B1FF light blue (named "Used")
- marker: 3x5 pixel arrow pointing left, #A1B1FF, at x+7 (planet side), top 31, centred on segment 9 (the first light one).

What the marks seem to mean (inferred from names and colours, not stated by Paul):
- Each gauge fills from the bottom. Bright segments ("Used") = the amount in use; dark segments = available but unused capacity; grey segments = capacity the system does not have (gauge max 15).
- In this mock-up: resources capacity 9 of 15, 5 used; energy capacity 14 of 15, 7 used.
- The arrow marks the current level (the top of the used part). Both "Free" groups share one name in Paper, so the grey vs dark split is a guess from colour.

### The control bar (right side)

An 8x72 dark panel with a 2x4 bracket at top and bottom (two 2x2 squares #474747 each). Between them, a 59x2 px bar rotated 270deg so it runs bottom to top:
- "You 70%": #009126 green, width round(70%, 1px) = 41 px, from the bottom.
- "Enemy": #CF5900 orange at opacity 0.4, the remaining 18 px, at the top.

This reads as a control or loyalty split for the system: your share in green from the bottom, the enemy's in faded orange above (inferred from layer names).

### Icons (corners, all #009126, 16x16, built from rectangles)

- top-left: infrastructure (factory)
- top-right: fleet (ship)
- bottom-left: military (tank or turret)
- bottom-right: hand (the faction "The Hand", a raised hand)

They match the page's standalone icon-*-16px frames. They are probably presence markers or buttons to the Infrastructure, Fleet, Ground/military and faction views (inferred; no note says so).

## States and variants

The frame has no variants or states: one static mock-up at one set of values (resources 5 of 9, energy 7 of 14, control 70%). The only alternative in it is the hidden "Dark pixel planet". The neighbouring frames in png/context/ are earlier layouts of the same tile, not states.

## Colours

#0B0B0D tile background · #00000099 gauge panels · #03C93B name text · #009126 icons and "You" · #CF5900 at 40% "Enemy" · #FFA942 / #874300 resources used / free · #A1B1FF / #325697 energy used / free · #555555 at 60% empty segments, at 70% top brackets · #474747 bottom brackets.
