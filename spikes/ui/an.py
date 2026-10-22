import sys,csv
def load(p):
    pts=[];cmds=[]
    for r in csv.reader(open(p)):
        if len(r)<3: continue
        t=float(r[0])
        if r[1]=='cmd': cmds.append((t,r[2]))
        elif r[1]=='x': pts.append((t,int(r[2])))
    return pts,cmds
pts,cmds=load(sys.argv[1])
print("cmds",[(round(t),c) for t,c in cmds])
# visible width = 1920-x (when found)
vis=[(t,(1920-x) if x>=0 else 0) for t,x in pts]
print("samples",len(vis),"rate/s",round(len(vis)/(vis[-1][0]/1000)))
# print compact trace around each command
for t0,c in cmds:
    seg=[(round(t-t0),v) for t,v in vis if t0-60<=t<=t0+700]
    print(c,"@",round(t0),":"," ".join(f"{a}:{b}" for a,b in seg[::3]))
