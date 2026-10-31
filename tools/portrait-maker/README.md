# Portrait Maker

Cuts a character out of a portrait and dithers the character and the background as two separate layers. Published (private) at https://claude.ai/artifact/EtWTEMxC57zYXQBs5XnSUX.

## What it does
- **Picture**: drop, paste or choose one or more portraits (the Airtable "Portrait" attachments are 864 × 1152 JPEGs). Each picture keeps its own cut-out; the dither look is shared so every portrait matches.
- **Cut-out**: *Find the person* (MediaPipe selfie segmenter, people only), *Plain backdrop* (removes the colour found at the picture's border), *Already cut out* (a PNG's own transparency) or *No cut-out*. Then grow or shrink, edge softness, *Follow hair edges* (a guided filter on the picture), and a keep/remove brush with undo.
- **Background layer**: the picture with the character's place filled from the colours around it (push-pull fill, then blurred), so the character can be moved or used on another setting.
- **Dither**: Grain (blue noise), Fine grid (Bayer 8), Coarse grid (Bayer 4), Halftone, and the two error-diffusion patterns for stills, Atkinson and Floyd. Palettes Black and white, Cold, Warm (same as the Planet Maker and Galaxy Maker), Picture colours (steps per colour), Custom (four colours). Detail = portrait height in dots; dot size = screen pixels per dot. The outline can be dithered with the same pattern. The blue noise is made exactly as in the Planet Maker, Galaxy Maker and the game.
- **Setting**: the character on its own background, a plain colour, another picture or nothing, with setting brightness and the character's size and place. The character and the setting are dithered separately on the same grid, so their dots line up.
- **Views**: Cut-out (red is removed, draw to fix), Character, Background, Character in setting, and two mock game screens (Mission report, Database entry) with sample text.
- **Save** (through the page's downloads permission): character PNG, background PNG, character in setting PNG (one pixel per dot times the dot size), the clean undithered cut-out at full size, or all four as one zip.

## Build
```
python3 build.py assets/sample.jpg assets/selfie_segmenter.tflite   # writes index.html
```
`index.html` inlines the sample photo (NASA astronaut portrait, public domain, from scikit-image's data) and the person-finder model (`selfie_segmenter.tflite`, float16, from storage.googleapis.com/mediapipe-models). The MediaPipe code loads from jsDelivr (`@mediapipe/tasks-vision@0.10.14`); its engine `vision_wasm_internal.wasm` (9.4 MB, from that npm package's `wasm/`) is published next to the page with `contentType: application/wasm` and is not kept in git. Run `./fetch-engine.sh` to get it: it downloads https://registry.npmjs.org/@mediapipe/tasks-vision/-/tasks-vision-0.10.14.tgz and extracts `package/wasm/vision_wasm_internal.wasm` (9,423,986 bytes) next to `index.html`. Keep its version in step with the jsDelivr import in `portrait-maker.src.html`.

If the engine cannot start in a view, the page says so and switches to Plain backdrop; the brush still works.
