#!/usr/bin/env python3
import re,subprocess,sys
# QA #173: every identity form searched, whole tree, every blob. usage: qa-forms.py <rev> <owner-file> <second-file> <username>
# The values are read from files outside the repo and never printed.
rev=sys.argv[1]; owner=open(sys.argv[2]).read().strip(); second=open(sys.argv[3]).read().strip(); u=sys.argv[4]
def esc(s): return re.escape(s)
forms={}
for tag,a in (("owner",owner),("second",second)):
    l,d=a.split("@")
    for fn,at in (("plain","@"),("url %40","%40"),("json \\u0040",r"\\u0040"),("html &#64;","&#64;"),("html &#x40;","&#x40;"),("html &commat;","&commat;"),("[at] obfusc",r" ?[\[\(]at[\]\)] ?"),("dbl-url %2540","%2540")):
        forms[f"{tag} {fn}"]=re.compile(esc(l)+(at if fn in("json \\u0040","[at] obfusc") else esc(at))+esc(d),re.I)
    forms[f"{tag} local-part+@ any"]=re.compile(esc(l)+r"(@|%40|%2540|\\u0040|&#64;|&#x40;)",re.I)
sep=r"(?:/|\\{1,4}|%2F|%5C|%255C|\\u002f|\\u005c)"
forms["home: /home/<u>"]=re.compile(r"/home/"+u+r"(?![A-Za-z0-9])",re.I)
forms["home: /Users/<u>"]=re.compile(r"/Users/"+u+r"(?![A-Za-z0-9])",re.I)
forms["home: Users\\<u> (1-4 backslashes)"]=re.compile(r"Users\\{1,4}"+u+r"(?![A-Za-z0-9])",re.I)
forms["home: any sep Users|home <sep> <u> (url/json-unicode too)"]=re.compile(r"(?:Users|home)"+sep+u+r"(?![A-Za-z0-9])",re.I)
forms["home: ~<u>"]=re.compile(r"~"+u+r"(?![A-Za-z0-9])",re.I)
forms["home: 8.3 short <USERNAME>~1"]=re.compile(u+r"~\d",re.I)
files=subprocess.run(["git","ls-tree","-r","--name-only",rev],capture_output=True,text=True).stdout.split()
tot={k:[0,set()] for k in forms}
for f in files:
    b=subprocess.run(["git","cat-file","blob",f"{rev}:{f}"],capture_output=True).stdout
    for enc in ("utf-8","utf-16-le"):
        try: t=b.decode(enc) if enc=="utf-8" else (b.decode(enc) if b[:2]==b"\xff\xfe" else "")
        except UnicodeDecodeError: t=b.decode("latin-1") if enc=="utf-8" else ""
        for k,r in forms.items():
            n=len(r.findall(t))
            if n: tot[k][0]+=n; tot[k][1].add(f)
print(f"rev={rev} files_scanned={len(files)}")
for k,(n,fs) in tot.items():
    print(f"  {k:55s} occ={n:5d} files={len(fs)}" + ("  -> "+", ".join(sorted(fs)[:4]) if fs and len(fs)<5 else ""))
