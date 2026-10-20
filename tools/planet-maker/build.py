import sys,json
src,sysf,out=sys.argv[1:4]
s=open(src,encoding='utf8').read()
d=open(sysf,encoding='utf8').read()
assert '/*__SYSTEMS__*/[]' in s
s=s.replace('/*__SYSTEMS__*/[]', d.replace('</','<\\/'))
open(out,'w',encoding='utf8').write(s)
print(len(s))
