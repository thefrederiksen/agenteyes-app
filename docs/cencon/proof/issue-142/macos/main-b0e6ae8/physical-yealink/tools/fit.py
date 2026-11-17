# usage: fit.py <stop-lines-file> [extra "T,captured" ...]
# Refits yesterday's mic-deficit model and tests new points against it.
import re, sys, math
pts=[]
for l in open(sys.argv[1]):
    if "[INFO]" in l and "AudioCapture] Stop: wrote" in l:
        a,c=map(float,re.search(r"= ([\d.]+)s of audio over a ([\d.]+)s capture",l).groups()); pts.append((c,c-a))
def fit(p):
    n=len(p); mx=sum(x for x,_ in p)/n; my=sum(y for _,y in p)/n
    sxx=sum((x-mx)**2 for x,_ in p); b=sum((x-mx)*(y-my) for x,y in p)/sxx; a=my-b*mx
    s2=sum((y-a-b*x)**2 for x,y in p)/(n-2); return a,b,math.sqrt(s2/sxx),math.sqrt(s2),n,mx,sxx
tcrit={18:2.101,19:2.093,20:2.086,21:2.080,22:2.074,23:2.069}
a,b,se,s,n,mx,sxx=fit(pts)
print(f"YESTERDAY'S FIT (n={n}, f9ef230): deficit = {a:.4f} s + {b*1e6:.1f} ppm * T, slope SE {se*1e6:.1f} ppm, resid sd {s*1000:.1f} ms, t={b/se:.2f}")
new=[tuple(map(float,x.split(","))) for x in sys.argv[2:]]
for T,cap in new:
    d=T-cap; pred=a+b*T; t=tcrit[n-2]
    pi=t*s*math.sqrt(1+1/n+(T-mx)**2/sxx)
    print(f"  new point T={T:.2f}s captured={cap:.2f}s deficit={d:.3f}s | predicted {pred:.3f}s, 95% PREDICTION interval {pred-pi:.3f}..{pred+pi:.3f} -> {'INSIDE' if abs(d-pred)<=pi else 'OUTSIDE'} | implied slope (deficit-{a:.4f})/T = {(d-a)/T*1e6:.1f} ppm")
if new:
    a2,b2,se2,s2,n2,_,_=fit(pts+[(T,T-c) for T,c in new]); t=tcrit[n2-2]
    print(f"REFIT WITH NEW POINTS (n={n2}): deficit = {a2:.4f} s + {b2*1e6:.1f} ppm * T, slope SE {se2*1e6:.1f} ppm, 95% CI {(b2-t*se2)*1e6:.1f}..{(b2+t*se2)*1e6:.1f} ppm, t={b2/se2:.2f}")
