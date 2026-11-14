# Read-only export of Paper frame "ui-inspo" (Praedra / Art-Direction).
import json, os, sys, shutil, re, base64, subprocess
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp import C
F="01M4XB1NAX45Y7V9FT3DQMTQK1"; PAGE="p-4-0"; ROOT="Q33-0"
OUT="/Users/paulgazagnaire/Repos/paper-exports/2026-11-14-ui-inspo"
c=C()
def texts(r): return [x["text"] for x in r["content"] if x.get("type")=="text"]
def payload(r): return texts(r)[-1]
def raw(r): return "\n".join(texts(r))
def w(p,s): os.makedirs(os.path.dirname(p),exist_ok=True); open(p,"w").write(s)
def slug(s): return re.sub(r"[^A-Za-z0-9]+","-",s).strip("-").lower()
r=c.tool("get_basic_info",{"fileId":F,"pageId":PAGE}); w(f"{OUT}/_page/get_basic_info.raw.txt",raw(r))
bi=json.loads(payload(r))
w(f"{OUT}/page-index.txt","\n".join(["name\tid\tx\ty\twidth\theight"]+[f'{a["name"]}\t{a["id"]}\t{a["worldX"]}\t{a["worldY"]}\t{a["width"]}\t{a["height"]}' for a in bi["artboards"]])+"\n")
w(f"{OUT}/comments/list_comment_threads.page.all.raw.txt",raw(c.tool("list_comment_threads",{"fileId":F,"pageId":PAGE,"status":"all","limit":200,"previewLength":1000})))
w(f"{OUT}/comments/list_comment_threads.frame.all.raw.txt",raw(c.tool("list_comment_threads",{"fileId":F,"nodeId":ROOT,"status":"all","limit":200,"previewLength":1000})))
w(f"{OUT}/comments/list_comment_threads.file.all.raw.txt",raw(c.tool("list_comment_threads",{"fileId":F,"status":"all","limit":200,"previewLength":1000})))
th=json.loads(payload(c.tool("list_comment_threads",{"fileId":F,"pageId":PAGE,"status":"all","limit":200})))
for t in (th.get("threads") or th.get("commentThreads") or []):
    tid=t.get("id") or t.get("commentThreadId")
    w(f"{OUT}/comments/thread-{tid}.raw.txt",raw(c.tool("get_comment_thread",{"fileId":F,"commentThreadId":tid})))
nodes={}; order=[]
def walk(nid,d):
    ni=json.loads(payload(c.tool("get_node_info",{"fileId":F,"nodeId":nid}))); ni["_depth"]=d; nodes[nid]=ni; order.append(nid)
    for ch in ni.get("childIds") or []: walk(ch,d+1)
walk(ROOT,0)
with open(f"{OUT}/node-info.jsonl","w") as fh:
    for n in order: fh.write(json.dumps(nodes[n],ensure_ascii=False)+"\n")
cs={}; rc=[]
for i in range(0,len(order),40):
    r=c.tool("get_computed_styles",{"fileId":F,"nodeIds":order[i:i+40]}); rc.append(raw(r)); p=json.loads(payload(r)); cs.update(p.get("styles",p))
w(f"{OUT}/computed-styles.raw.txt","\n\n".join(rc))
w(f"{OUT}/jsx.txt",raw(c.tool("get_jsx",{"fileId":F,"nodeId":ROOT,"format":"inline-styles","includeRootPositioning":True})))
w(f"{OUT}/tree-summary.txt",raw(c.tool("get_tree_summary",{"fileId":F,"nodeId":ROOT,"depth":10})))
tx=[f'{n}\t{nodes[n]["name"]}\t{json.dumps(nodes[n]["textContent"],ensure_ascii=False)}' for n in order if nodes[n].get("textContent") is not None]
w(f"{OUT}/texts.txt","id\tlayer name\ttext (JSON-escaped, verbatim)\n"+"\n".join(tx)+"\n")
# frame PNGs
os.makedirs(f"{OUT}/png/children",exist_ok=True); os.makedirs(f"{OUT}/originals",exist_ok=True)
def export(nid,scales,dest):
    r=c.tool("export",{"fileId":F,"nodes":{nid:[{"format":"png","scale":s} for s in scales]}})
    ex=json.loads(payload(r)).get("exports",[]); out=[]
    for s,e in zip(scales,ex):
        p=e.get("filePath")
        if p and os.path.exists(p): d=dest.format(s=s); shutil.move(p,d); out.append(d)
    return raw(r),out
lg=[]
lg.append(export(ROOT,["1x","2x"],f"{OUT}/png/{ROOT}__ui-inspo@{{s}}.png")[0])
rows=["id\tlayer name\tx\ty\twidth\theight\toriginal url\toriginal file\toriginal px"]
for i,ch in enumerate(nodes[ROOT]["childIds"]):
    n=nodes[ch]; base=f"{i+1:02d}_{ch}__{slug(n['name'])}"
    lg.append(export(ch,["2x"],f"{OUT}/png/children/{base}@{{s}}.png")[0])
    fi=c.tool("get_fill_image",{"fileId":F,"nodeId":ch}); meta={}
    for t in texts(fi):
        try:
            j=json.loads(t)
            if "originalUrl" in j: meta=j
        except Exception: pass
    of=""; opx=""
    if meta.get("originalUrl"):
        ext=os.path.splitext(meta["originalUrl"])[1] or ".bin"; of=f"originals/{base}{ext}"
        subprocess.run(["curl","-sSf","-o",f"{OUT}/{of}",meta["originalUrl"]],check=True)
    rows.append(f'{ch}\t{n["name"]}\t{n["x"]}\t{n["y"]}\t{n["width"]}\t{n["height"]}\t{meta.get("originalUrl","")}\t{of}\t')
    print(i+1,ch,n["name"],bool(of),flush=True)
w(f"{OUT}/children-index.tsv","\n".join(rows)+"\n")
w(f"{OUT}/_page/export.raw.txt","\n\n".join(lg))
print(len(order),"nodes",len(tx),"texts")
