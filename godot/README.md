# Rebellion (playable prototype)

A rough but playable front end for the rules core in `core/`. Pick a side, press Play, give orders to fleets and build on your planets. All rules live in `core/`; this folder only draws the screen, submits orders and calls the day step.

**Test build notes:** the computer opponent is not playing yet (the other side sits still). New games come from the core's `NewGame` (presets Easy / Medium / Hard; Easy exists for the Alliance only, so Empire plus Easy plays the Medium Empire start).

## Run it on the Mac (Apple Silicon or Intel)

1. Download `Rebellion-mac.zip` and double-click it to unzip. You get **Rebellion.app**.
2. The app is not signed by an Apple developer account, so macOS blocks it the first time. Open **Terminal** and paste:
   ```
   xattr -dr com.apple.quarantine ~/Downloads/Rebellion.app
   ```
   (Change the path if the app is not in Downloads.) Then double-click the app.
   Without Terminal: double-click once, close the warning, open **System Settings > Privacy & Security**, scroll down and click **Open Anyway**.
3. Linux / Steam Deck: unzip `Rebellion-linux.zip`, run `linux/Rebellion.x86_64` (keep the `.pck` and the `data_...` folder next to it).

## How to play

- Start screen: choose Alliance or Empire and a difficulty, then **New Game**. **Continue** loads the autosave (written every 30 days), **Load saved game** the one save slot.
- **Space** starts and pauses the clock. Speeds 1x/2x/5x/10x (keys 1-4) are days per second. **N** (or +1 day) advances one day.
- Orders are carried out when the next day starts, so after giving one press Play or +1 day. The top bar tells you how many orders are waiting. If the rules refuse an order, the reason appears in the Messages box.
- The clock stops by itself after a battle, when a system changes hands, when an invasion happens, and on victory.
- Top bar: day, your **Raw** ore, **Refined** material (what you spend), **Upkeep** used/limit (over the limit and units get scrapped), Save, Load, Menu.
- Map colours: orange = Alliance, green = Empire, grey = neutral; the thin ring is the sector; big double ring = headquarters; red ring = uprising; yellow ring = blockade. Fleets are triangles; yours have a white outline. "Your fleets" (top left) lists them.
- Click a **system**: owner, support, garrison, slots, facilities, troops, fleets. On your own systems there is a **Build** menu (facilities at a construction yard, ships at a shipyard, regiments at a training facility) with cost, upkeep and time.
- Click your **fleet**: **Move...** (then click the destination; route and arrival day are shown before you confirm), **Load troops** / **Unload**, **Bombard** and **Invade** (at an enemy or neutral system, troops aboard), retreat-instead-of-fighting switch, split and merge.
- **Messages** (bottom left): click one to jump to its system; battle and invasion lines open a short report.

| What | How |
|---|---|
| Zoom | mouse wheel, `+` / `-`, or trackpad pinch |
| Pan | drag, arrow keys, or two-finger scroll |
| Reset view | `Home` |
| Cancel / deselect / close report | `Esc` |
| Play / pause | `Space` |
| One day | `N` |
| Speed | `1` `2` `3` `4` |

Saves live in Godot's user folder (macOS: `~/Library/Application Support/Godot/app_userdata/Rebellion/`, Linux: `~/.local/share/godot/app_userdata/Rebellion/`): `save1.json` and `autosave.json`.

## For developers

`Scripts/GameBoot.cs` is the one adapter to the unfinished parts of the core: search it for `TODO(lead)`. Today it calls `NewGame.Create`, uses a no-op opponent (`CreateOpponent`; switch to `new SimpleAi(host, side).PlanDay()` when it exists) and reads `host.Victory.Winner`.

Files: `Main.cs` (screens, command-line modes, headless smoke test), `StartMenu.cs`, `GameScreen.cs` (top bar, loop, log, reports), `GameScreen.Panels.cs` (system, fleet, build panels, move mode), `GameSession.cs` (queued orders tracked, core events turned into plain messages), `GalaxyView.cs`, `FleetLayer.cs`.

Needs the **Godot 4.7.1 .NET (mono)** editor and the **.NET 8 SDK**. The project is NOT in `Rebellion.sln` (CI has no Godot SDK). Data: the csproj copies `data/design/**.csv` and `data/interim/*.csv` to `godot/data/**.txt` (git-ignored) before every build, because Godot drops `.csv` from exports; at start the game copies them to the user folder as `.csv` for the core's `GameData.Load`.

```
cd godot
dotnet build RebellionViewer.csproj -p:RestoreAdditionalProjectSources=/tmp/godot/nupkgs   # or the nupkgs folder of the editor download
godot --headless --path . -- --smoke     # 60 days of a new game with scripted orders, plus a save/load round trip
```

Command-line options after `--`: `--screenshot out.png [frames]`, `--menu`, `--side Alliance|Empire`, `--preset easy|medium|hard`, `--days N`, `--focus "System" [zoom]`, `--fleet "Name"`, `--zoom Z`.

Screenshot with the software renderer: `xvfb-run -a -s "-screen 0 1440x810x24" godot --path . --rendering-driver opengl3 --resolution 1440x810 -- --screenshot map.png 30`.

Export (templates 4.7.1 mono installed): `godot --headless --path . --export-release macOS ../build/mac/Rebellion-mac.zip` and `--export-release Linux ../build/linux/Rebellion.x86_64`. Builds go under `build/` (git-ignored); never commit them.
