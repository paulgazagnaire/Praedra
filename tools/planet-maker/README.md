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
    "cities": 0..1, "rad": 1500..15000, "plan": 0..1, "land": 0.02..1 or null, "lava": 0..1, "craters": 0..1, "scars": 0..1,
    "bands": 3..20, "turb": 0..1.4, "storms": 0..6, "stormSize": 0.2..1.6,
    "spin": -1..1, "cloudSpeed": -1..1, "flow": 0..1.5, "moonSpeed": 0..2,
    "tilt": 6..85, "roll": -90..90, "sun": -180..180, "sunH": -60..60, "zoom": 0.5..1.8,
    "ring": { "on": false, "type": "broad|narrow|dusty|arcs|wreck|moonfed", "inner": 1.1..2.8, "width": 0.04..2.6, "op": 0.1..1, "gaps": 0..1,
              "struct": 0..1, "clump": 0..1, "dust": 0..1, "feeder": 0..5, "seed": 0..9999, "spin": -1..1, "c1": "#hex", "c2": "#hex" },
    "moons": 0..5,
    "moonList": [ { "type": "rock", "size": 0.015..0.5, "orbit": 1.3..7, "speed": -2..3, "tilt": -60..60, "phase": 0..360,
                    "detail": 0..1, "feat": 0..1, "haze": 0..1, "shape": 0..1, "spin": -1..1, "seed": 0..9999,
                    "c1": "#hex", "c2": "#hex", "c3": "#hex" } ] } }
```
`moonList` holds the moons in order (the `moons` slider shows the first n). A preset without it still loads: the moons are generated from the seed and the world type.
Speeds: rotation = `spin * 0.5` radians per second (0.25 is one turn in about 50 s).

Pasted presets and library entries are checked on load: numbers are clamped to their slider range (moons 0 to 5, each moon's numbers and colours the same way, an unknown moon type becomes cratered rock, an unknown ring type becomes broad icy rings; presets saved before ring kinds existed load as broad icy rings with their old size and colours; a hand-written ring of another kind starts from that kind's own size, colours and source moon, so only the fields you write override it), bad colours are replaced, and a short palette is filled from the world type's defaults.

## Using it in Godot later
The shader is GLSL ES 3.00 with no tricks. Porting to a Godot `.gdshader` is a mechanical rename (`uniform` blocks stay, `fragColor` becomes `COLOR`). The preset values map one-to-one onto the shader uniforms, so the game can draw the same planet from the same JSON.

## Detail switch
The page has a Detail: High / Fast button (not part of a preset). High uses more noise layers, cloud shadows and a third crater size; Fast drops them for slower graphics cards.

## Inhabited worlds (population and density)
How a settled world looks from space is set by one number: how many people live on each square kilometre of land. The sliders feed it:
- **Population**, log scale: 0.01 billion at the left to 10,000 billion (10 trillion) at the right. Stored as `cities` (0..1; 0 = no people). Earth is about 8 billion.
- **Planet radius** in km (`rad`, default 6371): sets the land area, and every light pattern is measured in km, so a small moon is not covered by Earth-sized cities.
- **Land share** (`land`): estimated from the Ocean level (about 1 − 1.2 × ocean) until you move it. It only changes the density, not the shape of the continents.
- **Grown or planned** (`plan`, 0..1, default 0.5): moves the density at which districts turn planned, and how many of them do.
The line under Population shows the result, for example "44 people per km² of land · settled". Names by density: empty (< 0.5), frontier (< 5), rural (< 30), settled (< 150), dense (< 600), metropolitan (< 3,000), megalopolis (< 15,000), ecumenopolis (more).

Where people live is still the old settling score (coasts and basins first, then lowlands with a mild climate and some water). The average density lifts or lowers that score into a local density, and the local density picks the look:
- Thin: dark land, scattered villages that bunch around towns, a few bright cities with a bloom, faint winding veins (rivers, coast roads) only where people live. Nothing is straight.
- Dense: the lights join into sprawl with a ragged edge, with dark rivers and parks and big bright and dim regions, so it never goes flat.
- Packed (past about 1,000 per km² locally, earlier if Planned is high): some districts, about 450 km across, turn planned: a street grid with a main road every 60–100 km, or a hub with rings and spokes. Each district has its own direction, brightness and size, and its ring road is broken in places. No line is longer than a district.
- A world where everyone lives on bare rock too (very high density) loses the empty patches.
- Day: a town is only a few km of roofs, so drawn to scale it would vanish from orbit. The day side therefore reads the night field: wherever lights shine, or the local density is high enough that they would, the ground turns grey and built-up (a broad patchy area, wider than the lights), with dark streets inside planned districts. Farmland patchwork at moderate density (irrigation discs on dry worlds), none on airless rock. Relief is only flattened inside real cities, not across the wider built-up area.
Seed changes the layout.

Systems can carry real numbers later: `popB` (population in billions), `radiusKm` and `landPct` in `systems.json` are read when a system loads and set these sliders. Old saved presets keep working, but their `cities` value now means population on the log scale (old 0.3 = 0.6 billion, old 1 = 10 trillion).

## Clouds
The cloud layout is modelled on Earth's satellite cloud climatology, so a planet with the Clouds slider near the middle shows roughly Earth's mix of clear and cloudy sky (checked by counting covered pixels per latitude on a debug render):
- **Tropics:** a ragged storm line along the equator, broken into clusters, not a continuous belt.
- **Subtropics:** the driest belts, with thin low cloud: small heaped cells that gather in patches.
- **Mid latitudes:** the heaviest cover, in storm tracks. Waves in the jet stream (3 to 5 around the planet, random per seed) bend the cloud into separate frontal systems; fronts lean the same way in both hemispheres (mirrored), with feathery streaks drawn out along the flow and cyclones with spiral arms at the sharp bends.
- **Land and sea:** wet land and open sea carry more cloud, deserts stay clearer, so the cloud follows the planet's own climate.

**Cloud shapes.** Cloud edges fade over a wide band instead of a sharp one, so thin cloud stays see-through and thick cloud keeps its own texture: bright cores, grey gaps, curled edges. Before, a cloud went fully opaque almost at once and every mass read as a flat white blob. Cloud brightness now follows thickness more strongly, and the cover threshold was lowered a little so the amount of cloud stays about the same.

A second, high deck of thin ice cirrus sits above: patchy, combed into fine streaks along the jet streams, see-through, drifting faster than the low deck, shifted against it by height, and still lit after the ground has gone dark at dusk (pink-gold at the day-night line). The shader is compiled in variants, so work a planet does not use costs nothing: gas giants and worlds with Clouds at 0 skip all cloud code, and Fast detail also skips the high cirrus deck, small cells, streaks, relief and ground shadows and uses fewer cyclones. The Clouds slider sets overall cover, Cloud drift moves the whole pattern. Gas giants have their own bands and storms and are not affected. On software rendering, a cloudy world in High costs about 40 to 60 percent more than before clouds were added, Fast about 10 percent more, and worlds without clouds nothing; a real graphics card has not been measured.

**Cloud shadows (High detail).** Clouds darken the ground beside them, on the side away from the sun, on every world that has a low cloud deck (water cloud, ash, dust, ice haze). The shadow is thrown further than real clouds would throw it, further still as the sun gets low, so it reads from orbit; at noon it sits right under the cloud. It is a second look-up of the cloud pattern toward the sun, so High costs a little more per frame; Fast has no cloud shadows. High cirrus casts none, and clouds do not shadow moons or rings.

**What clouds are made of.** The weather follows the world. Most worlds carry white water cloud. A volcanic world has no water: its clouds are grey-brown ash and smoke that dim the lava glow under them and are tinted faintly orange from below, and the high white cirrus deck is off (the slider reads "Ash and smoke"). A desert carries blown dust in tan, not white cloud (the slider reads "Dust storms"). An ice world is too cold and dry for a water cloud deck: it keeps only thin, high ice-crystal streaks and a faint haze, pale blue, so the slider still shows against white ice (the slider reads "Ice crystal haze"; the low cloud deck is cut to about half). A barren airless rock has no weather at all: the Clouds slider is hidden and clouds are always off, even in presets that set them. Gas giants have their own bands. Not drawn: metal or glass rain on very hot gas giants and acid cloud on Venus-like worlds (they would be a colour change of the same cloud, easy to add if wanted).

## Moons
Moons are no longer one kind. There are nine: eight modelled on something that exists, and the Industrial moon, which is made up. The surveyed inventory below says what was left out and why.

| Kind | Real examples | What defines the look |
|---|---|---|
| Cratered rock | Earth's Moon, Mercury | pale highlands, craters, dark plains in the big basins |
| Dark ice-rock | Callisto, Iapetus, Rhea | very dark old crust, bright ice splashed out of craters, one face darker than the other |
| Smooth ice | Europa, Enceladus | bright, nearly no craters, long dark cracks, mottled chaos terrain, ice sheen |
| Volcanic | Io | sulphur yellows and oranges, frost, dark calderas, vents that glow on the night side |
| Hazy giant | Titan | thick orange smog over the ground, soft bands, bright atmosphere rim |
| Frozen plains | Pluto, Triton, Charon | cream nitrogen ice, dark red equatorial band, smooth bright lobe, bright poles |
| Captured rock | Phobos, Deimos, Amalthea | small, knobbly (not round), dark, deeply cratered, parallel grooves |
| Industrial moon | none; a moon turned into a factory | bare mined rock with glowing slag, steel-plated districts with lit windows, round open pits crossed by scaffolding, towers standing out from the rim |
| Living moon | none known; thought possible around a giant planet | kept plain on purpose: ocean, a few big continents, polar ice, a thin atmosphere; cloud wisps off until turned up |

Left out on purpose (real, but they would read as one of the above at this size): Ganymede (ice-rock with a Callisto look and grooved terrain), Mimas and other small icy craterlets (cratered rock with a huge crater), Hyperion (a sponge-textured captured rock), Miranda (patchwork of terrains), Moon-sized dwarf planets (frozen plains), irregular outer moons (captured rock). Not drawn at all: moonlet swarms, ring-moon pairs, binary moons, co-orbitals, moons with their own rings.

**Industrial moon.** Sliders: Mined rock and Steel plating (the two colours), Pits and towers (how many open pits and how many towers), Built-over share (how much of the surface is plated). The plating is square panels with seams, a few orange hazard panels and dark ones, and rows of lit windows and the odd cyan beacon, which show on the night side. Pits are stepped down into a hot glow; about half have a diagonal lattice and two main beams over them, and a steel ring round the edge. Slag veins run through the bare rock and glow. The towers are a lattice stem on a wider, lower base, up to about a fifth of the moon's width tall, drawn in 23 slots round the rim, so they only stand out at the edge (they are not on the face). A system whose first moon is described with "factory", "refinery", "shipyard", "dry dock", "dock frames", "foundry", "forge", "smelter", "quarried" or "industrial" gets this kind. It costs about 5 to 10 percent more than other moon kinds on software rendering.

**Editor.** In the Moons section: How many (0 to 5), pick which moon, pick its kind, then every setting for that moon: Size, Distance, Orbit speed (negative runs backwards), Orbit tilt, Start position, Detail, Feature, Atmosphere, Lumpy shape, Own spin (0 keeps one face to the planet), three colours. The Detail and Feature sliders are renamed per kind (for Smooth ice they read Mottling and Cracks, for Volcanic Vents and Eruptions). New look rerolls the moon's pattern, Reset to kind restores that kind's defaults. Lowering How many keeps your edits to the hidden moons.

**System presets.** Each system's moon text picks the first moon's kind when it says something ("white", "icy", "volcanic", "hazy", "captured", "black", "dark", "factory"…); every other moon is drawn from the seed, with more rocky and captured moons on rocky worlds and more ice-rock and hazy moons around gas giants.

**Shadows.** Each moon casts a soft shadow on the planet when it passes between the planet and the sun: a dark spot that crosses the surface as the moon orbits. Shadows are drawn on the planet's sunlit side only, and a moon shadows the planet even while it is behind it as seen from the camera. A moon also shades the rings: a soft dark streak on the ring behind it, away from the sun (a moon in the ring plane leaves a long thin one, a tilted moon drops its shadow on another part of the ring), drawn on the ring's sunlit side.

Moons are shaded once and only compiled into the shader when a planet has at least one, so a world with no moons costs nothing extra.

## Megastructures
A new kind in the first list, **Megastructure**, for things people built that are bigger than a planet. Four exist, picked with the chips under Structure: the **Ring**, the **Cylinder**, the **Wheel** and the **Shell**. (Rama, the Gate and the Swarm were built and then removed on Paul's call; a saved preset that names one of them loads as a Ring.) World-sized works (an engined planet, a computer planet, a fortress world) and an orbital ring or space elevator as a switch on any planet are planned (see the survey doc). Moons, planetary rings and the planet's own atmosphere glow do not apply and are hidden for them. Picking a structure also sets the angle it looks best from. Each carries a size label under the title (and a longer one in its note) with the figure from the source it is modelled on; none is drawn to scale beside a planet, since each fills the frame.

**Ring** (Niven's Ringworld, Banks's Orbitals, Bishop ring, Halo-type rings). A solid band round a star, spun for gravity, with a wall along each edge. It is drawn by sphere tracing a solid of revolution (one shape, a slab with two walls), not as a sphere, so it is a separate drawing path: it is compiled into the shader only for this kind and costs other kinds nothing.
- **Seen at an angle** the near half shows the outer hull and the far half shows the ground on the inside of the loop, with the star glowing in the middle. The floor is the same terrain the planets use (ocean, land, mountains, ice, clouds, city lights), laid along the strip, so all the Look sliders work on it. Round the loop is a full turn of longitude; across it the floor covers a band of latitudes (Climate range), so it runs from tropic in the middle to cold at the walls.
- **Night.** Shade squares near the star put parts of the loop in shade (Night share, six stripes round the loop). The shade is fixed in space and the ground turns under it (Rotation), so the cities light up as night passes over them.
- **Hull.** Square plates with seams, a heavier rib every twelfth plate, a hazard plate here and there, an accent stripe along each edge, small lights (Hull plating, Hull lights, Hull and Accent colours). The plates turn with the ring.
- **Width and walls.** Width is a share of the ring's radius, 6 to 60 percent. A real Ringworld is about 0.7 percent, a hair-thin line, so width is **not to scale**. Rim wall height is up to 12 percent.
- **Framing.** The ring fills the frame on its own with the star at the centre. It is not drawn to scale beside any planet.
- **Sky.** Star glow sets the light in the middle. The hull is lit by the Sun side and Sun height sliders as an outside light; the floor is lit only by the star, always overhead. That is not physically consistent (the star would not light the outside), but a ring lit only from inside is a black line.

**Cylinder** (O'Neill's Island Three, Babylon 5, Elysium-type pairs). A long drum spun about its axis for gravity, seen from outside under the real Sun (not a star at the centre; there is no star glow). Also sphere traced, in the same compiled-for-this-kind path.
- **Strips and mirrors.** Six long strips round the drum: three of window and three of hull (Window share sets how much of its strip each window takes). A mirror is hinged on each window edge (Mirror size, Mirror angle) to steer sunlight in; one set of numbers draws all six. The windows are panes between dark mullions over a warm lit interior that stays lit on the night side.
- **Ends.** A still neck and a docking wheel at each end; the hull and the mirrors turn with the drum, the necks do not.
- **One or a pair.** A pair is two drums side by side turning opposite ways (so the pair does not twist in space) with a bar across each end. Length to width runs 1.5 to 9.
- **Hull.** Plates with seams, a rib every fourth plate, a few hazard plates, an accent stripe along each window edge, small lights; coarser plates than the ring's, because the drum is small in the frame.
- **Framing.** The drum or pair, with its mirrors, is scaled to fill the frame. Not to scale beside any planet.

**Wheel** (the Stanford torus, 2001's Space Station V, von Braun's wheel). A fat tube bent into a ring and spun for gravity, hung by spokes from a still hub with docking ports.
- **Tube.** Plates round the tube with whole numbers of them in each direction, so they meet at the seam. Tube thickness runs 3 to 30 percent of the ring's radius (a real Stanford torus is about 10 percent).
- **Windows.** A band of panes along the tube's inner face, above and below, over a warm lit interior that stays lit on the night side (Window share).
- **Spokes and hub.** 3 to 8 spokes (the Stanford torus has six). The spokes and the tube turn together; the hub and its docking ports stand still.
- **Mirror.** Mirror over hub puts a flat segmented mirror on a mast above the hub, tilted at 45 degrees: the Stanford kind. At none it is a plain station wheel.

**Shell** (Dyson shell, Matrioshka brain, Shkadov thruster). A sphere of plates built round a star. There is no marching: a ray crosses each concentric layer twice, so the picture is each layer's outside face (lit by the Sun sliders), then the star, then each layer's lit inside face, each letting through what its open plates leave.
- **Plates and gaps.** Square plates in latitude bands with whole numbers of plates per band (so none pinch at the poles and all meet at the seam), a strut round each, lights on some (Hull plating sets how many bands, Hull lights). Gaps opens a share of the plates to a window; through it you see the lit inside of the far side, warm and bright.
- **Layers.** 1 to 4 nested shells, each a little smaller and turning the opposite way at a different speed, each warmer inside than the one outside (a Matrioshka brain: each shell lives on the one inside's waste heat).
- **Coverage.** Below 100% only a cap on the Sun side exists, with a hard edge: a Shkadov thruster, a giant mirror that pushes a star by reflecting its light one way.

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
- A moon whose orbit crosses any kind of ring clears a lane there, except the moon the ring is made from; the lane gets wider with the moon's size. This used to work only on broad and wreckage rings, so a moon in a wide dust ring did nothing. The lane does not depend on the Gaps slider. A moon tilted more than about 10 degrees out of the ring plane clears less (it only crosses the ring twice per orbit), and past 30 degrees nothing.
- On broad and wreckage rings (the kinds that have a Gaps slider), a moon outside the ring clears gaps where a grain would circle 2, 3/2 or 3 times for each orbit of the moon (at 0.63, 0.76 and 0.48 of the moon's distance). That is how real moons carve the Cassini Division and the gaps near it. Move a moon with the Distance slider and the gap moves with it. A broad ring keeps a Cassini-like and an Encke-like gap even without moons, unless a moon has already cut one there.
- Dusty, arc, wreckage and moon-fed rings need a moon that belongs to them (the Source moon slider, 1 to 5; "none" leaves the ring as a free-floating one). Picking such a kind adds a moon if there is none, puts it where that kind needs it (inside a dusty or wreckage ring, just inside the arcs, in the thick part of the moon-fed dust), flattens its orbit into the ring plane and makes it a small captured rock or a bright ice moon. After that you can move it: the ring bunches up, or the bulge and tail follow it, wherever it goes and wherever it is on its orbit.
- Picking any ring kind moves other moons out of the ring so they do not sit inside it by accident; drag a moon back in on purpose to cut a lane.

**Light.** With the sun on your side the rings reflect (dense ice is bright, dust is dull). With the sun behind the rings, dense rings turn dark and thin or dusty ones glow by forward scattering; the Backlit glow slider sets how much. Rings cast a shadow on the planet (thin narrow rings leave a pattern of fine lines) and the planet casts a soft shadow on the rings, and the rings cast one on moons that sit where the sun's light crosses them (dense ring: dark, thin dusty ring: a faint dimming).

**Editor.** In the Rings section: tick "This planet has rings", pick a kind, then Distance, Width, Thickness and the kind's own sliders (renamed per kind; those that do not apply are hidden), plus two colours. New look rerolls the pattern and nudges the colours, Reset to kind restores the kind's defaults.

**System presets.** A system's text picks the kind: "debris", "rock", "hull", "hauler" or "broken" give wreckage, "dust", "faint" or "thin" give faint dust rings, "dark" gives narrow dark rings, anything else broad icy rings; "wide" and "vast" widen it, "faint" and "heavy" change the thickness. Ten systems have a ring of their own.

Rings are only compiled into the shader when a planet has them. On software rendering a ringed planet costs about the same per frame as before; a real graphics card has not been measured.

## Gas giants
The belts are not a regular stripe pattern. Three waves of different spacing are added and the latitude is bent by slow noise, so some belts are broad and some thin, some faint, and the stripes fade toward the poles. The Bands slider is roughly the number of stripes (belts plus zones), not a pattern repeat count; saved planets with a Bands value keep it but look different from before.

The wind follows the same profile: belts and zones blow in opposite directions with a fast equator, so the shear sits where a belt meets a zone. That is where the turbulence is: edges turn wavy and curl, fine streaks run along the flow, and (in High detail) strings of small pale and dark ovals form along the edges, with soft mottling inside the broad belts and zones. Turbulence turns all of this up or down.

Giant storms are no longer flat discs. Each one twirls the sky around it before the bands are drawn, so the belts bend into spirals inside the storm and into arcs around it, the storm tints those swirled bands instead of painting over them, and the air near it is more turbulent. Storm size sets how far the twirl reaches. The belt colours are still the first four palette colours and the storm colour is the fifth.

Cost: on software rendering a gas giant in High is about 30 percent slower per frame than before; Fast skips the grain, ovals and puffs. Not measured on a real graphics card. The bands still slide past each other as before (Band flow), but the new wind speeds are lower, so they shear more gently. Not drawn: storms placed on a belt edge on purpose (they sit at a random latitude and the bands bend around them wherever they land), long-lived storm wakes, hexagons at the poles.

## Terrain relief
**Mountains.** They used to be one smooth ridge layer, which read as sand dunes. Now they are long belts (where plates would have collided) with sharp crests: a ridged multifractal, each layer weighted by the one before, so crests carry the fine detail and valleys stay smooth. Between the belts the land is plains and low hills. Steep faces turn bare rock, and the highest crests keep snow even in a warm climate (less on worlds with few ice caps). The Mountains slider still runs from none to everything. It costs about 10 to 20 percent more per frame on software rendering.

The light and shade on mountains and craters comes from the slope of the height map, measured from two nearby samples. Both samples now use the same amount of detail (before, the centre sample had two more layers of noise, which the slope read as huge cliffs and smeared Earth-like and desert highlands into streaks). Craters on rock worlds are unchanged.

## Known limits
- The system presets are a first guess read from each description (colours, moons, rings). Expect to adjust some by hand.
- The whole planet is one pass. Moons do not shadow each other, and a moon's shadow is a plain round disc (lumpy outlines are not followed).
- Rings are flat: no thickness, no braids, no waves. A ring has one source moon at most, and moons never push each other's gaps around beyond the three resonances listed under Rings.
- At most 5 moons per planet. Moons start small, 2 to 17 percent of the planet's radius (Earth's Moon is 27 percent, Titan about 4 percent of Saturn's, the small captured rocks far less), and they sit much closer than real moons would. The Size slider goes down to 1.5 percent. Orbits are circular.
- City looks are tuned by eye on software rendering, never on a real graphics card, and from the usual viewing size the planned districts read as texture more than as streets. The land share estimate from the Ocean slider is rough (it does not count the land actually drawn), so type the real land share when it matters. Cities are one colour: no tints for different districts yet.
- Industrial moon: the pits and plating are painted on the surface (nothing is raised), so scaffolding casts no shadow and plates have no relief, and towers are only seen at the edge, so a tower that points at you is invisible. Looks tuned by eye on software rendering only.
- Megastructure timing (software rendering, SwiftShader, 512 px, 3 frames after warm-up; real graphics cards not measured). High / Fast, in seconds: terran planet 7.1 / 3.4; Ring 14.9 / 10.2; one drum 5.5 / 3.8; pair 5.6 / 3.7; Wheel 5.5 / 3.5; Shell 5.6 / 3.5. Runs on this renderer differ by up to 2 times from one session to the next (the QA round measured the Ring at 6.1 against 9.8 for a terran, the opposite order to mine), so read these as "about a planet or less" for everything except the Ring, whose cost is its floor terrain and moves with Clouds and Population. Fast Detail on a structure takes fewer trace steps (44 against 90), accepts a hit within half a pixel and takes four samples for the normal instead of six; it changes 1 to 8 percent of the pixels (the thin mirrors look a little more ragged).
- Megastructure shell: the star is a flat bright disc with the same glow as the ring's, hidden by any solid plate in front of it (so a closed shell shows no star, as it should). Layers do not shadow each other. The cap of a Shkadov thruster is a hard-edged slice of the sphere, not a bowl, and it does not push anything. Tuned by eye on software rendering only; see the timing note below.
- Megastructure wheel: only the outside is drawn. The mirror over a wheel's hub casts no shadow on the wheel, and the windows do not light the hull. Not built: Niven's Smoke Ring (a torus of air round a star, which needs a volume, not a surface). Tuned by eye on software rendering only.
- Megastructure cylinder: only the outside is drawn (the land inside is not). Mirrors and hull do not shadow each other or the windows, and the lit interior does not light the hull around it. Hull, mirrors and window strips turn together, so a window always sits under its mirrors. Tuned by eye on software rendering only; see the timing note below.
- Megastructure ring: the floor is flat terrain with no height, so the walls cast no shadow on it and there is no air haze when you look across the loop. Shade squares are not drawn, only their shadow. Only one star. Tuned by eye on software rendering only; see the timing note below.
- Ring and moon draw order is approximate when a moon passes behind a ring.
- Moons make the shader bigger, and on software rendering a gas giant with 5 moons costs about 3 times a world without moons in both Fast and High. A cheap distance test before shading each moon changes nothing: with the moon code never run the cost is the same, so it comes from the size of the shader, not the maths. The fix would be to draw planet and moons in separate passes; it is not done because it has not been measured on a real graphics card.
