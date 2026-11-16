// Issue #156 pass 3, proof (3): one whole RepairService.RunAsync with the REAL probe over a library holding a
// crashed recording whose writer pid is another user's (pid 1). Compiled by no project. To re-run: copy it into
// tests/AgentEyes.Tests.Mac/ and run: dotnet test tests/AgentEyes.Tests.Mac -c Release --filter SweepProofScratch
// It asserts first that RecordingPaths.Root is inside the suite's redirected temp root (#161).
using System; using System.Collections.Generic; using System.IO; using System.Text.Json; using System.Threading.Tasks;
using AgentEyes; using Xunit; using Xunit.Abstractions;
namespace AgentEyes.Tests {
[Collection(CaptureOwnershipCollection.Name)]
public sealed class SweepProofScratch { private readonly ITestOutputHelper o; public SweepProofScratch(ITestOutputHelper o){this.o=o;}
 [Fact] public async Task RepairPass_ForeignPidCrash_EveryStageRunsForEveryOtherRecording() {
  string root = RecordingPaths.Root;
  Assert.StartsWith(AgentEyes.Tests.Mac.MacTestIsolation.RunRoot, root);   // #161: never the owner's ~/Movies
  o.WriteLine("root=" + root);
  CaptureLock.IsSameProcess = Video.FfmpegChildRegistry.IsSameProcess;
  Directory.CreateDirectory(root);
  string crashed = Path.Combine(root, "2026-11-15_120000_foreign"); Directory.CreateDirectory(crashed);
  File.WriteAllText(Path.Combine(crashed, "raw.mp4"), "x");
  ManifestStore.Replace(crashed, new Manifest { Mode="video", Label="video", CreatedUtc="2026-11-16T12:00:00Z",
    PendingMux = new Manifest.PendingMuxInfo { Mode="video", Source="mic", RawVideo="raw.mp4", FinalFile="recording.mp4" } });
  File.WriteAllText(Path.Combine(crashed, CaptureLock.FileName), "");
  File.WriteAllText(Path.Combine(crashed, CaptureLock.OwnerFileName), JsonSerializer.Serialize(new CaptureLock.OwnerRecord {
    Host=Environment.MachineName, Pid=int.MaxValue-11, What="capture session", StartedUtc=new DateTime(2026,11,15,12,0,0,DateTimeKind.Utc),
    Writers=new List<CaptureLock.WriterRecord>{ new(){Label="video", Pid=1, StartUtc=new DateTime(2026,11,15,12,0,1,DateTimeKind.Utc)} } }));
  string[] healthy = { "2026-11-14_090000_a", "2026-11-16_090000_b" };
  foreach (var h in healthy) { string d=Path.Combine(root,h); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d,"recording.mp4"),"v");
    ManifestStore.Replace(d, new Manifest{Mode="video", Label="video", CreatedUtc="2026-11-14T09:00:00Z", Title="t"}); }
  string queued = Path.Combine(root, healthy[1]);
  var resumed = new List<string>(); var thumbed = new List<string>(); var drained = new List<string>(); int housekept = 0;
  RepairService.ResumeStep = (d,_) => { lock(resumed) resumed.Add(Path.GetFileName(d)); return new PostRecordingOutcome(d) { Skipped = true }; };
  RepairService.ThumbStep = d => { thumbed.Add(Path.GetFileName(d)); return true; };
  RepairService.HousekeepStep = (r,s,y) => { housekept++; return new Housekeeping.HousekeepingReport{ Trigger="repair-pass" }; };
  PostRecordingQueue.Reset(); var realDispatch = PostRecordingQueue.Dispatcher; PostRecordingQueue.Dispatcher = _ => { }; var realRunner = PostRecordingQueue.Runner; PostRecordingQueue.Runner = d => drained.Add(Path.GetFileName(d));
  PostRecordingQueue.Enqueue(queued, "scratch: a refused sequence waiting for the drain");
  try {
   using var svc = new RepairService(() => false);
   svc.HousekeepingSettings = () => new Housekeeping.HousekeepingSettings { Enabled = true, ReportOnly = true };
   await svc.RunAsync("pass3-proof");
  } finally { RepairService.RestoreDefaultSteps(); PostRecordingQueue.Runner = realRunner; PostRecordingQueue.Dispatcher = realDispatch; PostRecordingQueue.Reset(); }
  o.WriteLine("resume (unfinished admitted): " + string.Join(",", resumed));
  o.WriteLine("thumbnail backfill: " + string.Join(",", thumbed));
  o.WriteLine("housekeeping runs: " + housekept);
  o.WriteLine("queue drain ran: " + string.Join(",", drained));
  string log = File.ReadAllText(Log.CurrentFile);
  foreach (var l in log.Split('\n')) if (l.Contains("trigger=pass3-proof")) o.WriteLine("log: " + l.Trim());
  Assert.Contains("2026-11-15_120000_foreign", resumed);
  Assert.Equal(healthy, thumbed.ToArray());
  Assert.Equal(1, housekept);
  Assert.Equal(new[]{healthy[1]}, drained.ToArray());
 } } }
