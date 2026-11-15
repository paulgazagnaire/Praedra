import json,os,sys,shutil,re,subprocess,difflib
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from mcp import C
F="01M4XB1NAX45Y7V9FT3DQMTQK1"; PAGE="p-3-1"; ROOT="SIG-0"
OUT="/Users/paulgazagnaire/Repos/paper-exports/2026-11-15-ui-update"
c=C()
def texts(r): return [x["text"] for x in r["content"] if x.get("type")=="text"]
def pl(r): return json.loads(texts(r)[-1])
def raw(r): return "\n".join(texts(r))
def w(p,s): os.makedirs(os.path.dirname(p),exist_ok=True); open(p,"w").write(s)
def slug(s): return re.sub(r"[^A-Za-z0-9]+","-",s).strip("-").lower() or "layer"
bi=pl(c.tool("get_basic_info",{"fileId":F,"pageId":PAGE})); w(f"{OUT}/_page/get_basic_info.json",json.dumps(bi,indent=1))
w(f"{OUT}/_page/page-index.txt","\n".join(["name\tid\tx\ty\twidth\theight"]+[f'{a["name"]}\t{a["id"]}\t{a["worldX"]}\t{a["worldY"]}\t{a["width"]}\t{a["height"]}' for a in bi["artboards"]])+"\n")
w(f"{OUT}/_page/comments.frame.raw.txt",raw(c.tool("list_comment_threads",{"fileId":F,"nodeId":ROOT,"status":"all","limit":200,"previewLength":1000})))
w(f"{OUT}/_page/comments.page.raw.txt",raw(c.tool("list_comment_threads",{"fileId":F,"pageId":PAGE,"status":"all","limit":200,"previewLength":1000})))
nodes={}; order=[]
def walk(nid,d,parent):
    ni=pl(c.tool("get_node_info",{"fileId":F,"nodeId":nid})); ni["_depth"]=d; nodes[nid]=ni; order.append(nid)
    for ch in ni.get("childIds") or []: walk(ch,d+1,nid)
walk(ROOT,0,None)
cs={}
for i in range(0,len(order),40): cs.update(pl(c.tool("get_computed_styles",{"fileId":F,"nodeIds":order[i:i+40]}))["styles"])
def build(n):
    x=nodes[n]; return {"id":n,"name":x["name"],"type":x["component"],"x":x["x"],"y":x["y"],"worldX":x["worldX"],"worldY":x["worldY"],"width":x["width"],"height":x["height"],"visible":x["isVisible"],"locked":x["isLocked"],"text":x.get("textContent"),"styles":cs.get(n,{}),"children":[build(ch) for ch in x.get("childIds") or []]}
w(f"{OUT}/tree.json",json.dumps(build(ROOT),indent=1,ensure_ascii=False))
w(f"{OUT}/jsx.txt",raw(c.tool("get_jsx",{"fileId":F,"nodeId":ROOT,"format":"inline-styles","includeNodeIds":True,"includeRootPositioning":True})))
w(f"{OUT}/tree-summary.txt",raw(c.tool("get_tree_summary",{"fileId":F,"nodeId":ROOT,"depth":10})))
tx=[f'{n}\t{nodes[n]["name"]}\t{json.dumps(nodes[n]["textContent"],ensure_ascii=False)}' for n in order if nodes[n].get("textContent") is not None]
w(f"{OUT}/texts.txt","id\tlayer name\ttext\n"+"\n".join(tx)+"\n")
def export(nid,fmt,scale,dest):
    r=c.tool("export",{"fileId":F,"nodes":{nid:[{"format":fmt,"scale":scale}]}})
    ex=pl(r).get("exports",[])
    if ex and ex[0].get("filePath") and os.path.exists(ex[0]["filePath"]): shutil.move(ex[0]["filePath"],dest); return True
    return raw(r)
print("frame",export(ROOT,"png","2x",f"{OUT}/frame@2x.png"))
for i,ch in enumerate(nodes[ROOT]["childIds"]):
    print(ch,export(ch,"png","2x",f"{OUT}/children/{i+1:02d}_{slug(nodes[ch]['name'])}_{ch}@2x.png"))
types={}
for n in order: types[nodes[n]["component"]]=types.get(nodes[n]["component"],0)+1
print("types",types)
# SVG: only SVG nodes; icons (frames named icon-*) as SVG if SVG, else 4x PNG
for n in order:
    nm=nodes[n]["name"]; t=nodes[n]["component"]
    if t=="SVG":
        r=export(n,"svg","1x",f"{OUT}/svgs/{slug(nm)}_{n}.svg"); print("svg",n,nm,r if r is not True else "ok")
    elif nm.startswith("icon-"):
        r=export(n,"png","8x",f"{OUT}/svgs/{slug(nm)}_{n}@8x.png"); print("icon png",n,nm,r if r is not True else "ok")
# image fills
for n in order:
    if "url(" in json.dumps(cs.get(n,{})):
        fi=c.tool("get_fill_image",{"fileId":F,"nodeId":n})
        for t in texts(fi):
            try: j=json.loads(t)
            except Exception: continue
            if j.get("originalUrl"):
                os.makedirs(f"{OUT}/images",exist_ok=True)
                d=f"{OUT}/images/{slug(nodes[n]['name'])}_{n}{os.path.splitext(j['originalUrl'])[1]}"
                subprocess.run(["curl","-sSf","-o",d,j["originalUrl"]],check=True); print("image",n,j["originalUrl"])
print(len(order),"nodes",len(tx),"texts")
