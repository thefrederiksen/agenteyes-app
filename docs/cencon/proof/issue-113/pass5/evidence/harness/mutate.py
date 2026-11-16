"""Apply one named mutation to RecordingService.cs (or restore it). Usage: mutate.py <repo> <name|restore>"""
import shutil, sys
repo, name = sys.argv[1], sys.argv[2]
p = repo + '/src/AgentEyes.Core/RecordingService.cs'
bak = p + '.mutbak'
if name == 'restore':
    shutil.move(bak, p)
    sys.exit(0)
shutil.copy(p, bak)
s = open(p, encoding='utf-8').read()

INNER = '''                catch (Exception readEx)
                {
                    Log.Error($"[RecordingService] OnWatchdogTick: the check of {_watchedFile} FAILED - counted as no "
                              + "growth; watching continues", readEx);
                    live = watchdog.CheckFailed(readEx);
                }'''
OUTER = '''                Log.Error("[RecordingService] OnWatchdogTick FAILED", ex);
'''
assert INNER in s and OUTER in s

if name == 'M1-inner-silent-return':
    # the read failure logs and returns without re-arming: the gate's original shape, at the read
    s = s.replace(INNER, '''                catch (Exception readEx)
                {
                    Log.Error("[RecordingService] OnWatchdogTick FAILED - the capture watchdog stopped checking", readEx);
                    return;
                }''')
elif name == 'M2-outer-silent-return':
    s = s.replace(OUTER, '''                Log.Error("[RecordingService] OnWatchdogTick FAILED - the capture watchdog stopped checking", ex);
                return;
''')
elif name == 'M3-original-shape':
    # exactly what the gate reviewed: no inner catch, outer catch logs only
    s = s.replace(INNER, '''                catch (Exception readEx) when (false)
                {
                    live = watchdog.CheckFailed(readEx);
                }''')
    s = s.replace(OUTER, '''                Log.Error("[RecordingService] OnWatchdogTick FAILED - the capture watchdog stopped checking", ex);
                return;
''')
else:
    raise SystemExit('unknown mutation ' + name)
open(p, 'w', encoding='utf-8', newline='').write(s)
