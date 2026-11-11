# Galaxy Maker

Tunes the galaxy drawn behind the game's map. It is the game's own backdrop shader (`godot/Scripts/GalaxyBackdrop.gdshader`) ported to one WebGL2 page, with every number that shapes the look made a control, plus slow movement and the dither style from the Planet Maker.

## Files
- `index.html`: the whole tool, self-contained, built by `build.py` below (not kept in git). Open it in any browser.
- `galaxy-maker.src.html`: the same page with a placeholder where the systems go. Edit this one, never `index.html`.
- `build.py`: puts the 555 systems' map positions (from `data/design/extended-galaxy-v6-planets.csv`, each sector pulled 0.7 toward its centre as `GalaxyView.SectorPull` does) into the page.

## Rebuild
```
python3 build.py galaxy-maker.src.html ../../data/design/extended-galaxy-v6-planets.csv index.html
```

## What it does
- **Same galaxy as the game.** Two log-spiral arms (start 67°, pitch 17°, radius 480 on the 1000 by 1000 map, Charter at the centre), light weighted by a 128 by 128 density map of where the systems are, built the same way as `GalaxyBackdrop.DensityMap`. The "Game today" look and Reset give exactly the game's numbers and colours.
- **No turning.** The view only pans and zooms (drag, wheel, pinch). Movement is local and slow: gas drifts outward along the arms (Gas drift), the pink star-forming knots breathe (Nebulae breathe), and a share of the stars twinkle: a slow breath plus a rare short flare, each star on its own clock (Twinkle strength, Stars that twinkle, Twinkle pace). It redraws at 30 frames a second at most.
- **Controls.** Shape (arm start, pitch, width, raggedness, disc size), Light (brightness, glow, core light and size, disc, arms, how much the systems shape the light), Gas and dust (star clouds, pink nebulae, dust lanes, filaments, lane offset), Stars (brightness, density, foreground stars), Movement, Colours (core, disc, arms, nebulae, dust, space).
- **Depth** (all off by default, so old settings draw exactly as before).
  - *Parallax*: the star layers sit a little above and below the disc, the foreground stars and the drifting motes well in front, the distant stars behind; they slide and grow at different rates when the view pans or zooms (`layerQ`, needs the view's zoom in `uZoom`).
  - *Distant stars*, *Drifting motes* (big soft out-of-focus specks, each on its own slow clock) and *Mote size*.
  - *Relief*, *Contour lines* and *Mesh* are all drawn from one height (`heightAt`: the galaxy's smooth light, core as the peak, arms as ridges, drifting with the gas). Relief lights the slopes facing *Light from* (an angle on the screen, 0° right, 90° below, 225° upper left); contour lines are one line per step of height (*Contour count*); the mesh is a square grid pushed up the screen by the height (*Mesh squares*, *Mesh height*). Lines use the *Lines* colour.
- **Movement, more of it, each with its own amount.** *Overall pace* multiplies the clock (0 stops everything, the game must scale its time the same way); *Arm waves* and *Wave pace* send slow bright waves out along the arms; *Core pulse* swells the core. *Brightness ceiling* (Light) caps how bright the backdrop can ever get, so it cannot outshine the map on top.
- **Glitch** (inside the dither; button on the picture or the checkbox). A glitch is a patch of square tiles where the dither switches to another pattern: slanted lines, nested squares, big dot, chunky grid, crosses, chevrons, bars. Tiles are locked to the map in whole dots. Controls: *Amount* (share of the picture, 0% is exactly the plain dither), *Patch size*, *Tile size* (8 to 32 dots), *Double-size tiles*, *Patterns in play*, *Flat tiles* (share of tiles that take one tone for the whole square), *Change pace* (0 freezes the patches) and *Shuffle the patches* (seed). In the settings these are the `g...` keys of `dither`.
- **Looks.** Game today (read from `godot/ui/galaxy-backdrop.json` at build time; Reset goes there too), Depth and glitch (game today with the new parts switched on), Deep and quiet, Rich, Ink (black and white dots). Two more dither colour sets: Blue and Orange on grey.
- **Dither.** The same four patterns, four colour sets and dot size as the Planet Maker; one canvas pixel per dot, enlarged without smoothing.
- **Show the 555 systems on top** draws every system as a dot (gold Heartland, blue Midreach, violet Farreach) to check the light falls where the map is.
- **Save image** renders the whole galaxy, 2048 pixels square. **Settings for the game** copies the settings as JSON (`format: praedra-galaxy`, `v: 1`) for the game's backdrop to read, and loads pasted settings back (unknown keys are dropped, numbers clamped to the slider ranges).

## Known limits
- The game reads these settings from `godot/ui/galaxy-backdrop.json` (`GalaxyLook.cs`) and redraws the backdrop over time when any movement is on. Paste new settings there to change the game's galaxy.
- The depth, movement and glitch parts and the Blue and Orange colour sets added in November 2026 are in the game too (ported 2026-11-11). The game works out the view's zoom, centre and scale from its own pan and zoom, and runs its clock at *Overall pace*.
- Glitch tiles are a whole number of dots, so they stay put when the view pans but reshuffle inside their patches when it zooms; the patches themselves stay on the same part of the galaxy.
- The dither code is copied three times: here, in the Planet Maker and in the game (`GalaxyLook.BlueNoise`, `GalaxyBackdrop.gdshader`). Change one, change all three.
- Tuned on software rendering only.
