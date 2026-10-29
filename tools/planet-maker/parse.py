import re, json, sys
src = sys.argv[1]; out = sys.argv[2]
rows = []
for line in open(src, encoding='utf8'):
    if not line.startswith('| ') or '**World:**' not in line: continue
    cells = [c.strip() for c in line.strip().strip('|').split(' | ')]
    # cells: **name**, lore, description
    m = re.match(r'\*\*(.+?)\*\*', cells[0])
    if not m: continue
    d = cells[2]
    def f(k):
        mm = re.search(r'\*\*'+k+r':\*\*\s*(.*?)(?:<br>|$)', d)
        return mm.group(1).strip() if mm else ''
    rows.append({'n': m.group(1), 'sec': re.sub(r'\s*\(.*\)','',f('Sector')).replace(' Sector',''),
                 'w': f('World'), 's': f('From space'), 'm': f('Moons and rings'), 'r': f('Resources')})
json.dump(rows, open(out,'w',encoding='utf8'), ensure_ascii=False, separators=(',',':'))
print(len(rows))
