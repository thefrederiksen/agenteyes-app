import re,sys
t=open(sys.argv[1]).read()
for name in ("PinnedNativeImports","PinnedFileWrites"):
    m=re.search(name+r"\s*=\s*\{(.*?)\n\s*\};",t,re.S)
    for s in re.findall(r'^\s*"((?:[^"\\]|\\.)*)"',m.group(1),re.M): print(("IMPORT " if "Native" in name else "WRITE ")+s)
