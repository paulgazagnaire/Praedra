# usage: python3 parse_presets.py <systems-from-parse.py.json> <08-planet-presets.json> systems.json preset-base.json [sector-density.json]
# Merges the lore sheet's planet presets into the system list. Every preset repeats mostly the same settings, so the
# most common value of each setting goes to preset-base.json and each system keeps only what differs ("p").
import json, sys, collections, math, re
# Head counts come from the sheet's own numbers: popB (everyone, billions) for land worlds and cloudPopM (millions living in a gas
# giant's clouds; moon, ring and orbit dwellers are not counted) for gas giants. The Population slider (and Floating cities on a gas
# giant) takes the log position of that count, 100 people to 10,000 billion.
def pos_of(people):   # the Population slider's scale: 100 people at 0, 10,000 billion at 1, log in between
    return round(min(1.0, max(0.001, (math.log10(people / 1e9) + 7) / 11)), 4)
sysf, prf, out, base_out = sys.argv[1:5]
sector_sd = json.load(open(sys.argv[5], encoding='utf8')) if len(sys.argv) > 5 else {}   # from regions.py: the star density of each sector
rows = {r['n']: r for r in json.load(open(sysf, encoding='utf8'))}
pre = json.load(open(prf, encoding='utf8'))['systems']
assert len(pre) == len(rows) and all(p['name'] in rows for p in pre), 'system lists differ'
dump = lambda v: json.dumps(v, sort_keys=True, separators=(',', ':'))
params = {p['name']: {k: v for k, v in p['preset']['params'].items() if k != 'name'} for p in pre}
for p in pre:
    pp = params[p['name']]
    if p['radiusKm']: pp['rad'] = min(75000 if pp.get('kind') == 'gas' else 15000, max(15000 if pp.get('kind') == 'gas' else 50, p['radiusKm']))   # the real radius
    if pp.get('kind') in ('ring', 'shell'): continue   # a ring's slider is a density, not a head count
    n = p['cloudPopM'] * 1e6 if pp.get('kind') == 'gas' else p['popB'] * 1e9
    pp['cities'] = pos_of(n) if n > 0 else 0
count = collections.defaultdict(collections.Counter)
for p in params.values():
    for k, v in p.items(): count[k][dump(v)] += 1
base = {k: json.loads(c.most_common(1)[0][0]) for k, c in count.items()}
res = []
for p in pre:
    r = dict(rows[p['name']]); t = p['text']
    r['pop'] = t['population']; r['lore'] = t['lore']; r['tf'] = p['terraform']; r['hist'] = t.get('history', ''); r['sd'] = sector_sd.get(r['sec'], p['starDensity'])
    r['p'] = {k: v for k, v in params[p['name']].items() if dump(v) != dump(base[k])}
    res.append(r)
json.dump(res, open(out, 'w', encoding='utf8'), ensure_ascii=False, separators=(',', ':'))
json.dump(base, open(base_out, 'w', encoding='utf8'), ensure_ascii=False, separators=(',', ':'))
print(len(res), 'systems,', len(base), 'base settings')
