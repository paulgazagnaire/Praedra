# Planet Maker

Draws planets purely in code (one WebGL2 fragment shader, no images, no libraries) and animates them. Built for Praedra system illustrations.

## Files
- `index.html` — the whole tool, self-contained. Open it in any browser.
- `planet-maker.src.html` — the same page with a placeholder where the system list goes.
- `systems.json` — the 555 systems (name, sector, world, from-space, moons) read from `specs/creative/07-systems.md`.
- `build.py` / `parse.py` — rebuild `index.html` from the two files above.
- `presets/` — named presets exported from the tool (`<name>.planet.json`), one per system once the team has saved them.

## Rebuild
```
python3 build.py planet-maker.src.html systems.json index.html
```
Edit `planet-maker.src.html` (the shader and the page), never `index.html`. `parse.py <07-systems.md> systems.json` rereads the system list from the lore sheet.

## Preset format
```json
{ "format": "praedra-planet", "v": 1, "name": "Scoria",
  "params": {
    "kind": "rock",            // terran ocean swamp desert ice rock lava city locked gas
    "seed": 4821,              // changes continents, craters, storms, ring bands
    "pal": ["#sea","#low","#mid","#high","#ice|storm|lava glow"],
    "hazeCol": "#aab4c4",
    "sea": 0..1, "wet": -1..1, "mount": 0..1, "scale": 0.5..2.5, "ice": 0..1, "clouds": 0..1, "haze": 0..1.3,
    "cities": 0..1, "lava": 0..1, "craters": 0..1, "scars": 0..1,
    "bands": 3..20, "turb": 0..1.4, "storms": 0..6, "stormSize": 0.2..1.6,
    "spin": -1..1, "cloudSpeed": -1..1, "flow": 0..1.5, "moonSpeed": 0..2,
    "tilt": 6..85, "roll": -90..90, "sun": -180..180, "sunH": -60..60, "zoom": 0.5..1.8,
    "ring": { "on": false, "inner": 1.35, "width": 0.8, "op": 0.55, "gaps": 0.5, "spin": 0.3, "c1": "#hex", "c2": "#hex" },
    "moons": 0..5 } }
```
Speeds: rotation = `spin * 0.5` radians per second (0.25 is one turn in about 50 s).

Pasted presets and library entries are checked on load: numbers are clamped to their slider range (moons 0 to 5), bad colours are replaced, and a short palette is filled from the world type's defaults.

## Using it in Godot later
The shader is GLSL ES 3.00 with no tricks. Porting to a Godot `.gdshader` is a mechanical rename (`uniform` blocks stay, `fragColor` becomes `COLOR`). The preset values map one-to-one onto the shader uniforms, so the game can draw the same planet from the same JSON.

## Detail switch
The page has a Detail: High / Fast button (not part of a preset). High uses more noise layers, cloud shadows and a third crater size; Fast drops them for slower graphics cards.

## Inhabited worlds (cities slider)
Every world with Cities above zero gets one population density field, and both sides of the planet draw from it, so a place that glows at night is built up by day. People settle coasts first (and basins on worlds with no sea), then lowlands with a mild climate and some water; hot, dry, cold or high land stays empty. The slider sets how far they spread: low = a few coastal towns, high = most of the land, 0.7 and up = a whole built-up planet (hubs and highways everywhere).
- Night: dense lights in the cores, a dim glow and scattered villages in the farmland around them, planned highways, avenues and square or pentagon hubs inside built-up land.
- Day: grey paved districts with dark streets and hub outlines, farmland patchwork around them (irrigation discs on dry worlds), none on airless rock.
Seed changes the layout. The look is fixed by the shader, not by a preset value beyond `cities`.

## Clouds
The cloud layout is modelled on Earth's satellite cloud climatology, so a planet with the Clouds slider near the middle shows roughly Earth's mix of clear and cloudy sky (checked by counting covered pixels per latitude on a debug render):
- **Tropics:** a ragged storm line along the equator, broken into clusters, not a continuous belt.
- **Subtropics:** the driest belts, with thin low cloud: small heaped cells that gather in patches.
- **Mid latitudes:** the heaviest cover, in storm tracks. Waves in the jet stream (3 to 5 around the planet, random per seed) bend the cloud into separate frontal systems; fronts lean the same way in both hemispheres (mirrored), with feathery streaks drawn out along the flow and cyclones with spiral arms at the sharp bends.
- **Land and sea:** wet land and open sea carry more cloud, deserts stay clearer, so the cloud follows the planet's own climate.

A second, high deck of thin ice cirrus sits above: patchy, combed into fine streaks along the jet streams, see-through, drifting faster than the low deck, shifted against it by height, and still lit after the ground has gone dark at dusk (pink-gold at the day-night line). The shader is compiled in variants, so work a planet does not use costs nothing: gas giants and worlds with Clouds at 0 skip all cloud code, and Fast detail also skips the high cirrus deck, small cells, streaks, relief and ground shadows and uses fewer cyclones. The Clouds slider sets overall cover, Cloud drift moves the whole pattern. Gas giants have their own bands and storms and are not affected. On software rendering, a cloudy world in High costs about 40 to 60 percent more than before clouds were added, Fast about 10 percent more, and worlds without clouds nothing; a real graphics card has not been measured.

## Known limits
- The system presets are a first guess read from each description (colours, moons, rings). Expect to adjust some by hand.
- The whole planet is one pass; moons do not cast shadows on the planet, and rings do not light moons.
- Ring and moon draw order is approximate when a moon passes behind a ring.
