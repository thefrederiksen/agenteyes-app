import re,subprocess,sys
# QA #173: the same values searched across LINE WRAPS (console-width wrapping). usage: qa-wrapped.py <rev> <owner-file> <second-file> <username>
rev=sys.argv[1]; owner=open(sys.argv[2]).read().strip(); second=open(sys.argv[3]).read().strip(); u=sys.argv[4]
W=r"(?:\r?\n[ \t]*)?"   # an optional line wrap (with optional indent) between ANY two characters
def wrapped(s): return W.join(re.escape(c) for c in s)
pats={"owner":re.compile(wrapped(owner),re.I),"second":re.compile(wrapped(second),re.I),
 "home Users<sep><user>":re.compile(wrapped("Users")+W+r"(?:\\{1,4}|/)"+W+wrapped(u)+r"(?![A-Za-z0-9])",re.I),
 "home /home/<user>":re.compile(wrapped("/home/"+u)+r"(?![A-Za-z0-9])",re.I)}
files=subprocess.run(["git","ls-tree","-r","--name-only",rev],capture_output=True,text=True).stdout.split()
for f in files:
    t=subprocess.run(["git","cat-file","blob",f"{rev}:{f}"],capture_output=True).stdout.decode("latin-1")
    for k,p in pats.items():
        for m in p.finditer(t):
            if "\n" in m.group(0):
                print(f"WRAPPED {k}: {f}:{t.count(chr(10),0,m.start())+1}")
