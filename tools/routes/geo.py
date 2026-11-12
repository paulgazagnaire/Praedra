import csv,collections,math
import os
ROOT=os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','data','design')+os.sep
def load():
    L=list(csv.DictReader(open(ROOT+'extended-galaxy-v6-lanes.csv')))
    P={r['name']:r for r in csv.DictReader(open(ROOT+'extended-galaxy-v6-planets.csv'))}
    raw={n:(float(r['map_x']),float(r['map_y'])) for n,r in P.items()}
    sec=collections.defaultdict(list)
    for n,r in P.items():
        if r['sector']: sec[r['sector']].append(n)
    drawn=dict(raw)
    for s,mem in sec.items():
        cx=sum(raw[m][0] for m in mem)/len(mem); cy=sum(raw[m][1] for m in mem)/len(mem)
        for m in mem: drawn[m]=(cx+(raw[m][0]-cx)*0.7, cy+(raw[m][1]-cy)*0.7)
    return L,P,raw,drawn
def seg_cross(p1,p2,p3,p4):
    def o(a,b,c): return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
    d1,d2,d3,d4=o(p3,p4,p1),o(p3,p4,p2),o(p1,p2,p3),o(p1,p2,p4)
    return d1*d2<0 and d3*d4<0
def pt_seg(p,a,b):
    ax,ay=a;bx,by=b;px,py=p
    dx,dy=bx-ax,by-ay; t=max(0,min(1,((px-ax)*dx+(py-ay)*dy)/(dx*dx+dy*dy)))
    return math.hypot(px-ax-t*dx,py-ay-t*dy), t
def dist(a,b): return math.hypot(a[0]-b[0],a[1]-b[1])
def write_planets(keep):
    """Write the planets file back with its route columns recomputed from the links in keep, as the loader checks them
    (lane_count; chokepoint = a system whose loss splits its own route network) plus on_route, major_routes, best_lane_tier."""
    nb=collections.defaultdict(set)
    for l in keep: nb[l['from']].add(l['to']); nb[l['to']].add(l['from'])
    def comps(excl):
        seen,n=set(),0
        for s in nb:
            if s==excl or s in seen: continue
            n+=1; st=[s]; seen.add(s)
            while st:
                u=st.pop()
                for v in nb[u]:
                    if v!=excl and v not in seen: seen.add(v); st.append(v)
        return n
    base=comps(None)
    rows=list(csv.DictReader(open(ROOT+'extended-galaxy-v6-planets.csv',newline='')))
    pc=list(rows[0].keys())
    for r in rows:
        n=r['name']; ls=[l for l in keep if n in (l['from'],l['to'])]
        routes=sorted({x for l in ls if l['routes'] for x in l['routes'].split(';')})
        r['lane_count']=str(len(ls)); r['on_route']='1' if ls else '0'
        r['major_routes']=';'.join(routes)
        r['best_lane_tier']='major' if any(l['tier']=='major' for l in ls) else ('minor' if ls else '')
        r['chokepoint']='1' if ls and comps(n)>base-(1 if len(nb[n])==0 else 0) and len(nb[n])>1 else '0'
    with open(ROOT+'extended-galaxy-v6-planets.csv','w',newline='') as f:
        w=csv.DictWriter(f,pc,lineterminator='\n'); w.writeheader(); w.writerows(rows)
    return len(nb)
