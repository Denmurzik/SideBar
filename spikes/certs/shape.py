import json,sys,os,re
p=os.path.expandvars(r'%LOCALAPPDATA%\Google\Nearby\Sharing\preferences.json')
d=json.load(open(p,encoding='utf-8'))
def kind(v):
    if isinstance(v,str):
        if re.fullmatch(r'[A-Za-z0-9+/=_-]{16,}',v): return f'str[len={len(v)}] b64like'
        return f'str[len={len(v)}]'
    if isinstance(v,(int,float,bool)) or v is None: return type(v).__name__
    return None
def walk(v,path,depth=0):
    k=kind(v)
    if k: 
        # short strings that look like enum/names are not secret but be safe: print only kind
        print('  '*depth+path+': '+k); return
    if isinstance(v,dict):
        print('  '*depth+path+f': object[{len(v)}]')
        for kk,vv in v.items():
            # key names: mask if look like id (long/b64)
            name=kk if len(kk)<=48 and re.fullmatch(r'[A-Za-z0-9_.\-]+',kk) and not re.fullmatch(r'[0-9a-fA-F]{16,}',kk) else f'<key len={len(kk)}>'
            walk(vv,name,depth+1)
    elif isinstance(v,list):
        print('  '*depth+path+f': array[{len(v)}]')
        if v: walk(v[0],'[0]',depth+1)
walk(d,'root')
