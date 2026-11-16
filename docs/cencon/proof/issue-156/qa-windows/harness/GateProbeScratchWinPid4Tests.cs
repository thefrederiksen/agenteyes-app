// The Review Gate's scratch reproduction for #156 (rejection 6918963862), copied UNCHANGED below this
// header from its scratchpad as pass 3's starting point. Compiled by no project. To re-run: copy it into
// tests/AgentEyes.Tests.Mac/ and run: dotnet test tests/AgentEyes.Tests.Mac -c Release --filter GateProbeScratch
// (original first line: GATE SCRATCH - never committed. Real process probe, no seam on IsSameProcess.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgentEyes;
using Xunit;
using Xunit.Abstractions;

namespace AgentEyes.Tests
{
    [Collection(CaptureOwnershipCollection.Name)]
    public sealed class GateProbeScratchWinPid4Tests : IDisposable
    {
        private readonly ITestOutputHelper _o;
        private readonly string _root;
        private readonly Func<string> _realHostId = CaptureLock.ThisHostId;
        public GateProbeScratchWinPid4Tests(ITestOutputHelper o)
        {
            _o = o;
            _root = Path.Combine(Path.GetTempPath(), "gate-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            CaptureLock.ThisHostId = () => "00000000000000000000000000000001";
        }
        public void Dispose()
        {
            CaptureLock.ThisHostId = _realHostId;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string Crashed(string name, int writerPid)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "raw.mp4"), "x");
            ManifestStore.Replace(dir, new Manifest
            {
                Mode = "video", Label = "video", CreatedUtc = "2026-11-16T12:00:00Z",
                PendingMux = new Manifest.PendingMuxInfo { Mode = "video", Source = "mic", RawVideo = "raw.mp4", FinalFile = "recording.mp4" },
            });
            File.WriteAllText(Path.Combine(dir, CaptureLock.FileName), "");
            var owner = new CaptureLock.OwnerRecord
            {
                Host = Environment.MachineName, Pid = 424242, What = "capture session",
                StartedUtc = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
                Writers = new List<CaptureLock.WriterRecord> { new() { Label = "video", Pid = writerPid, StartUtc = new DateTime(2026, 11, 15, 12, 0, 1, DateTimeKind.Utc) } },
            };
            File.WriteAllText(Path.Combine(dir, CaptureLock.OwnerFileName), JsonSerializer.Serialize(owner));
            return dir;
        }

        private string Try(string what, Func<string> f)
        {
            string r;
            try { r = "OK: " + f(); } catch (Exception ex) { r = "THREW " + ex.GetType().Name + ": " + ex.Message; }
            _o.WriteLine(what + " -> " + r);
            return r;
        }

        [Fact]
        public void Control_WriterPidIsGone_StaleAndAdmitted()
        {
            string dir = Crashed("2026-11-15_120000_gone", 999999);
            Assert.StartsWith("OK: False", Try("Inspect(pid gone)", () => { var l = CaptureLock.Inspect(dir); return l.Live + " " + l.Reason; }));
            Assert.Equal("OK: 1", Try("FindUnfinished", () => PostRecordingPlan.FindUnfinished(_root).Count.ToString()));
            Assert.StartsWith("OK:", Try("FindMissing", () => Thumbnails.FindMissing(_root).Count.ToString()));
        }

        [Fact]
        public void Control_WriterPidReusedByMyOwnProcess_StaleAndAdmitted()
        {
            string dir = Crashed("2026-11-15_120000_mine", Environment.ProcessId);
            Assert.StartsWith("OK: False", Try("Inspect(pid reused, same user)", () => { var l = CaptureLock.Inspect(dir); return l.Live + " " + l.Reason; }));
        }

        [Fact]
        public void WriterPidReusedByAnotherUsersProcess_Observed()
        {
            string healthy = Crashed("2026-11-14_090000_healthy", 999999);   // sorts first
            string dir = Crashed("2026-11-15_120000_rootpid", 4);            // pid 1 = launchd, root
            Try("Inspect(pid 4)", () => { var l = CaptureLock.Inspect(dir); return l.Live + " " + l.Reason; });
            Try("ClearStaleClaim(pid 4)", () => CaptureLock.ClearStaleClaim(dir, "gate").ToString());
            Try("ThrowIfLive(pid 4)", () => { CaptureLock.ThrowIfLive(dir, "finalize"); return "passed"; });
            Try("FindUnfinished(root with healthy + pid4)", () => string.Join(",", System.Linq.Enumerable.Select(PostRecordingPlan.FindUnfinished(_root), Path.GetFileName)));
            Try("Thumbnails.FindMissing(root with healthy + pid4)", () => Thumbnails.FindMissing(_root).Count.ToString());
            Try("Thumbnails.NeedsThumb(healthy alone)", () => Thumbnails.NeedsThumb(healthy).ToString());
        }

        [Fact]
        public void Reap_OwnerPidReusedByAnotherUsersProcess_Observed()
        {
            string folder = Path.Combine(_root, "registry");
            Directory.CreateDirectory(folder);
            var e = new Video.FfmpegChildRegistry.Entry { Pid = 999998, StartUtc = DateTime.UtcNow.AddDays(-1), OwnerPid = 4, OwnerStartUtc = DateTime.UtcNow.AddDays(-1), Label = "capture video (x)", CaptureWriter = true };
            File.WriteAllText(Path.Combine(folder, "999998.json"), JsonSerializer.Serialize(e));
            Try("ReapOrphansIn(owner pid 1)", () => string.Join(" | ", Video.FfmpegChildRegistry.ReapOrphansIn(folder)));
            _o.WriteLine("record still on disk: " + File.Exists(Path.Combine(folder, "999998.json")));
        }
    }
}
