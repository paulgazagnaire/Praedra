# usage: python3 regions.py <07-systems.md> <proposed-spiral-data.json> <systems-from-parse.py.json> sector-density.json
# Star density follows a sector, not a single system. The spiral galaxy keeps the old split of 7 core, 19 inner-rim and 38 outer-rim
# sectors; ranked by how far each sector's systems sit from Charter (the centre) that gives the region of every sector.
import json, sys, re, math, collections
md, spiral, sysf, out = sys.argv[1:5]
CORE, INNER = 7, 19   # sectors; the rest are outer rim
classic = {}   # new system name -> the Classic placeholder name the spiral file is keyed by
for l in open(md, encoding='utf8'):
    m = re.match(r'\| ([^|]+?) \| \*\*([^*]+)\*\* \|', l)
    if m: classic[m.group(2)] = m.group(1)
pos = json.load(open(spiral, encoding='utf8'))['pos']
rows = json.load(open(sysf, encoding='utf8'))
dist = collections.defaultdict(list)
for r in rows:
    if r['sec'] in ('Outside any sector',) or classic.get(r['n']) not in pos: continue
    x, y = pos[classic[r['n']]]
    dist[r['sec']].append(math.hypot(x - 500, y - 500))
order = sorted(dist, key=lambda s: sum(dist[s]) / len(dist[s]))
assert len(order) == 64, len(order)
sd = {s: 0.8 if i < CORE else 0.6 if i < CORE + INNER else 0.4 for i, s in enumerate(order)}
json.dump(sd, open(out, 'w', encoding='utf8'), ensure_ascii=False, indent=0, sort_keys=True)
print(collections.Counter(sd.values()))
