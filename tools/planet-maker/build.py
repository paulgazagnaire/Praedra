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
open(out,'w',encoding='utf8').write(s)
print(len(s))
