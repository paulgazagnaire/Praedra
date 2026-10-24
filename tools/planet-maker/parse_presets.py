# usage: python3 parse_presets.py <systems-from-parse.py.json> <08-planet-presets.json> systems.json preset-base.json
# Merges the lore sheet's planet presets into the system list. Every preset repeats mostly the same settings, so the
# most common value of each setting goes to preset-base.json and each system keeps only what differs ("p").
import json, sys, collections, math, re
# People living in the clouds of a gas giant (millions), read by hand from each gas giant's World and Population lines in the
# lore sheet. Only worlds whose people live on decks, rafts, balloons or platforms in the atmosphere are listed; worlds whose people
# live on a moon, aboard a ring or in orbit are left out. Where the sheet gives one number for cloud and moon people, only the cloud
# share is used (checked by the Lore Master). The Floating cities slider takes the log position of this population.
# The Population slider runs from 100 people to 10,000 billion. Land worlds take their count from the first number in the sheet's population line.
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
def pos_of(people):   # the Population slider's scale: 100 people at 0, 10,000 billion at 1, log in between
    return round(min(1.0, max(0.001, (math.log10(people / 1e9) + 7) / 11)), 4)
def old_pos(v):   # a position on the slider's earlier scale (10 million at 0) as a position on the current one
    return round((5 + 6 * v) / 11, 4) if v > 0 else 0
WORDS = {'one': 1, 'two': 2, 'three': 3, 'four': 4, 'five': 5, 'six': 6, 'seven': 7, 'eight': 8, 'nine': 9, 'ten': 10, 'eleven': 11, 'twelve': 12, 'twenty': 20, 'thirty': 30, 'forty': 40, 'fifty': 50}
def people(txt):   # the first head count in a world's population line, or None when it gives no clear one
    t = txt.lower()
    m = re.search(r'(?:about |roughly |some |over |nearly |around )?(\d[\d,]*(?:\.\d+)?)\s*(million|billion|thousand)?\b', t)
    if m: return float(m.group(1).replace(',', '')) * {'million': 1e6, 'billion': 1e9, 'thousand': 1e3, None: 1}[m.group(2)]
    m = re.search(r'\b(a few|a handful of|a couple of)\s+(hundred|dozen|thousand)\b', t)
    if m: return {'hundred': 300, 'dozen': 40, 'thousand': 3000}[m.group(2)]
    m = re.match(r'(?:about |roughly )?(\w+) ', t)
    return float(WORDS[m.group(1)]) if m and m.group(1) in WORDS else None
sysf, prf, out, base_out = sys.argv[1:5]
rows = {r['n']: r for r in json.load(open(sysf, encoding='utf8'))}
pre = json.load(open(prf, encoding='utf8'))['systems']
assert all(any(p['name'] == n and p['kind'] == 'gas' for p in pre) for n in CLOUD_M), 'a cloud-city system is not a gas giant'
assert len(pre) == len(rows) and all(p['name'] in rows for p in pre), 'system lists differ'
dump = lambda v: json.dumps(v, sort_keys=True, separators=(',', ':'))
params = {p['name']: {k: v for k, v in p['preset']['params'].items() if k != 'name'} for p in pre}
pop_text = {p['name']: p['text']['population'] for p in pre}
guess = []   # land worlds whose population line gives no count: they keep the old slider floor, 10 million
for n, pp in params.items():
    if pp.get('kind') in ('ring', 'shell'): continue   # a ring's slider is a density, not a head count
    if n in CLOUD_M: pp['cities'] = pos_of(CLOUD_M[n] * 1e6)
    elif pp.get('kind') != 'gas' and pp.get('cities') == 0.001:   # the sheet's "nothing set" value
        c = people(pop_text[n])
        if c is None: guess.append(n)
        pp['cities'] = pos_of(c) if c else old_pos(0.001)
    elif pp.get('cities', 0) > 0: pp['cities'] = old_pos(pp['cities'])
print(len(guess), 'land worlds without a count in their population line:', ', '.join(guess))
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
