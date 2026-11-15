import json,os,sys
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from mcp import C
c=C(); F="01M4XB1NAX45Y7V9FT3DQMTQK1"; OUT="/Users/paulgazagnaire/Repos/paper-exports/2026-11-15-ui-update"
def pl(r): return json.loads([x["text"] for x in r["content"] if x.get("type")=="text"][-1])
def raw(r): return "\n".join(x["text"] for x in r["content"] if x.get("type")=="text")
def collect(root):
    nodes={}; order=[]; st=[root]
    while st:
        n=st.pop(); ni=pl(c.tool("get_node_info",{"fileId":F,"nodeId":n})); nodes[n]=ni; order.append(n); st+=list(reversed(ni.get("childIds") or []))
    cs={}
    for i in range(0,len(order),40): cs.update(pl(c.tool("get_computed_styles",{"fileId":F,"nodeIds":order[i:i+40]}))["styles"])
    return nodes,order,cs
# source candidate: Four windows (game HUD top bar)
os.makedirs(f"{OUT}/source",exist_ok=True)
open(f"{OUT}/source/four-windows.jsx.txt","w").write(raw(c.tool("get_jsx",{"fileId":F,"nodeId":"NNY-0","format":"inline-styles","includeNodeIds":True,"includeRootPositioning":True})))
res={}
for key,root in [("new","SIG-0"),("old","NNY-0")]:
    nodes,order,cs=collect(root); top=nodes[root]["worldY"]; left=nodes[root]["worldX"]
    rows=[]
    for n in order:
        x=nodes[n]
        if x.get("textContent") is None or x["worldY"] is None: continue
        if x["worldY"]-top>70: continue
        s=cs.get(n,{})
        rows.append({"text":x["textContent"],"x":round(x["worldX"]-left),"y":round(x["worldY"]-top),"w":x["width"],"h":x["height"],"font":s.get("fontFamily","").split(",")[0].strip('"'),"size":s.get("fontSize"),"weight":s.get("fontWeight"),"color":s.get("color"),"id":n})
    res[key]=sorted(rows,key=lambda r:r["x"])
    names=[nodes[n]["name"] for n in order]
    res[key+"_names"]=names
json.dump(res,open(f"{OUT}/source/hud-compare.json","w"),indent=1,ensure_ascii=False)
for k in ["old","new"]:
    print("==",k)
    for r in res[k]: print(f'{r["x"]:>5} {r["y"]:>3} {r["w"]:>5}x{r["h"]:<4} {r["font"]:<14} {r["size"]:<5} {r["weight"]} {r["color"]:<8} {json.dumps(r["text"],ensure_ascii=False)}')
sn=set(res["new_names"]); so=set(res["old_names"])
print("layer names shared (excluding generic):",sorted(n for n in sn&so if n not in ("Frame","Rectangle","1x","Text")))
