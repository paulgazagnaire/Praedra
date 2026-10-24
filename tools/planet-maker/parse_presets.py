# usage: python3 parse_presets.py <systems-from-parse.py.json> <08-planet-presets.json> systems.json preset-base.json
# Merges the lore sheet's planet presets into the system list. Every preset repeats mostly the same settings, so the
# most common value of each setting goes to preset-base.json and each system keeps only what differs ("p").
import json, sys, collections
sysf, prf, out, base_out = sys.argv[1:5]
rows = {r['n']: r for r in json.load(open(sysf, encoding='utf8'))}
pre = json.load(open(prf, encoding='utf8'))['systems']
assert len(pre) == len(rows) and all(p['name'] in rows for p in pre), 'system lists differ'
dump = lambda v: json.dumps(v, sort_keys=True, separators=(',', ':'))
params = {p['name']: {k: v for k, v in p['preset']['params'].items() if k != 'name'} for p in pre}
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
