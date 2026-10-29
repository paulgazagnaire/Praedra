# Praedra (playable prototype)

A rough but playable front end for the rules core in `core/`. Pick a side, press Play, give orders to fleets and build on your planets. All rules live in `core/`; this folder only draws the screen, submits orders and calls the day step. The computer plays the other side (the core's simple AI). New games use the core's 360-system start.

## Launch it on the Mac (Apple Silicon or Intel)

1. Download `Praedra-mac.zip` and double-click it to unzip. You get **Praedra.app**. Drag it to Applications (or leave it in Downloads).
2. The app is not signed by an Apple developer account, so macOS blocks it the first time. Pick one way:
   - **Terminal:** open Terminal and paste (change the path if the app is elsewhere):
     ```
     xattr -dr com.apple.quarantine ~/Downloads/Praedra.app
     ```
     then double-click the app.
   - **No Terminal:** double-click the app once, close the warning, open **System Settings > Privacy & Security**, scroll down and click **Open Anyway**, then confirm.
3. Linux / Steam Deck: unzip `Praedra-linux.zip`, run `linux/Praedra.x86_64` (keep the `.pck` and the `data_...` folder next to it).

The first time you press **New Game**, a **How to play** page opens. You can open it again any time from the menu (top left, **How to play**) or with **F1**.

## How to play (short)

- **The screen** (Paul's mockup, 2026-10-28). The map fills the window; small dark pieces float at its edges. Top left: the menu (≡) and the map layers. Top centre: search, the **Layers** switch, and a pill that says why the clock stopped or how many things need you (click it for Needs you). Top right: **Goal**, the day, pause / play, **+1** and the speeds. Bottom left: Raw, Refined, Upkeep, then **→ HQ**, **→ Enemy HQ** and **Fit**. Bottom centre: a card for what you clicked (owner, support, troops, fleets; for a fleet where it is and goes) with **Build** or **Move...** and **Details →**. Bottom right: the latest message, with BAT / FLT / SYS / PRD / MSN to switch kind (a dot marks news) and **Messages** for the whole log. Blue is always you, orange the enemy, grey nobody, a hollow ring a system you never explored.
- **Goal.** The Hand wins by taking the Hearth (the Collective headquarters) **and** holding its two key leaders prisoner; the Collective wins by taking Charter and holding the Hand's two. A key leader who dies no longer counts. Either side also wins when the other holds no system, or by holding 70% of the systems the two sides hold for 180 days after the enemy capital fell. The **Goal** button says how close you are, and turns into a countdown ("You win in N days", red "Enemy wins in N days") once that race runs. The Hand does not know where the Hearth is at the start: **Enemy HQ: unknown**.
- **The window.** **Details →**, the menu or a key opens a window on the right with tabs: **Needs you** (Q), **Selected**, **Fleets** (F), **Leaders** (C), **Missions** (M) and **Log** (E). Its cross or Esc closes it. While it is open a map click shows the clicked thing in it; while it is closed a map click only fills the card. **Needs you** lists what wants a decision, worst first, each with the button that fixes it. **Run until something needs me** plays until a new problem appears.
- **Search** (/): type part of a name to find a system, one of your fleets or one of your leaders. Enter picks the first hit. While you move a fleet, picking a system sends the fleet there.
- **Map layers** (top left, V cycles them): Owners, Support, Trouble, Yards (yellow = idle), Intel age (bright = seen now) and Slots, then **Routes** (L) and **Legend**. A small key shows under the list. The map names systems only from a closer zoom; pointing at a dot names it.
- **Sectors on the map.** Each sector (the group of about ten systems) is a faint tile with a gap around it. Point at the open space of a sector: the tile lights up, its systems get their names, and a card says how many are yours. Click it to open its table.
- **Sector tables** (S, or the menu): a window over the bottom left of the map with two tables, one row per system of a sector: support, troops (yours: have / need), slots, what is built or which fleets are there, and how fresh the picture is. Click a row to select the system. Its cross or Esc closes it.
- **Fog of war.** You see live only the systems you hold or have a fleet at. Others show what your side saw last, with the day; the card turns yellow when the picture is more than 10 days old. Enemy fleets are drawn only where you see them. The computer opponent plays by the same rule.
- **Time.** Play (▶ or Space) runs the clock; pause (|| or Space) stops it. The chips 1x, 2x, 5x, 10x set the speed (keys 1-4): a day lasts 8 seconds at 1x, 4 at 2x, 1.6 at 5x, 0.8 at 10x. **+1** (N) moves one day. The clock stops by itself after a battle, a capture or an invasion; **Menu, Settings** adds more stops. Orders start when the next day starts; a note under the search counts the ones waiting.
- **Move a fleet.** Select it (map, Fleets tab or search), press **Move...**, click the destination. The order bar shows the route and the arrival day. Shift-click adds a waypoint, Backspace drops the last one. A route through a known enemy trap turns red: **Go anyway**, **Go around via X**, or Esc to cancel. The game never reroutes you by itself.
- **Fleets.** The Selected tab shows the ships by class, fighters, regiments aboard, troop and fighter room, officers, and **Split, transfer or merge**. **Load troops** keeps the planet's garrison safe.
- **Invade.** Load regiments, fly to the enemy or neutral system (enemy fleets there fight you first), optionally **Bombard** (military, civilian or everything), then **Invade**. One attack order per fleet per day.
- **Build.** Select one of your systems, open **Build**, pick Ships, Facilities, Troops or Fighters, set how many, click a row. Each row shows cost, upkeep and days, and says why it cannot be built now. **Stop...** on a busy yard asks first and says what is lost. Idle yards show in Needs you, on the Yards layer and in the sector windows.
- **Money.** **Raw** is ore from mines; refineries turn it into **Refined**, which you spend. **Upkeep** (used / limit) is what everything costs; over the limit, units get scrapped, and Needs you warns before that.
- **Leaders** (C): your leaders with where they are and their post, what the enemy needs from you, enemy leaders you have seen (key ones marked "you must capture"), and the fallen. Give a leader a fleet post (Admiral, Commander, General) or a system General post, move one alone, or send a team on a mission.
- **Missions** (M): each mission's team, target and phase, with **Cancel**. Results come in the Log.
- **The Hearth (Collective).** Select it (**My HQ**), press **Move HQ...**, click one of your systems.
- **Save / Load.** **Menu, Save game...** has six slots with the day and side; overwriting asks first. **Ctrl+S** (Cmd+S) saves to slot 1. The game autosaves every 30 days; **Continue** on the start screen loads that. Load and Back to the start screen ask before they replace the running game.

| What | How |
|---|---|
| Zoom | mouse wheel, `+` / `-`, or trackpad pinch |
| Pan | drag, arrow keys, or two-finger scroll |
| Reset view | `Home` |
| Cancel / deselect / close a window | `Esc` |
| Play / pause | `Space` |
| One day | `N` |
| Speed | `1` `2` `3` `4` |
| Needs you, Fleets, Leaders, Missions, Log | `Q` `F` `C` `M` `E` |
| Search | `/` |
| Next map layer | `V` |
| Routes on / off | `L` |
| Sector windows | `S` |
| Goal | `G` |
| Next fleet at this system | `Tab` (Shift+Tab back) |
| Drop the last waypoint | `Backspace` |
| Quick save | `Ctrl+S` / `Cmd+S` |
| How to play | `F1` |

Saves live in Godot's user folder (macOS: `~/Library/Application Support/Godot/app_userdata/Praedra/`, Linux: `~/.local/share/godot/app_userdata/Praedra/`): `save1.json` to `save6.json` and `autosave.json`. Delete `howto_seen.txt` there to see the first-time How to play page again.

## For developers

`Scripts/GameBoot.cs` is the one adapter to the core's setup (new game from `NewGame.Create`, the simple AI as opponent with `AiUsesFog` = true, the victory check).

Fog of war: the screens read other sides only through the core's fog API for the player's side (`host.Fog`: `Picture`, `IsExplored`, `Sees`, `VisibleFleets`, `KnownHq`, `EventsFor`); `GameSession` wraps these (`KnownOwnerOf`, `LastSeenDay`, `MyHqSystem`, `EnemyHqSystem`). Own fleets, systems and the Collective's own HQ are read from the game as before (they are the side's own).

Characters: the screens read the player's own characters from `host.Characters`; enemy leaders only through the fog (`Fog.CharactersSeen`: where and when your side saw them) plus the key leaders the win terms name; character messages come from the fog-filtered feed (`EventsFor`). The five orders (`AttachCharacter`, `DetachCharacter`, `AssignSystemGeneral`, `ReleaseSystemGeneral`, `MoveCharacter`) are submitted with `Game.Submit`; refusals are worded by `GameSession.WhyCharacter`.

Missions: the screens list only the player's own mission groups (`host.Missions.Missions`, own state) and special forces. Which kinds a team can do, whether a target is allowed, the route and the arrival day all come from the core's read-only dry run `MissionModule.CheckStart(StartMissionOrder)` (the same checks as the order handler; it changes nothing and draws nothing). The orders `StartMissionOrder` and `CancelMissionOrder` are submitted with `Game.Submit`; refusals are worded by `GameSession.WhyMission` (every `MissionReject` code). Mission messages come from the fog-filtered feed (`EventsFor`): the mission side's own events, and `AgentDetected` for the defender.

Files: `Main.cs` (screens, command-line modes, headless smoke test), `StartMenu.cs`, `GameBoot.cs` (the adapter to the core's setup and saves), `GameSession.cs` (queued orders tracked, core events turned into plain messages, fog-filtered reads). The game screen is one `GameScreen` class split by area: `GameScreen.cs` (loop, keys, Esc), `.Shell.cs` (the floating pieces: menu, layers, search, pill, clock, stocks, toasts, legend), `.Hud.cs` (the selection card and the message corner), `.Drawer.cs` (the window and its tabs), `.Attention.cs` (Needs you), `.System.cs` (Selected: a system), `.Fleet.cs` (Selected: a fleet, split and merge, attacks), `.Build.cs` (build table, queues, Stop, where research stands), `.Scrap.cs` (scrapping ships, fighters, regiments and buildings), `.Move.cs` (move mode, order bar, traps, waypoints), `.Map.cs` (map layers, sector rows), `.Search.cs`, `.Goal.cs`, `.Log.cs` (log, toasts, unread count), `.Dialogs.cs` (menu, saves, settings, How to play, victory), `.Characters.cs` (Leaders tab), `.Missions.cs` (Missions tab and planner), `.Playtest.cs` (click helpers for the playtest and screenshot options). `Ui/UiKit.cs` is the look (colours, fonts, theme, cards, procedural icons; the fonts are Inter and JetBrains Mono, both SIL OFL, licences in `fonts/`), `Ui/UiSettings.cs` the settings file, `Ui/SectorWindow.cs` the sector table. `GalaxyView.cs` draws the map, `FleetLayer.cs` the fleets, `GalaxyBackdrop.cs` + `GalaxyBackdrop.gdshader` the galaxy picture behind it (procedural, drawn per pixel into a buffer that is redrawn only when the view moves; its arms follow the spiral the systems were laid out on and its light follows where the systems are). Playtests: `Main.Playtest.cs` and `Main.Playtest*.cs` (characters, missions, harvester, routes, screens).

Needs the **Godot 4.7.1 .NET (mono)** editor and the **.NET 8 SDK**. The project is NOT in `Praedra.sln` (CI has no Godot SDK). Data: the csproj copies `data/design/**.csv` and `data/classic/*.csv` to `godot/data/**.txt` (git-ignored) before every build, because Godot drops `.csv` from exports; at start the game copies them to the user folder as `.csv` for the core's `GameData.Load`.

```
cd godot
dotnet build PraedraViewer.csproj -p:RestoreAdditionalProjectSources=/tmp/godot/nupkgs   # or the nupkgs folder of the editor download
godot --headless --path . -- --smoke      # 60 days of a new game with scripted orders, plus a save/load round trip
godot --headless --path . -- --playtest   # plays Collective and Hand games through the real screens (see below); exit code 1 on failure
```

**`--playtest`** starts each game from the start screen and then clicks the real buttons (it raises the same `Pressed` signal a mouse click does; the core is only read to decide where to click): How to play, Play/Pause and speed, select a fleet from the list, Move..., Load/Unload troops, Bombard, Invade, battle popup and log click, retreat switch, split and merge, build a ship and a facility, Save, Load with the confirm dialog, Menu, Continue, fog of war (an enemy fleet the player cannot see is neither drawn nor listed, unexplored systems are drawn dim, enemy fleets cannot be selected, no message about captures the player could not see), the Hand's **Enemy HQ: unknown** button at the start, the Collective's **Move HQ...** (a refused move with its reason, then a move that arrives on the announced day), characters (the Leaders tab shows Nobody and Ledger for the Collective or Cassia Ravilla for the Hand; of the enemy leaders it lists only the key leaders you must capture and those your side has seen, a character moved alone arrives on the announced day, attaching it to a fleet shows it in the fleet panel, a second conflicting attach is refused with a plain reason, detach, naming and releasing a system General), missions (the Missions list by button and key M; Recruitment greyed out with its plain reason; Mae Abara on Diplomacy to a known neutral system with an outcome message by the last expected day; the Missions list showing the trip out with Cancel greyed, then the team at the target; Cancel and the team coming home; Midwife on Espionage from the system panel with its result and the facilities shown; Listeners trained, listed under Special forces and picked for a team; the Hand sends its own spy on Espionage; every mission refusal code has words), a screen tour (`Main.PlaytestScreens.cs`: the drawer tabs and colours, Needs you and Run until something needs me, search, search while moving, the map layers, the sector windows, a map click (the card, Details opens the window, the cross and Esc close it), the menu, a sector on the map (hover names it, click opens its table), the Goal window, a settings switch, Ctrl+S, Esc, C), running to day 400 against the AI, and the victory and defeat screens (forced with the core's debug set-holder order, which is switched on only inside the test). It uses throw-away save files (`playtest_*.json`) so real saves are never touched.

Command-line options after `--` for screenshots and quick looks: `--screenshot out.png [frames]`, `--menu`, `--newgame`, `--side Collective|Hand`, `--preset easy|medium|hard`, `--days N`, `--rules new|classic` (space battle rules, DEFAULT new), `--focus "System" [zoom]`, `--fleet "Name"`, `--moveto "System"` (needs `--fleet`), `--howto`, `--filter All|Battles|"My systems"`, `--zoom Z`, `--characters` (open the Leaders tab), `--click "Button text"` (click a panel button by the start of its text), `--pick "System"` (answer a destination click, e.g. after `--click "Move Nobody..."`), `--hover "System"` (hover a system: the route preview of a move or mission target), `--missions` (open the Missions list), `--open Build|Log|Selected|...` (a drawer tab or a Selected sub-tab), `--layer Support|Trouble|Yards|Intel|Slots`, `--sectors` (open the sector windows), `--sectorhover Sector_Name` (light a sector as if the mouse pointed at it), `--mapclick "System"` (select as a map click: the card, no window), `--close` (close the window), `--gamemenu` (open the menu), `--goal` (open the Goal window), `--trap "System"` (a move whose route crosses a trap), `--preview "System"` (a move preview). Example: `--characters --click "Send Midwife on a mission..." --click Espionage --click "Pick the target system..." --pick Salitre --click "Send: Espionage" --days 12 --missions`.

Screenshot with the software renderer: `xvfb-run -a -s "-screen 0 1440x810x24" godot --path . --rendering-driver opengl3 --resolution 1440x810 -- --screenshot map.png 30 --newgame`.

Export (templates 4.7.1 mono installed): `godot --headless --path . --export-release macOS ../build/mac/Praedra-mac.zip` and `--export-release Linux ../build/linux/Praedra.x86_64`. Builds go under `build/` (git-ignored); never commit them.

**Mac signing.** Godot's own ad-hoc signing from Linux makes an app macOS kills at launch on Apple Silicon ("zsh: killed", seen 2026-10-13), so the macOS preset has `codesign/codesign=0` and **every Mac build must come from `godot/build-mac.sh`** (a plain Godot export is unsigned and will not start on Apple Silicon). The script signs after export with rcodesign (apple-codesign 0.29.0, Paul approved 2026-10-13): unzip the export, `rcodesign sign --entitlements-xml-file mac-entitlements.plist Praedra.app`, then re-zip with `zip -qry` (keeps permissions). Ad-hoc, no hardened runtime; the entitlements let .NET run its JIT. macOS still asks for **Open Anyway** once per download (no notarization without a paid Apple account).
