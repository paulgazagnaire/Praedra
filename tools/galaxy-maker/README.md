# Galaxy Maker

Tunes the galaxy drawn behind the game's map. It is the game's own backdrop shader (`godot/Scripts/GalaxyBackdrop.gdshader`) ported to one WebGL2 page, with every number that shapes the look made a control, plus slow movement and the dither style from the Planet Maker.

## Files
- `index.html`: the whole tool, self-contained. Open it in any browser.
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
- **Looks.** Game today, Deep and quiet, Rich, Ink (black and white dots).
- **Dither.** The same four patterns, four colour sets and dot size as the Planet Maker; one canvas pixel per dot, enlarged without smoothing.
- **Show the 555 systems on top** draws every system as a dot (gold Heartland, blue Midreach, violet Farreach) to check the light falls where the map is.
- **Save image** renders the whole galaxy, 2048 pixels square. **Settings for the game** copies the settings as JSON (`format: praedra-galaxy`, `v: 1`) for the game's backdrop to read, and loads pasted settings back (unknown keys are dropped, numbers clamped to the slider ranges).

## Known limits
- The game does not read these settings yet, and it redraws its backdrop only when the view moves, so it shows no movement. Taking the settings and the movement into the game is Godot work: the shader takes the same uniforms, and the movement needs the backdrop redrawn every frame (or a cheap separate star layer that is).
- The outer halo uses the arm colour (the game has its own fixed blue-grey); the difference is very small.
- Tuned on software rendering only.
