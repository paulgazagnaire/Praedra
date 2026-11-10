# Night art batch

Makes the missing character and unit pictures from the prompts in the Airtable base
"Praedra Lore", with Google's Gemini image model ("Nano Banana Pro", `gemini-3-pro-image`).

What it does, in order, for every row of **Characters** and of **Units** (troops and special forces only):

1. If **Reference sheet** is empty: fills the Terms "Reference sheet template" with the row's **Look**
   (the Chair and the Speculators use their own templates) and makes the sheet.
2. If **Portrait** is empty: sends the reference sheet with the row's **Portrait prompt** and the
   Terms "Portrait style" line, so the face stays the same.
3. Saves each picture and a dithered copy (Portrait Maker's default look: Atkinson, Cold, 320 dots, dot 2)
   to `/mnt/project-files/praedra/art/generated/<date>/`, and attaches the first version in Airtable.

It never replaces a picture already in Airtable. A prompt containing a banned phrase
(see `BANNED` in the script: the Praevisio "washing machine" words, fasces) is skipped and reported.

## Keys

- `GEMINI_API_KEY`: from Google AI Studio (billing on, monthly spend cap set in the Spend tab).
- `AIRTABLE_TOKEN`: needs `data.records:read` and `data.records:write` on the base.

## Use

    python3 tools/art-night/art_night.py --plan              # list and cost, makes nothing
    python3 tools/art-night/art_night.py --run --limit 2     # small test
    python3 tools/art-night/art_night.py --run               # everything missing
    python3 tools/art-night/art_night.py --run --tries 3     # three versions of each (first goes to Airtable)
    python3 tools/art-night/art_night.py --run --only Proxies --no-airtable

`--max-dollars` (default 10) refuses a run whose estimate is higher. The hard limit is the
AI Studio spend cap. Needs Python 3 with Pillow and numpy.
