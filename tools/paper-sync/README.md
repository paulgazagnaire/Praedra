# Paper sync: the game's windows as editable layers in Paul's Paper file

Paul keeps the UI frames of the Paper file "Praedra", page "UIs---V2--human", next to the game. These scripts rebuild those frames
from the running game, so the frames show the game as it is: every text a text layer (font, size, colour, line breaks as Godot set
them), every box a box layer, and only true pictures (planets, unit icons, the map, scroll bars) as images cut from a 2x screenshot.

## Steps

1. Shoot the game states (cloud session or any Linux box with xvfb):

       GODOT=/path/to/Godot_v4.7.1-stable_mono_linux.x86_64 tools/paper-sync/shoot.sh /tmp/raw

   For each state this writes `NAME.png` (2x screenshot), `NAME.json` (`--dump-ui`: every visible control with its rectangle,
   box, text lines and colours) and `NAME.map.png` (the map with every panel hidden, for the four-windows frame).

2. Build the frames:

       python3 tools/paper-sync/build_frames.py /tmp/raw /tmp/frames --img-url paper-asset:///Users/<you>/paper-exports/<date>/img \
           --stories stories.json

   One HTML file per artboard, plus `img/` with every picture they use. `--img-url` is where the `img/` folder will be on the Mac
   that runs Paper. `stories.json` is the story list beside each window (keyed "Sector window", "Ground window", "Fleet window",
   "Infrastructure window"; each row `id`, `story`, `status` yes/partly/no, `note`), taken from specs/ui/11-window-stories.md.

3. Check them against the game before writing anything into Paper:

       python3 tools/paper-sync/check_frames.py /tmp/raw /tmp/check

   It draws each window from its dump in Chromium (render.js, the game's own fonts) and compares it with the game's pixels.
   A window is right when under about 1% of its pixels differ by more than 60/255; `/tmp/check/NAME.cmp.png` shows game | HTML |
   difference. Wrapped text is the usual culprit: the dump must take the same width Godot wrapped at.

4. On the Mac (Remote Control), copy `img/` to the `--img-url` folder and write each HTML file into its artboard with Paper's
   `write_html`: delete the artboard's children, then `insert-children`. Back the frames up first.

## Files

- `godot/Scripts/Ui/UiDump.cs` writes the dump; `--dump-ui FILE.json` (Main.cs) turns it on, `--strike SYSTEM` sets up the harvested
  state.
- `dump2html.py`: one window from a dump to nested absolute-positioned layers with names.
- `build_frames.py`: which game states make which frame, captions, the story panel.
- `shoot.sh`, `render.js`, `check_frames.py`: steps 1 and 3.
