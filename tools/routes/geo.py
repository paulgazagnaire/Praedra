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
