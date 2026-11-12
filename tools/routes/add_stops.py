"""Great routes stop at the systems they are drawn through (Paul 2026-11-12, decision card: "Add the stops").

A great-route link (tier major) drawn within NEAR map units of a system it does not stop at, away from its two ends, looks as if it
stops there. Judged on the map as the game draws it (each sector pulled 0.7 toward its own centre, GalaxyView.SectorPull). Each such
link A-B is split into A-C and C-B on the same route, C being the system closest to the line. A minor link already joining A-C or C-B
gives way to the great route (a pair of systems has one link only). Repeats until no great-route link passes a system, then writes
the lanes file and the planets' route columns back and prints every split. A second run finds nothing to do and changes nothing.
usage: python3 tools/routes/add_stops.py   (run from the repository root)
"""
import sys, os, csv, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geo import load, pt_seg, ROOT, write_planets

NEAR = 5.0  # map units as drawn; the decision card's figure (8 great-route links passed a system closer than this)
L, P, raw, D = load()
pair = lambda a, b: (a, b) if a < b else (b, a)
splits, given_way = [], []
while True:
    hit = None
    for i, l in enumerate(L):
        if l['tier'] != 'major': continue
        a, b = l['from'], l['to']
        near = sorted((round(d, 6), n, t) for n in P if n not in (a, b)
                      for d, t in [pt_seg(D[n], D[a], D[b])] if d < NEAR and 0.05 < t < 0.95)
        if near: hit = (i, near[0]); break
    if hit is None: break
    i, (d, c, t) = hit
    l = L[i]; a, b = l['from'], l['to']
    new = [dict(l, **{'from': a, 'to': c}), dict(l, **{'from': c, 'to': b})]
    taken = {pair(a, c), pair(c, b)}
    for x in [x for x in L if pair(x['from'], x['to']) in taken]:
        if x['tier'] == 'major': sys.exit('%s-%s already a great-route link; split by hand' % (x['from'], x['to']))
        given_way.append('%s-%s (%s, minor)' % (x['from'], x['to'], x['kind']))
    L = [x for j, x in enumerate(L) if j != i and pair(x['from'], x['to']) not in taken]
    L[i:i] = new
    splits.append('%s: %s - %s now stops at %s (drawn %.1f from the line)' % (l['routes'], a, b, c, d))
if not splits: print('nothing to do: no great-route link passes a system'); sys.exit(0)
with open(ROOT + 'extended-galaxy-v6-lanes.csv', 'w', newline='') as f:
    w = csv.DictWriter(f, list(L[0].keys()), lineterminator='\n'); w.writeheader(); w.writerows(L)
on = write_planets(L)
print('%d links (%s); systems on a route %d' % (len(L), dict(collections.Counter(x['tier'] for x in L)), on))
for s in splits: print('split', s)
for s in given_way: print('gave way', s)
