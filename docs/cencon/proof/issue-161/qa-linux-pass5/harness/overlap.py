# overlap.py <trx>: (start = endTime - duration: the TRX startTime is written at report time, start==end, measured) for each test in the loopback collection (LibraryRedirectTests, HeadlessControlServerFramingTests),
# how many tests of OTHER classes ran at the same time (intervals intersect). Prints the probe tests and a total.
import sys, re, xml.etree.ElementTree as ET
from datetime import datetime
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(sys.argv[1]).getroot()
cls = {u.get("id"): u.find("t:TestMethod", ns).get("className") for u in root.iter("{%s}UnitTest" % ns["t"])}
def ts(s):
    s = re.sub(r"(\.\d{6})\d*", r"\1", s); return datetime.fromisoformat(s)
rows = []
for r in root.iter("{%s}UnitTestResult" % ns["t"]):
    if r.get("outcome") == "NotExecuted": continue
    end = ts(r.get("endTime")); h, m, sec = r.get("duration").split(":"); from datetime import timedelta
    d = timedelta(hours=int(h), minutes=int(m), seconds=float(sec))
    rows.append((r.get("testName"), cls[r.get("testId")], end - d, end))  # TRX startTime is the REPORT time; start = end - duration
LOOP = ("LibraryRedirectTests", "HeadlessControlServerFramingTests")
inl = lambda c: c.endswith(LOOP)
loop = [x for x in rows if inl(x[1])]; other = [x for x in rows if not inl(x[1])]
tot = 0; withany = 0
for n, c, s, e in loop:
    k = sum(1 for _, oc, os_, oe in other if os_ < e and oe > s)
    tot += k; withany += k > 0
    if "AskRunningService" in n or "Framing" in c:
        print("  %-110s %6d ms  overlapped by %3d tests of other classes" % (n.split(".")[-1][:110], (e - s).total_seconds() * 1000, k))
span = lambda xs: (min(x[2] for x in xs), max(x[3] for x in xs))
ls, le = span(loop)
print("loopback-collection tests executed: %d; of them overlapped by another class's test: %d; total overlaps: %d" % (len(loop), withany, tot))
print("loopback collection span %s .. %s; other-class tests running inside that span: %d of %d" % (ls.time(), le.time(), sum(1 for x in other if x[2] < le and x[3] > ls), len(other)))
