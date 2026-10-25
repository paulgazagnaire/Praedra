# usage: python3 regions.py <07-systems.md> <extended-galaxy-v6-planets.csv> <systems-from-parse.py.json> sector-density.json
# Star density follows a sector, not a single system. The v6 galaxy data (specs/planets/data) gives every system a depth band
# (group: heartland, midreach, farreach) and every sector holds one band; the band of a sector sets its density.
import json, sys, re, csv, collections
SD = {'heartland': 0.8, 'midreach': 0.6, 'farreach': 0.4}
SD.update({'core': SD['heartland'], 'inner_rim': SD['midreach'], 'outer_rim': SD['farreach']})  # older copies of the planets file
md, spiral, sysf, out = sys.argv[1:5]
classic = {}   # new system name -> the Classic placeholder name the spiral file is keyed by
for l in open(md, encoding='utf8'):
    m = re.match(r'\| ([^|]+?) \| \*\*([^*]+)\*\* \|', l)
    if m: classic[m.group(2)] = m.group(1)
group = {r['name']: r['group'] for r in csv.DictReader(open(spiral, encoding='utf8'))}   # keyed by the Classic placeholder names
rows = json.load(open(sysf, encoding='utf8'))
bands = collections.defaultdict(collections.Counter)
for r in rows:
    g = group.get(classic.get(r['n']))
    if r['sec'] != 'Outside any sector' and g: bands[r['sec']][g] += 1
mixed = {s: dict(c) for s, c in bands.items() if len(c) > 1}
assert not mixed, mixed
assert len(bands) == 64, len(bands)
sd = {s: SD[next(iter(c))] for s, c in bands.items()}
json.dump(sd, open(out, 'w', encoding='utf8'), ensure_ascii=False, indent=0, sort_keys=True)
print(collections.Counter(sd.values()))
