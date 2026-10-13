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

- **Goal.** Empire: capture the Rebel headquarters system. Alliance: capture Coruscant. You also win when the other side has no systems left. Your headquarters and the enemy capital carry a tag on the map; the **My HQ** and **Enemy HQ** buttons (top left) jump there. The Empire does not know where the Rebel HQ is: until its picture of the galaxy shows it, there is no tag and the button says **Enemy HQ: unknown**.
- **Fog of war.** You see live only the systems you hold or have a fleet at. Other systems show what your side saw last (the system panel says "Last seen day N"); dim hollow dots are unexplored. Enemy fleets are drawn only where you can see them (as class counts, without names), and Messages only tell you what your side could know. The computer opponent plays by the same rule.
- **Move the Rebel HQ (Alliance).** Click your HQ system (or **My HQ**), press **Move HQ...**, then click one of your systems; the gold line shows the route and the arrival day. The HQ travels on the lanes for some days (it cannot be attacked on the way) and the top-left line says "HQ moving to X, arrives day N". The move is refused (with the reason in Messages) if the destination is not yours or a large enemy blockade pins the HQ.
- **Characters.** Press **Characters** (top bar, or **C**) for your leaders: where they are (a system, aboard a fleet, or travelling with the arrival day), their post, status (free, injured, captured, dead) and skills (leadership; the Force rank once it is known). Leaders give bonuses: a fleet's **Admiral** multiplies its bombardment, a fleet's **General** helps its regiments in an invasion, a system's **General** helps the defenders (Commanders have no effect yet). Select a fleet to attach a character standing at the same system (Admiral, Commander, General or passenger) or detach one; select one of your systems to name or release its General. **Move NAME...** sends a character alone like a fleet move (route and arrival day shown first). Enemy leaders are not listed: your side cannot see where they are.
- **Missions.** Press **Send NAME on a mission...** in the Characters list (or **Send a mission from X...** on the panel of the system where your agents stand). Tick the team (characters and special forces units standing at that system), pick the kind (kinds this team cannot do are greyed out with the reason, e.g. "Recruitment - not available yet"), press **Pick the target system...** and click the target: the purple line shows the route and the banner the arrival day. **Send** queues it. The **Missions** list (top bar, or **M**) shows each mission's team, target, phase (travelling out, at the target with the next result day, returning) and has a **Cancel** button (once the team is at the target). Results come in Messages: "Espionage on X succeeded: you now see its facilities, troops and fleets", failed, foiled (the enemy caught the team), and "Enemy agents detected at X" when your forces catch theirs. Special forces you train (Build, Troops) are listed on their system's panel.
- **Which missions are worth it now.** **Espionage** (see what an enemy system holds; Han Solo and Bothan Spies are good at it) and **Diplomacy** (Mon Mothma or Leia raise a neutral system's support before you take it; it keeps working until you cancel it). **Sabotage** is very hard with the current interim success tables: the team is usually caught. Decoys, Recruitment, Jedi training and Death Star sabotage are not available yet (the rule book does not cover them); Abduction and Assassination are not offered because your side cannot see where enemy leaders are.
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
| Characters list | `C` |
| Missions list | `M` |
| How to play | `F1` |

Saves live in Godot's user folder (macOS: `~/Library/Application Support/Godot/app_userdata/Rebellion/`, Linux: `~/.local/share/godot/app_userdata/Rebellion/`): `save1.json` and `autosave.json`. Delete `howto_seen.txt` there to see the first-time How to play page again.

## For developers

`Scripts/GameBoot.cs` is the one adapter to the core's setup (new game from `NewGame.Create`, the simple AI as opponent with `AiUsesFog` = true, the victory check).

Fog of war: the screens read other sides only through the core's fog API for the player's side (`host.Fog`: `Picture`, `IsExplored`, `Sees`, `VisibleFleets`, `KnownHq`, `EventsFor`); `GameSession` wraps these (`KnownOwnerOf`, `LastSeenDay`, `MyHqSystem`, `EnemyHqSystem`). Own fleets, systems and the Alliance's own HQ are read from the game as before (they are the side's own).

Characters: the screens read only the player's own characters from `host.Characters` (the fog API has no picture of where enemy leaders are, so none are shown); character messages come from the fog-filtered feed (`EventsFor`). The five orders (`AttachCharacter`, `DetachCharacter`, `AssignSystemGeneral`, `ReleaseSystemGeneral`, `MoveCharacter`) are submitted with `Game.Submit`; refusals are worded by `GameSession.WhyCharacter`.

Missions: the screens list only the player's own mission groups (`host.Missions.Missions`, own state) and special forces. Which kinds a team can do, whether a target is allowed, the route and the arrival day all come from the core's read-only dry run `MissionModule.CheckStart(StartMissionOrder)` (the same checks as the order handler; it changes nothing and draws nothing). The orders `StartMissionOrder` and `CancelMissionOrder` are submitted with `Game.Submit`; refusals are worded by `GameSession.WhyMission` (every `MissionReject` code). Mission messages come from the fog-filtered feed (`EventsFor`): the mission side's own events, and `AgentDetected` for the defender.

Files: `Main.cs` (screens, command-line modes, headless smoke test), `Main.Playtest.cs` (the headless playtest), `StartMenu.cs`, `GameScreen.cs` (top bar, loop, log, overlays), `GameScreen.Panels.cs` (system, fleet, build panels, move mode), `GameScreen.Characters.cs` (Characters list, fleet officers, system General, moving a character alone), `GameScreen.Missions.cs` (the mission planner, the Missions list, special forces on the system panel), `GameSession.Missions.cs` (mission words, refusals and messages), `Main.PlaytestCharacters.cs` and `Main.PlaytestMissions.cs` (the character and mission parts of the playtest), `GameScreen.Playtest.cs` (click helpers for the playtest and screenshot options), `GameSession.cs` (queued orders tracked, core events turned into plain messages), `GalaxyView.cs`, `FleetLayer.cs`.

Needs the **Godot 4.7.1 .NET (mono)** editor and the **.NET 8 SDK**. The project is NOT in `Rebellion.sln` (CI has no Godot SDK). Data: the csproj copies `data/design/**.csv` and `data/interim/*.csv` to `godot/data/**.txt` (git-ignored) before every build, because Godot drops `.csv` from exports; at start the game copies them to the user folder as `.csv` for the core's `GameData.Load`.

```
cd godot
dotnet build RebellionViewer.csproj -p:RestoreAdditionalProjectSources=/tmp/godot/nupkgs   # or the nupkgs folder of the editor download
godot --headless --path . -- --smoke      # 60 days of a new game with scripted orders, plus a save/load round trip
godot --headless --path . -- --playtest   # plays Alliance and Empire games through the real screens (see below); exit code 1 on failure
```

**`--playtest`** starts each game from the start screen and then clicks the real buttons (it raises the same `Pressed` signal a mouse click does; the core is only read to decide where to click): How to play, Play/Pause and speed, select a fleet from the list, Move..., Load/Unload troops, Bombard, Invade, battle popup and log click, retreat switch, split and merge, build a ship and a facility, Save, Load with the confirm dialog, Menu, Continue, fog of war (an enemy fleet the player cannot see is neither drawn nor listed, unexplored systems are drawn dim, enemy fleets cannot be selected, no message about captures the player could not see), the Empire's **Enemy HQ: unknown** button at the start, the Alliance's **Move HQ...** (a refused move with its reason, then a move that arrives on the announced day), characters (the Characters list shows Luke and Leia for the Alliance or Vader for the Empire and no enemy leader, a character moved alone arrives on the announced day, attaching it to a fleet shows it in the fleet panel, a second conflicting attach is refused with a plain reason, detach, naming and releasing a system General), missions (the Missions list by button and key M; Recruitment greyed out with its plain reason; Mon Mothma on Diplomacy to a known neutral system with an outcome message by the last expected day; the Missions list showing the trip out with Cancel greyed, then the team at the target; Cancel and the team coming home; Han Solo on Espionage from the system panel with its result and the facilities shown; Bothan Spies trained, listed under Special forces and picked for a team; the Empire sends its own spy on Espionage; every mission refusal code has words), running to day 400 against the AI, and the victory and defeat screens (forced with the core's debug set-holder order, which is switched on only inside the test). It uses throw-away save files (`playtest_*.json`) so real saves are never touched.

Command-line options after `--` for screenshots and quick looks: `--screenshot out.png [frames]`, `--menu`, `--newgame`, `--side Alliance|Empire`, `--preset easy|medium|hard`, `--days N`, `--focus "System" [zoom]`, `--fleet "Name"`, `--moveto "System"` (needs `--fleet`), `--open Ships|Fighters|Troops|Facilities`, `--howto`, `--filter All|Battles|"My systems"`, `--zoom Z`, `--characters` (open the Characters list), `--click "Button text"` (click a panel button by the start of its text), `--pick "System"` (answer a destination click, e.g. after `--click "Move Luke Skywalker..."`), `--hover "System"` (hover a system: the route preview of a move or mission target), `--missions` (open the Missions list). Example: `--characters --click "Send Han Solo on a mission..." --click Espionage --click "Pick the target system..." --pick Bandomeer --click "Send: Espionage" --days 12 --missions`.

Screenshot with the software renderer: `xvfb-run -a -s "-screen 0 1440x810x24" godot --path . --rendering-driver opengl3 --resolution 1440x810 -- --screenshot map.png 30 --newgame`.

Export (templates 4.7.1 mono installed): `godot --headless --path . --export-release macOS ../build/mac/Rebellion-mac.zip` and `--export-release Linux ../build/linux/Rebellion.x86_64`. Builds go under `build/` (git-ignored); never commit them.

**Mac signing.** Godot's own ad-hoc signing from Linux makes an app macOS kills at launch on Apple Silicon ("zsh: killed", seen 2026-10-13), so the macOS preset has `codesign/codesign=0` and the app is signed after export with rcodesign (apple-codesign 0.29.0, Paul approved 2026-10-13): unzip the export, `rcodesign sign --entitlements-xml-file mac-entitlements.plist Rebellion.app`, then re-zip with `zip -qry` (keeps permissions). Ad-hoc, no hardened runtime; the entitlements let .NET run its JIT. macOS still asks for **Open Anyway** once per download (no notarization without a paid Apple account).
