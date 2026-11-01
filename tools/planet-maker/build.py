import sys,json
src,sysf,out=sys.argv[1:4]
s=open(src,encoding='utf8').read()
d=open(sysf,encoding='utf8').read()
assert '/*__SYSTEMS__*/[]' in s
s=s.replace('/*__SYSTEMS__*/[]', d.replace('</','<\\/'))
if '/*__PRESET_BASE__*/{}' in s:
    import os
    b=open(os.path.join(os.path.dirname(sysf) or '.','preset-base.json'),encoding='utf8').read()
    s=s.replace('/*__PRESET_BASE__*/{}', b.replace('</','<\\/'))
if '/*__OVERRIDES__*/{}' in s:
    import os
    f=os.path.join(os.path.dirname(sysf) or '.','system-overrides.json')
    o=open(f,encoding='utf8').read() if os.path.exists(f) else '{}'
    s=s.replace('/*__OVERRIDES__*/{}', o.strip().replace('</','<\\/'))
open(out,'w',encoding='utf8').write(s)
print(len(s))
