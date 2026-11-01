"""Copy the looks Paul saved in the published Planet Maker ("Save for the game") into system-overrides.json.

Input: a JSON list of the records in the artifact's "systems" collection, each {name, code} (code = a praedra-planet preset)
or {name, reset: true} (back to the lore sheet). Prints the names whose look changed, one a line, for bake-thumbs.js.

    python3 apply-saved.py saved.json > /tmp/changed.txt
    python3 build.py planet-maker.src.html systems.json index.html
    mapfile -t names < /tmp/changed.txt; node bake-thumbs.js bake /tmp/planet-thumbs "${names[@]}"
    (one name a line, quoted: "Serra Alta" stays one name; unchanged pictures come from the current sheet)
    node bake-thumbs.js pack /tmp/planet-thumbs
"""
import json, os, sys

here = os.path.dirname(os.path.abspath(__file__))
names = {s['n'] for s in json.load(open(os.path.join(here, 'systems.json'), encoding='utf8'))}
path = os.path.join(here, 'system-overrides.json')
cur = json.load(open(path, encoding='utf8')) if os.path.exists(path) else {}
recs = json.load(open(sys.argv[1], encoding='utf8'))
new = dict(cur)
for r in recs:
    n = r.get('name')
    if n not in names:
        print('skipped, not a system: %r' % n, file=sys.stderr); continue
    if r.get('reset'):
        new.pop(n, None); continue
    p = json.loads(r['code'])['params']
    p['name'] = n
    new[n] = p
changed = sorted(n for n in set(cur) | set(new) if cur.get(n) != new.get(n))
with open(path, 'w', encoding='utf8') as f:
    json.dump(dict(sorted(new.items())), f, ensure_ascii=False, indent=1)
    f.write('\n')
if changed:
    print('\n'.join(changed))
print('%d saved looks, %d changed' % (len(new), len(changed)), file=sys.stderr)
