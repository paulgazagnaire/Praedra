# Builds index.html from galaxy-maker.src.html and the v6 galaxy data (the systems' map positions).
# usage: python3 build.py galaxy-maker.src.html ../../data/design/extended-galaxy-v6-planets.csv index.html
import sys, csv, json
src, csvf, out = sys.argv[1:4]
rows = list(csv.DictReader(open(csvf, encoding='utf8')))
# the game draws each sector a little tighter round its own centre (GalaxyView.SectorPull = 0.7); do the same so the overlay and
# the density map match the game's map
PULL = 0.7
by = {}
for r in rows:
    by.setdefault(r['sector'], []).append(r)
sysl = []
for sec in sorted(by):
    mem = by[sec]
    cx = sum(float(r['map_x']) for r in mem) / len(mem)
    cy = sum(float(r['map_y']) for r in mem) / len(mem)
    for r in sorted(mem, key=lambda r: r['name']):
        x, y = float(r['map_x']), float(r['map_y'])
        if sec:
            x, y = cx + (x - cx) * PULL, cy + (y - cy) * PULL
        sysl.append([r['name'], round(x, 1), round(y, 1), r['group'] or 'beyond'])
s = open(src, encoding='utf8').read()
assert '/*__SYSTEMS__*/[]' in s
s = s.replace('/*__SYSTEMS__*/[]', json.dumps(sysl, separators=(',', ':')))
open(out, 'w', encoding='utf8').write(s)
print(len(sysl), 'systems,', len(s), 'bytes')
