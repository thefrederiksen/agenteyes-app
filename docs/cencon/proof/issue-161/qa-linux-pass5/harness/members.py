# members.py <file-at-A> <file-at-B>: split a C# file into members (method/property/field/const/nested type, keyed by
# name + ordinal), compare each body text A vs B. Prints CHANGED/ADDED/REMOVED and the count of IDENTICAL.
import sys, re, hashlib
SIG = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|readonly|const|extern|sealed|override|async|unsafe|partial|new)\s+)+[\w<>\[\],.?() ]*?\b(\w+)\s*(?:\(|=|;|\{|=>|<)', re.M)
def members(text):
    lines = text.split("\n"); out = {}; i = 0; seen = {}
    while i < len(lines):
        m = SIG.match(lines[i])
        if m and not lines[i].strip().startswith("//"):
            name = m.group(1); start = i; depth = 0; body = []; opened = False
            while i < len(lines):
                l = re.sub(r'"(?:\\.|[^"\\])*"', '""', lines[i]); l = l.split("//")[0]
                body.append(lines[i]); depth += l.count("{") - l.count("}"); opened |= "{" in l
                if (opened and depth <= 0) or (not opened and (l.rstrip().endswith(";"))): break
                i += 1
            k = seen.get(name, 0); seen[name] = k + 1
            key = name if k == 0 else f"{name}#{k}"
            if "class " in lines[start] and opened:   # nested type: record header only, recurse not needed for this file
                pass
            out[key] = "\n".join(body)
        i += 1
    return out
a = members(open(sys.argv[1]).read()); b = members(open(sys.argv[2]).read())
same = [k for k in a if k in b and a[k] == b[k]]
for k in a:
    if k in b and a[k] != b[k]: print("  CHANGED ", k)
for k in b:
    if k not in a: print("  ADDED   ", k)
for k in a:
    if k not in b: print("  REMOVED ", k)
print("  IDENTICAL (%d): %s" % (len(same), ", ".join(sorted(same))))
