# usage: python3 parse_presets.py <systems-from-parse.py.json> <08-planet-presets.json> systems.json preset-base.json
# Merges the lore sheet's planet presets into the system list. Every preset repeats mostly the same settings, so the
# most common value of each setting goes to preset-base.json and each system keeps only what differs ("p").
import json, sys, collections, math
# People living in the clouds of a gas giant (millions), read by hand from each gas giant's World and Population lines in the
# lore sheet. Only worlds whose people live on decks, rafts, balloons or platforms in the atmosphere are listed; worlds whose people
# live on a moon, aboard a ring or in orbit are left out. Where the sheet gives one number for cloud and moon people, only the cloud
# share is used (checked by the Lore Master). The Floating cities slider takes the log position of this population.
CLOUD_M = {
    'Vigilia': 300, 'Hartwell': 300, 'Ardlinn': 3500, 'Pyrite': 900, 'Disciplina': 8000, 'Sourwater': 0.02, 'Natron': 670,
    'Nueva Pradera': 3000, 'Ellerbeck': 700, 'Limes': 5, 'Gypsum Minor': 2, 'Kolokol': 700, 'Dongyu': 900, 'Kaukomaa': 800,
    'Lowenna': 600, 'Port Sorrel': 12, 'Marisol': 5000, 'Lagoa Funda': 0.6, 'Natalis': 200, 'Old Amber': 0.6, 'Halfpenny': 20,
    'Azzurra': 850, 'Brushfield': 1, 'Kovarna': 9300, 'Kalinov': 20, 'Wellhead': 2000, 'Kreuzweg': 90, 'Groza': 0.02,
    'Zlatna': 4500, 'Nowhere': 0.08, 'Backwater': 0.02, 'The Ladle': 0.009, 'Goldcloud': 0.2, 'Aozora': 1, 'Slate Hill': 5,
    'Stormfen': 30, 'Sankt Odile': 2, 'Sonnfeld': 5000, 'Redfen': 0.0006, 'Ivyholm': 1000, "Brandt's Giant": 0.003, 'Meseta': 8,
    'Mirevale': 6, 'Novy Rynek': 2000, 'Awlbridge': 60, 'Bluefathom': 0.005, 'Belavista': 6000, 'Los Pozos': 0.2, 'Signum': 150,
    'Flarewater': 800, 'Naphtha': 0.01, 'Last Platform': 0.0003, 'Halvmark': 9, 'Brokengate': 0.8, 'Seven Gates': 5000,
    'Waymark': 25, 'Opsis': 3400, 'Endline': 200, 'Skylark': 1,
    'Nebbia': 11, 'Drift Harbour': 0.5, 'Kairos': 0.2, 'Grandmother': 0.5,   # Lore Master's check: Nebbia's villages all float in the fog; the last three have only faint lights
}
def cloud_pos(millions):   # the inverse of the tool's popBillions: 0.01 billion at 0, 10,000 billion at 1
    return round(min(1.0, max(0.003, (math.log10(millions / 1000.0) + 2) / 6)), 3)
sysf, prf, out, base_out = sys.argv[1:5]
rows = {r['n']: r for r in json.load(open(sysf, encoding='utf8'))}
pre = json.load(open(prf, encoding='utf8'))['systems']
assert all(any(p['name'] == n and p['kind'] == 'gas' for p in pre) for n in CLOUD_M), 'a cloud-city system is not a gas giant'
assert len(pre) == len(rows) and all(p['name'] in rows for p in pre), 'system lists differ'
dump = lambda v: json.dumps(v, sort_keys=True, separators=(',', ':'))
params = {p['name']: {k: v for k, v in p['preset']['params'].items() if k != 'name'} for p in pre}
for n, m in CLOUD_M.items(): params[n]['cities'] = cloud_pos(m)
count = collections.defaultdict(collections.Counter)
for p in params.values():
    for k, v in p.items(): count[k][dump(v)] += 1
base = {k: json.loads(c.most_common(1)[0][0]) for k, c in count.items()}
res = []
for p in pre:
    r = dict(rows[p['name']]); t = p['text']
    r['pop'] = t['population']; r['lore'] = t['lore']; r['tf'] = p['terraform']; r['sd'] = p['starDensity']
    r['p'] = {k: v for k, v in params[p['name']].items() if dump(v) != dump(base[k])}
    res.append(r)
json.dump(res, open(out, 'w', encoding='utf8'), ensure_ascii=False, separators=(',', ':'))
json.dump(base, open(base_out, 'w', encoding='utf8'), ensure_ascii=False, separators=(',', ':'))
print(len(res), 'systems,', len(base), 'base settings')
