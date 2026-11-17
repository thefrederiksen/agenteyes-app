#!/usr/bin/env python3
# Issue #173 QA round 1 bounce: count every identity form in EVERY blob of <rev>, single-line AND across a
# console line wrap (CRLF/LF plus indentation between any two characters). Values come from files kept
# outside the repository; run the output through sed to mask the username before committing it.
import re,subprocess,sys
# usage: measure.py <rev> <owner-file> <second-file> <username>   (values never printed)
rev=sys.argv[1]; owner=open(sys.argv[2]).read().strip(); second=open(sys.argv[3]).read().strip(); u=sys.argv[4]
W=r"(?:\r?\n[ \t]*)?"
def wr(s): return W.join(re.escape(c) for c in s)
def enc(a):  # @ in its encoded forms
    l,d=a.split("@"); return re.escape(l)+r"(?:@|%40|%2540|\\u0040|&#64;|&#x40;|&commat;)"+re.escape(d)
def wenc(a):
    l,d=a.split("@"); return wr(l)+W+r"(?:@|%"+W+"4"+W+"0)"+W+wr(d)
pats={
 "owner address (plain+encoded)":(re.compile(enc(owner),re.I),re.compile(wenc(owner),re.I)),
 "second address (plain+encoded)":(re.compile(enc(second),re.I),re.compile(wenc(second),re.I)),
 "/home/<username>":(re.compile(r"/home/"+re.escape(u)+r"(?![A-Za-z0-9])",re.I),re.compile(wr("/home/"+u)+r"(?![A-Za-z0-9])",re.I)),
 "/Users/<username>":(re.compile(r"/Users/"+re.escape(u)+r"(?![A-Za-z0-9])",re.I),re.compile(wr("/Users/"+u)+r"(?![A-Za-z0-9])",re.I)),
 "Users\\+<username>":(re.compile(r"Users\\{1,4}"+re.escape(u)+r"(?![A-Za-z0-9])",re.I),re.compile(wr("Users")+W+r"(?:\\"+W+r"){1,4}"+wr(u)+r"(?![A-Za-z0-9])",re.I)),
}
files=subprocess.run(["git","ls-tree","-r","--name-only",rev],capture_output=True,text=True).stdout.split("\n")
res={k:[0,set(),0,set(),[]] for k in pats}
for f in filter(None,files):
    t=subprocess.run(["git","cat-file","blob",f"{rev}:{f}"],capture_output=True).stdout.decode("latin-1")
    for k,(p,pw) in pats.items():
        n=len(p.findall(t))
        if n: res[k][0]+=n; res[k][1].add(f)
        for m in pw.finditer(t):
            if "\n" in m.group(0):
                res[k][2]+=1; res[k][3].add(f); res[k][4].append(f"{f}:{t.count(chr(10),0,m.start())+1}")
print(f"rev {rev}: {len([f for f in files if f])} blobs")
for k,(n,fs,w,wf,loc) in res.items():
    print(f"  {k:34s} single-line {n:5d} occ / {len(fs):3d} files | WRAPPED {w} occ / {len(wf)} files {loc}")
