# fleet-new-frame-1 / -2 vs Fleet window draft 4 (and draft 3)

Paper, file "Praedra", page "UIs---V2--human". Read only. Values below come from get_computed_styles / get_node_info, not from screenshots.

## Origin
The new frames are **not** copies of draft 4 nodes: their layer names come from the Infrastructure window ("infrastructure" artboard, "CHARTER: INFRASTRUCTURE"): "CHARTER", "Energy slots  3 of 15", "Shipyard", "Fold/Close Infra Charter", "You 70%", "Enemy". Paul rebuilt the Fleet window content (from draft 4) inside the Infrastructure window's visual language. Width kept: 344 (draft 4's one-column width). Both frames are one window: frame-1 = Command tab open, frame-2 = Ships tab open.

## Window by window (draft 4 window → new frames)

### Window chrome / header
- KEPT: "FLEET 1" title, BitPotion 48px/500 #BABABA; grip "⋮⋮", fold "–", close "×".
- RECOLOURED/REFONTED: ⋮⋮ – × from JetBrains Mono #909EBD → Space Grotesk #BABABA (24px).
- REMOVED: location line "FLEET · AT TIRYAL".
- REMOVED: "ALL FLEETS, IF AN ENEMY COMES" with Fight / Retreat underline toggle.
- REMOVED: stat row STRENGTH 178 / SLOWEST Mandate / FIGHTERS 8 / 22 / TROOPS 0 / 13.
- ADDED: status row: "Status:" (12px/500 #BABABA) left; "Moving" "/" "ETA: 29 days" (12px/400 #7D8699) right; under it a 296x2 progress bar, 70% filled #5AA9F0, rest #616161 at 40% opacity (same pattern as the Infrastructure window's "Building: 29 days left" bar).
- RESTYLED: header block is its own panel, bg #141416, padding 24, gap 24 (344x124). Draft 4 had one #141416 background with a 1px #606060 window border; the new frames have **no outer border**.

### Tabs
- Draft 4: text tabs with a 3px #BABABA underline on the active one: "Ships 5 | (Ship) | Fighters 8 | Troops | Leaders", 13px.
- New: a filled tab strip (Infrastructure style), 344x33, strip bg #1E1E21 with 1px gaps as separators; inactive tab bg #141416, **active tab bg #19191B** (same as the body, so it joins the content). Text 12px/400 #BABABA, count 12px #454545, padding 8.
- New tab set: **Command | Ships 3 | Fighters 8/16 | Troops 4/24**.
  - RENAMED/MERGED: "Leaders" → "Command", now FIRST; it holds fleet actions, behaviour, leaders and passengers.
  - REMOVED: the per-ship "Ship" tab (MANDATE #371, hull, IT CARRIES, EVERY MANDATE stats).
  - CHANGED counts: Fighters now "used/room" (8/16), Troops "used/room" (4/24) — draft 4 had "Fighters 8" and "Troops" with no count; Ships 3 (draft 4: Ships 5).
  - Frame-1 still has a stray count "3" inside the Command tab, clipped by the 72px tab width (invisible in the PNG). Frame-2 removed it.

### Command tab (frame-1) — replaces draft 4's button rows + Leaders tab
Body panel bg #19191B, padding 16, gap 24 between sections. Section titles 16px/500 #BABABA (new — draft 4 used 12px caps labels).
- "Fleet actions": buttons "Bombardment ▾", "Invasion", "Move".
  - RENAMED: "Bombard ▾" → "Bombardment ▾", "Invade" → "Invasion", "Move..." → "Move".
  - RECOLOURED: Bombard/Invade were disabled-grey (#454545 text, #1E1E21 border); now all three read enabled (#BABABA).
  - REMOVED: "Merge with ▾" and "Scrap..." (red #FF6B6B).
  - RESTYLED buttons: draft 4 = 32px high, padding 12, 1px #2D2E32 border, 12px/500; new = 35px high, padding 8, 1px #36363C outline, 12px/400.
- "Fleet behavior": "Defensive", "Elusive" — NEW (same button style; neither shown as selected). Probably replaces the Fight/Retreat toggle (inferred).
- "Fleet command": three columns "Admiral", "General", "Commander" (12px title case; draft 4: ADMIRAL / GENERAL / COMMANDER caps). Slots 90x120.
  - Admiral: FILLED card — 82x76 portrait area bg #141416 (placeholder: the ship sprite), name "Hostilia / Saxa", 1px #36363C solid outline.
  - General, Commander: empty, 1px #36363C **dashed** outline, centred "Drop a leader here" (draft 4 had the same text, smaller, dimmer).
- "Passengers": NEW content — 5 leader cards 93x120 in rows of 3 (gap 16): "Cyprian Galena", "Quintus Ferro", "Fidelis Censor", "Aurelia Quell", "Mantis Doran". Draft 4 had "PASSENGERS" with a "None" chip.

### Ships tab (frame-2) — replaces draft 4's ship card grid
- REMOVED: helper text "Click a ship for its damage, its cargo and its class. / Shift+click to pick several."
- REMOVED: 3-per-row 98x96 ship cards, the "Drop ships here to split" slot, selection highlight (#5AA9F0 border), the Decree ships.
- ADDED: full-width list rows 312x89, gap 8, 1px #2D2E32 border. Left 80px cell bg #141416 with the ship sprite; right side padding 16:
  - "Mandate" 13px/500 #BABABA
  - "Capacity" ... fighter icon "6/6"  regiment icon "0/3" (12px #454545)
  - "Hull" ... "2750" "/2750" (12px #454545)
  - three identical Mandate rows.
- Not designed in the new frames: Split / Scrap of picked ships, ship detail (old Ship tab), drag-to-split.

### Not present in either new frame
Fighters tab content, Troops tab content, Merge, Scrap, Split, Move-in-progress state, location ("at Tiryal"), Fight/Retreat, strength / slowest ship, class stats (Upkeep 71, Hull 2750, Shields 300, Heavy fire 260, Pulse fire 220, Fire front/sides/rear 200, 200, 80), "Ground at Tiryal" link, combat/event toasts.

### Sizes
Width 344 both (unchanged). Height: draft 4 window 737 (+ 24 caption); frame-1 863 (grows with content, no fixed height); frame-2 472.

## vs draft 3 (two columns, 698 wide)
Everything above applies, plus: back to one column (344); draft 3's "ALL FLEETS, WHEN AN ENEMY ARRIVES", "At Tiryal.", the SHIPS/FIGHTERS/REGIMENTS/LEADERS/STRENGTH/SLOWEST stat grid, the right-hand tab column and the extended class stats (Fighter room 6, Regiment room 3, Bombardment 2, Hold beam 3, Battle speed 4, Detection 10) are all absent. "REGIMENTS" wording of draft 3 is "Troops" in the new frames.
