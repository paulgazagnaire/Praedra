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
    "ring": { "on": false, "type": "broad|narrow|dusty|arcs|wreck|moonfed", "inner": 1.1..2.8, "width": 0.04..2.6, "op": 0.1..1, "gaps": 0..1,
              "struct": 0..1, "clump": 0..1, "dust": 0..1, "feeder": 0..5, "seed": 0..9999, "spin": -1..1, "c1": "#hex", "c2": "#hex" },
    "moons": 0..5,
    "moonList": [ { "type": "rock", "size": 0.04..0.5, "orbit": 1.3..7, "speed": -2..3, "tilt": -60..60, "phase": 0..360,
                    "detail": 0..1, "feat": 0..1, "haze": 0..1, "shape": 0..1, "spin": -1..1, "seed": 0..9999,
                    "c1": "#hex", "c2": "#hex", "c3": "#hex" } ] } }
```
`moonList` holds the moons in order (the `moons` slider shows the first n). A preset without it still loads: the moons are generated from the seed and the world type.
Speeds: rotation = `spin * 0.5` radians per second (0.25 is one turn in about 50 s).

Pasted presets and library entries are checked on load: numbers are clamped to their slider range (moons 0 to 5, each moon's numbers and colours the same way, an unknown moon type becomes cratered rock, an unknown ring type becomes broad icy rings; presets saved before ring kinds existed load as broad icy rings with their old size and colours), bad colours are replaced, and a short palette is filled from the world type's defaults.

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

**Cloud shapes.** Cloud edges fade over a wide band instead of a sharp one, so thin cloud stays see-through and thick cloud keeps its own texture: bright cores, grey gaps, curled edges. Before, a cloud went fully opaque almost at once and every mass read as a flat white blob. Cloud brightness now follows thickness more strongly, and the cover threshold was lowered a little so the amount of cloud stays about the same.

A second, high deck of thin ice cirrus sits above: patchy, combed into fine streaks along the jet streams, see-through, drifting faster than the low deck, shifted against it by height, and still lit after the ground has gone dark at dusk (pink-gold at the day-night line). The shader is compiled in variants, so work a planet does not use costs nothing: gas giants and worlds with Clouds at 0 skip all cloud code, and Fast detail also skips the high cirrus deck, small cells, streaks, relief and ground shadows and uses fewer cyclones. The Clouds slider sets overall cover, Cloud drift moves the whole pattern. Gas giants have their own bands and storms and are not affected. On software rendering, a cloudy world in High costs about 40 to 60 percent more than before clouds were added, Fast about 10 percent more, and worlds without clouds nothing; a real graphics card has not been measured.

## Moons
Moons are no longer one kind. There are eight, each modelled on something that exists. The surveyed inventory below says what was left out and why.

| Kind | Real examples | What defines the look |
|---|---|---|
| Cratered rock | Earth's Moon, Mercury | pale highlands, craters, dark plains in the big basins |
| Dark ice-rock | Callisto, Iapetus, Rhea | very dark old crust, bright ice splashed out of craters, one face darker than the other |
| Smooth ice | Europa, Enceladus | bright, nearly no craters, long dark cracks, mottled chaos terrain, ice sheen |
| Volcanic | Io | sulphur yellows and oranges, frost, dark calderas, vents that glow on the night side |
| Hazy giant | Titan | thick orange smog over the ground, soft bands, bright atmosphere rim |
| Frozen plains | Pluto, Triton, Charon | cream nitrogen ice, dark red equatorial band, smooth bright lobe, bright poles |
| Captured rock | Phobos, Deimos, Amalthea | small, knobbly (not round), dark, deeply cratered, parallel grooves |
| Living moon | none known; thought possible around a giant planet | kept plain on purpose: ocean, a few big continents, polar ice, a thin atmosphere; cloud wisps off until turned up |

Left out on purpose (real, but they would read as one of the above at this size): Ganymede (ice-rock with a Callisto look and grooved terrain), Mimas and other small icy craterlets (cratered rock with a huge crater), Hyperion (a sponge-textured captured rock), Miranda (patchwork of terrains), Moon-sized dwarf planets (frozen plains), irregular outer moons (captured rock). Not drawn at all: moonlet swarms, ring-moon pairs, binary moons, co-orbitals, moons with their own rings.

**Editor.** In the Moons section: How many (0 to 5), pick which moon, pick its kind, then every setting for that moon: Size, Distance, Orbit speed (negative runs backwards), Orbit tilt, Start position, Detail, Feature, Atmosphere, Lumpy shape, Own spin (0 keeps one face to the planet), three colours. The Detail and Feature sliders are renamed per kind (for Smooth ice they read Mottling and Cracks, for Volcanic Vents and Eruptions). New look rerolls the moon's pattern, Reset to kind restores that kind's defaults. Lowering How many keeps your edits to the hidden moons.

**System presets.** Each system's moon text picks the first moon's kind when it says something ("white", "icy", "volcanic", "hazy", "captured", "black", "dark", "quarried"…); every other moon is drawn from the seed, with more rocky and captured moons on rocky worlds and more ice-rock and hazy moons around gas giants.

**Shadows.** Each moon casts a soft shadow on the planet when it passes between the planet and the sun: a dark spot that crosses the surface as the moon orbits. Shadows are drawn on the planet's sunlit side only, and a moon shadows the planet even while it is behind it as seen from the camera.

Moons are shaded once and only compiled into the shader when a planet has at least one, so a world with no moons costs nothing extra.

## Rings
Rings are no longer one flat band with noise. There are six kinds, each modelled on a ring that exists, and they share what makes real rings: grains in a thin flat sheet that sit inside the distance where tides would pull a moon apart (about 2.4 planet radii for ice), so moons start outside them, and gaps and edges that moons cut.

| Kind | Real example | What defines the look | What the sliders mean |
|---|---|---|---|
| Broad icy rings | Saturn | dim see-through inner ring (C), dense bright middle (B), paler outer (A), the Cassini Division and a narrow Encke-type gap, ringlet banding, dark spokes in the B ring | Gaps, Ringlets, Dark spokes |
| Narrow dark rings | Uranus | 3 to 10 thin, sharp-edged, soot-dark rings with clean space between; the outer one wider and brighter and a little uneven | Number of rings |
| Faint dust rings | Jupiter | main band with a halo inside and a soft gossamer tail outside, dust bunched along the orbit of its source moon; dull with the sun behind you, glowing with the sun behind the planet | Halo and tail, Dust-source moon |
| Clumpy arcs | Neptune | a thin ring that is only thick in 1 to 5 bright arcs bunched in one stretch, with a shepherd moon just inside it | Number of arcs, Arc contrast, Shepherd moon |
| Fresh wreckage | a moon torn apart or smashed | dark lumpy rock, a heavy bulge where the biggest piece still is, and a spiral tail behind it because inner grains orbit faster than outer ones; it follows its remnant moon | Gaps, Grain, Bulge and tail, Remnant moon |
| Moon-fed dust | Saturn's E ring | very wide, thin, white-blue, thickest on the orbit of the geyser moon that sprays it, thinning outward; scaled in so the planet stays big (the real one reaches 3 to 8 planet radii) | Reach, Geyser moon |

Left out on purpose: Saturn's F ring (braided and kinked, it would read as Narrow dark rings), the tilted and warped inner halo of Jupiter, rings around moons, and Saturn-style propeller moonlets and density waves (too fine for this size). Not drawn at all: ring thickness seen edge-on, grains rolling across the ring in motion.

**How rings and moons act on each other.**
- A moon whose orbit crosses a broad ring or a wreckage ring clears a lane there; the lane gets wider with the moon's size.
- A moon outside the ring clears gaps where a grain would circle 2, 3/2 or 3 times for each orbit of the moon (at 0.63, 0.76 and 0.48 of the moon's distance). That is how real moons carve the Cassini Division and the gaps near it. Move a moon with the Distance slider and the gap moves with it. A broad ring keeps a Cassini-like and an Encke-like gap even without moons, unless a moon has already cut one there.
- Dusty, arc, wreckage and moon-fed rings need a moon that belongs to them (the Source moon slider, 1 to 5; "none" leaves the ring as a free-floating one). Picking such a kind adds a moon if there is none, puts it where that kind needs it (inside a dusty or wreckage ring, just inside the arcs, in the thick part of the moon-fed dust), flattens its orbit into the ring plane and makes it a small captured rock or a bright ice moon. After that you can move it: the ring bunches up, or the bulge and tail follow it, wherever it goes and wherever it is on its orbit.
- Picking any ring kind moves other moons out of the ring so they do not sit inside it by accident; drag a moon back in on purpose to cut a lane.

**Light.** With the sun on your side the rings reflect (dense ice is bright, dust is dull). With the sun behind the rings, dense rings turn dark and thin or dusty ones glow by forward scattering; the Backlit glow slider sets how much. Rings cast a shadow on the planet (thin narrow rings leave a pattern of fine lines) and the planet casts a soft shadow on the rings.

**Editor.** In the Rings section: tick "This planet has rings", pick a kind, then Distance, Width, Thickness and the kind's own sliders (renamed per kind; those that do not apply are hidden), plus two colours. New look rerolls the pattern and nudges the colours, Reset to kind restores the kind's defaults.

**System presets.** A system's text picks the kind: "debris", "rock", "hull", "hauler" or "broken" give wreckage, "dust", "faint" or "thin" give faint dust rings, "dark" gives narrow dark rings, anything else broad icy rings; "wide" and "vast" widen it, "faint" and "heavy" change the thickness. Ten systems have a ring of their own.

Rings are only compiled into the shader when a planet has them. On software rendering a ringed planet costs about the same per frame as before; a real graphics card has not been measured.

## Terrain relief
The light and shade on mountains and craters comes from the slope of the height map, measured from two nearby samples. Both samples now use the same amount of detail (before, the centre sample had two more layers of noise, which the slope read as huge cliffs and smeared Earth-like and desert highlands into streaks). Craters on rock worlds are unchanged.

## Known limits
- The system presets are a first guess read from each description (colours, moons, rings). Expect to adjust some by hand.
- The whole planet is one pass; rings do not light moons and do not cast shadows on them.
- Rings are flat: no thickness, no braids, no waves. A ring has one source moon at most, and moons never push each other's gaps around beyond the three resonances listed under Rings.
- At most 5 moons per planet. Moons start small, 2 to 17 percent of the planet's radius (Earth's Moon is 27 percent, Titan about 4 percent of Saturn's, the small captured rocks far less), and they sit much closer than real moons would. The Size slider goes down to 1.5 percent. Orbits are circular.
- Ring and moon draw order is approximate when a moon passes behind a ring.
