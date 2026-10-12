# Rebellion (playable prototype)

A rough but playable front end for the rules core in `core/`. Pick a side, press Play, give orders to fleets and build on your planets. All rules live in `core/`; this folder only draws the screen, submits orders and calls the day step. The computer plays the other side (the core's simple AI). New games use the core's 360-system start.

## Launch it on the Mac (Apple Silicon or Intel)

1. Download `Rebellion-mac.zip` and double-click it to unzip. You get **Rebellion.app**. Drag it to Applications (or leave it in Downloads).
2. The app is not signed by an Apple developer account, so macOS blocks it the first time. Pick one way:
   - **Terminal:** open Terminal and paste (change the path if the app is elsewhere):
     ```
     xattr -dr com.apple.quarantine ~/Downloads/Rebellion.app
     ```
     then double-click the app.
   - **No Terminal:** double-click the app once, close the warning, open **System Settings > Privacy & Security**, scroll down and click **Open Anyway**, then confirm.
3. Linux / Steam Deck: unzip `Rebellion-linux.zip`, run `linux/Rebellion.x86_64` (keep the `.pck` and the `data_...` folder next to it).

The first time you press **New Game**, a **How to play** page opens. You can open it again any time with the **How to play** button (top bar) or **F1**.

## How to play (short)

- **Goal.** Empire: capture the Rebel headquarters system. Alliance: capture Coruscant. You also win when the other side has no systems left. Your headquarters and the enemy capital carry a tag on the map; the **My HQ** and **Enemy HQ** buttons (top left) jump there.
- **Time.** **Play** (or Space) runs the clock at 5x by default; Space again pauses. Speed buttons or keys 1-4 change it (1x, 2x, 5x, 10x days per second). **+1 day** (or N) moves one day. The clock stops by itself after a battle, when a system changes hands, when an invasion happens, and on victory.
- **Orders start when the next day starts.** The top bar says how many orders are waiting. If the rules refuse an order, the reason appears in Messages.
- **Move a fleet.** Click one of your fleets (white outline) or its name in "Your fleets", press **Move...**, then click the destination. The white line shows the route and the arrival day. Once you confirm, the planned route stays drawn and the fleet list says "moving to X, arrives day N".
- **Invade.** Select a fleet at one of your planets that has regiments, press **Load troops** (the fleet needs troop room: transports and some warships have it; the button keeps the planet's garrison safe, a second button loads everything). Fly to the enemy or neutral system. Enemy fleets there fight you first. Optional: **Bombard** (military, civilian or everything). Press **Invade**. A fleet can carry out one attack order at a time, so bombard, press +1 day, then invade.
- **Build.** Click one of your planets. The **Build** menu has collapsible sections **Facilities**, **Ships**, **Fighters**, **Troops** (click a heading). Mines and refineries come first. Ships need a Shipyard on that planet, troops a Training facility, buildings a Construction yard and a free slot.
- **Money.** **Raw** is ore from mines. Refineries turn it into **Refined**, which you spend. **Upkeep** (used/limit) is what everything you own costs; the limit is 50 for each mine-and-refinery pair; over the limit, units get scrapped. Hover over the numbers in the top bar for a reminder.
- **Order on planets.** A planet needs enough regiments (garrison) or it revolts (red ring): yards and mines stop.
- **Messages** (bottom left): filter **All / Battles / My systems**; click a line to jump to its system; battle and invasion lines open a report. A battle report also pops up by itself when a battle is fought.
- **Save / Load.** **Save** writes the one save slot. **Load** and **Menu** ask before they replace the running game. The game autosaves every 30 days; **Continue** on the start screen loads that.
- Map colours: orange = Alliance, green = Empire, grey = nobody; thin ring = sector; double ring = headquarters; red ring = uprising; yellow ring = blockade. Fleets are triangles, stacked in a column at a system with one name each.

| What | How |
|---|---|
| Zoom | mouse wheel, `+` / `-`, or trackpad pinch |
| Pan | drag, arrow keys, or two-finger scroll |
| Reset view | `Home` |
| Cancel / deselect / close a window | `Esc` |
| Play / pause | `Space` |
| One day | `N` |
| Speed | `1` `2` `3` `4` |
| How to play | `F1` |

Saves live in Godot's user folder (macOS: `~/Library/Application Support/Godot/app_userdata/Rebellion/`, Linux: `~/.local/share/godot/app_userdata/Rebellion/`): `save1.json` and `autosave.json`. Delete `howto_seen.txt` there to see the first-time How to play page again.

## For developers

`Scripts/GameBoot.cs` is the one adapter to the core's setup (new game from `NewGame.Create`, the simple AI as opponent, the victory check).

Files: `Main.cs` (screens, command-line modes, headless smoke test), `Main.Playtest.cs` (the headless playtest), `StartMenu.cs`, `GameScreen.cs` (top bar, loop, log, overlays), `GameScreen.Panels.cs` (system, fleet, build panels, move mode), `GameScreen.Playtest.cs` (click helpers for the playtest and screenshot options), `GameSession.cs` (queued orders tracked, core events turned into plain messages), `GalaxyView.cs`, `FleetLayer.cs`.

Needs the **Godot 4.7.1 .NET (mono)** editor and the **.NET 8 SDK**. The project is NOT in `Rebellion.sln` (CI has no Godot SDK). Data: the csproj copies `data/design/**.csv` and `data/interim/*.csv` to `godot/data/**.txt` (git-ignored) before every build, because Godot drops `.csv` from exports; at start the game copies them to the user folder as `.csv` for the core's `GameData.Load`.

```
cd godot
dotnet build RebellionViewer.csproj -p:RestoreAdditionalProjectSources=/tmp/godot/nupkgs   # or the nupkgs folder of the editor download
godot --headless --path . -- --smoke      # 60 days of a new game with scripted orders, plus a save/load round trip
godot --headless --path . -- --playtest   # plays Alliance and Empire games through the real screens (see below); exit code 1 on failure
```

**`--playtest`** starts each game from the start screen and then clicks the real buttons (it raises the same `Pressed` signal a mouse click does; the core is only read to decide where to click): How to play, Play/Pause and speed, select a fleet from the list, Move..., Load/Unload troops, Bombard, Invade, battle popup and log click, retreat switch, split and merge, build a ship and a facility, Save, Load with the confirm dialog, Menu, Continue, running to day 400 against the AI, and the victory and defeat screens (forced with the core's debug set-holder order, which is switched on only inside the test). It uses throw-away save files (`playtest_*.json`) so real saves are never touched.

Command-line options after `--` for screenshots and quick looks: `--screenshot out.png [frames]`, `--menu`, `--newgame`, `--side Alliance|Empire`, `--preset easy|medium|hard`, `--days N`, `--focus "System" [zoom]`, `--fleet "Name"`, `--moveto "System"` (needs `--fleet`), `--open Ships|Fighters|Troops|Facilities`, `--howto`, `--filter All|Battles|"My systems"`, `--zoom Z`.

Screenshot with the software renderer: `xvfb-run -a -s "-screen 0 1440x810x24" godot --path . --rendering-driver opengl3 --resolution 1440x810 -- --screenshot map.png 30 --newgame`.

Export (templates 4.7.1 mono installed): `godot --headless --path . --export-release macOS ../build/mac/Rebellion-mac.zip` and `--export-release Linux ../build/linux/Rebellion.x86_64`. Builds go under `build/` (git-ignored); never commit them.
