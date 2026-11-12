"""Thin the hyperspace route links (Paul 2026-11-11: "clean this mess of a route layout, reduce the number").

Reads data/design/extended-galaxy-v6-lanes.csv and -planets.csv, drops links by the rules below (judged on the map as the game draws
it: each sector pulled 0.7 toward its own centre, GalaxyView.SectorPull), writes both files back with the planets' route columns
(chokepoint, lane_count, on_route, major_routes, best_lane_tier) recomputed, and prints every dropped link with its reason.
Rules, in order:
 1. the Lantern knot: Lantern Run and Long Odds (one-hop "great routes" inside Lantern Sector) and Yard Road's spur to The Notch;
 2. a minor link drawn within 10 map units of a system it does not stop at;
 3. a minor link whose ends are already joined by at most 4 other links at most 1.5 times as long (longest checked first);
 4. a loose group of at most 4 systems joined only by minor links and touching no great route;
 5. a minor link that closes a loop (great routes first, then the shortest minor links, are kept);
 6. inside each sector, at most CAP minor links between its own systems: the longest link to a dead end goes first.
usage: python3 tools/routes/thin_routes.py [CAP]   (run from the repository root; default CAP 2)
"""
import sys, os, csv, heapq, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geo import load, pt_seg, dist, ROOT, write_planets

CAP = int(sys.argv[1]) if len(sys.argv) > 1 else 2
L, P, raw, D = load()
# The script rewrites its own input, so a second run would thin the thinned map again (Ponytail on #287: 7 more links, 10 more systems
# off the routes). It runs only on the map from before the thinning, which still has Lantern Run.
if not any(l['routes'] == 'Lantern Run' for l in L):
    sys.exit('the lanes file is already thinned (no Lantern Run left): restore the old lanes and planets files first, e.g. from git '
             '(git show 4359b9b:data/design/extended-galaxy-v6-lanes.csv), then run again')
key = lambda l: (l['from'], l['to'])
sec = lambda n: P[n]['sector']
drop = {}
for l in L:
    if l['routes'] in ('Lantern Run', 'Long Odds') or key(l) == ('The Notch', 'Snowmouth'): drop[key(l)] = 'the Lantern knot'
for l in L:
    if l['tier'] != 'minor' or key(l) in drop: continue
    for n in sorted(P):
        if n in (l['from'], l['to']): continue
        d, t = pt_seg(D[n], D[l['from']], D[l['to']])
        if d < 10 and 0.05 < t < 0.95: drop[key(l)] = 'drawn over ' + n; break
def graph(excl):
    g = collections.defaultdict(list)
    for l in L:
        if key(l) in excl: continue
        a, b = l['from'], l['to']; w = dist(D[a], D[b]); g[a].append((b, w, key(l))); g[b].append((a, w, key(l)))
    return g
for l in sorted([l for l in L if l['tier'] == 'minor' and key(l) not in drop], key=lambda l: (-dist(D[l['from']], D[l['to']]), key(l))):
    g = graph(set(drop) | {key(l)}); a, b = l['from'], l['to']; direct = dist(D[a], D[b])
    best = {a: 0}; pq = [(0, a, 0)]; found = None
    while pq:
        c, u, h = heapq.heappop(pq)
        if u == b: found = c; break
        if c > 1.5 * direct or h >= 4: continue
        for v, w, _ in g[u]:
            if c + w < best.get(v, 1e9): best[v] = c + w; heapq.heappush(pq, (c + w, v, h + 1))
    if found is not None and found <= 1.5 * direct: drop[key(l)] = 'shortcut of a path %d%% longer' % round(100 * found / direct - 100)
g = graph(set(drop)); seen = set()
major_sys = {s for l in L if l['tier'] == 'major' and key(l) not in drop for s in (l['from'], l['to'])}
for s in sorted(g):
    if s in seen or not g[s]: continue
    comp = [s]; seen.add(s); i = 0
    while i < len(comp):
        for v, _, _ in g[comp[i]]:
            if v not in seen: seen.add(v); comp.append(v)
        i += 1
    if len(comp) <= 4 and not (set(comp) & major_sys):
        for x in comp:
            for v, _, k in g[x]: drop.setdefault(k, 'loose group of %d systems' % len(comp))
par = {}
def find(x):
    par.setdefault(x, x)
    while par[x] != x: par[x] = par[par[x]]; x = par[x]
    return x
for l in sorted([l for l in L if key(l) not in drop], key=lambda l: (l['tier'] != 'major', dist(D[l['from']], D[l['to']]), key(l))):
    a, b = find(l['from']), find(l['to'])
    if a == b and l['tier'] == 'minor': drop[key(l)] = 'closes a loop'
    else: par[a] = b
while True:
    keep = [l for l in L if key(l) not in drop]
    deg = collections.Counter()
    for l in keep: deg[l['from']] += 1; deg[l['to']] += 1
    inner = collections.defaultdict(list)
    for l in keep:
        if l['tier'] == 'minor' and sec(l['from']) == sec(l['to']): inner[sec(l['from'])].append(l)
    changed = False
    for s in sorted(inner):
        ls = inner[s]
        if len(ls) <= CAP: continue
        leaves = [l for l in ls if deg[l['from']] == 1 or deg[l['to']] == 1]
        if not leaves: continue
        l = max(leaves, key=lambda l: (dist(D[l['from']], D[l['to']]), key(l)))
        drop[key(l)] = 'sector had more than %d short links' % CAP; changed = True
    if not changed: break

keep = [l for l in L if key(l) not in drop]
cols = list(L[0].keys())
with open(ROOT + 'extended-galaxy-v6-lanes.csv', 'w', newline='') as f:
    w = csv.DictWriter(f, cols, lineterminator='\n'); w.writeheader(); w.writerows(keep)
write_planets(keep)
print('kept %d of %d links (%s); systems on a route %d' % (len(keep), len(L), dict(collections.Counter(l['tier'] for l in keep)), len(nb)))
for k in sorted(drop): print('dropped %s - %s: %s' % (k[0], k[1], drop[k]))
