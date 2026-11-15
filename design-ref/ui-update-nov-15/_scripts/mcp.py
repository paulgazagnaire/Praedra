import json, sys, urllib.request
URL="http://127.0.0.1:29979/mcp"
READ_ONLY={"get_basic_info","get_children","get_tree_summary","get_jsx","get_computed_styles","get_node_info","export","find_nodes","get_tokens","list_comment_threads","get_comment_thread","get_screenshot","list_resources","get_fill_image","get_children"}
class C:
    def __init__(s):
        s.sid=None; s.n=0
        r=s.rpc("initialize",{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"export-script","version":"1"}})
        s.notify("notifications/initialized")
    def _post(s,body):
        h={"Content-Type":"application/json","Accept":"application/json, text/event-stream"}
        if s.sid: h["Mcp-Session-Id"]=s.sid
        req=urllib.request.Request(URL,data=json.dumps(body).encode(),headers=h)
        resp=urllib.request.urlopen(req,timeout=300)
        s.sid=resp.headers.get("Mcp-Session-Id") or s.sid
        raw=resp.read().decode()
        if "text/event-stream" in (resp.headers.get("Content-Type") or ""):
            out=None
            for line in raw.splitlines():
                if line.startswith("data:"):
                    d=json.loads(line[5:].strip())
                    if "id" in d: out=d
            return out
        return json.loads(raw) if raw.strip() else None
    def notify(s,m): s._post({"jsonrpc":"2.0","method":m})
    def rpc(s,m,p):
        s.n+=1; r=s._post({"jsonrpc":"2.0","id":s.n,"method":m,"params":p})
        if "error" in r: raise RuntimeError(r["error"])
        return r["result"]
    def tool(s,name,args):
        assert name in READ_ONLY, name
        return s.rpc("tools/call",{"name":name,"arguments":args})
