import json,os,sys,shutil
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from mcp import C
c=C(); F="01M4XB1NAX45Y7V9FT3DQMTQK1"; OUT="/Users/paulgazagnaire/Repos/paper-exports/2026-11-15-ui-update"
def pl(r): return json.loads([x["text"] for x in r["content"] if x.get("type")=="text"][-1])
K=["backgroundColor","borderTopWidth","borderBottomWidth","borderLeftWidth","borderRightWidth","borderWidth","borderColor","borderBottomColor","borderRadius","padding","paddingInline","gap","opacity","display","flexDirection","alignItems","justifyContent","backgroundImage"]
for root in ["SIG-0","NNY-0"]:
    print("==",root)
    ni=pl(c.tool("get_node_info",{"fileId":F,"nodeId":root})); top=ni["worldY"]; left=ni["worldX"]
    st=list(ni["childIds"]); seen=[]
    while st:
        n=st.pop(0); x=pl(c.tool("get_node_info",{"fileId":F,"nodeId":n}))
        if x["worldY"] is not None and x["worldY"]-top<=70 and x["component"]!="Text" and not x["name"].startswith("icon") and x["component"]!="Rectangle" or x["name"]=="GalaxyView" or (root=="SIG-0" and x["component"]=="Rectangle" and x["width"]>100):
            s=pl(c.tool("get_computed_styles",{"fileId":F,"nodeIds":[n]}))["styles"][n]
            print(f'{n} {x["name"]!r} {x["component"]} at {round(x["worldX"]-left)},{round(x["worldY"]-top)} {x["width"]}x{x["height"]}', {k:s[k] for k in K if k in s})
            if x["name"]=="GalaxyView" or x["worldY"]-top<=70:
                if root=="SIG-0" or x["name"]!="GalaxyView": st+= [ch for ch in (x.get("childIds") or [])] if x["component"]=="Frame" and not x["name"].startswith("icon") and x["name"]!="GalaxyView" else []
        elif x["worldY"] is not None and x["worldY"]-top<=70 and x["component"]=="Frame" and not x["name"].startswith("icon"):
            st+=x.get("childIds") or []
r=pl(c.tool("export",{"fileId":F,"nodes":{"NNY-0":[{"format":"png","scale":"1x"}]}}))["exports"][0]["filePath"]; shutil.move(r,f"{OUT}/source@1x.png")
