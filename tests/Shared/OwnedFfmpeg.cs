using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentEyes.Video;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #151: the ffmpeg processes ONE test started through the product runner, and the only
    /// ones that test's failure path may kill.
    ///
    /// The failure path it replaces swept every process named ffmpeg on the machine and killed those
    /// that had started since the test began. Start time is not ownership: on a developer machine the
    /// recorder may be muxing a live recording while the gate runs, and that sweep would have killed
    /// its ffmpeg mid-write. So this records the pid and start time of each child that
    /// <see cref="Ffmpeg"/> starts in this test's async flow (through
    /// <see cref="Ffmpeg.ChildStartedObserver"/>) and <see cref="KillOwned()"/> kills exactly those -
    /// and only when the pid still names the SAME process (start time matches), so a reused pid is
    /// never killed. A process it cannot positively prove is its own is left alone.
    ///
    /// Use: <c>using var owned = new OwnedFfmpeg();</c> before starting the run (also before a
    /// Task.Run that starts it - the observer flows into it), then <c>owned.KillOwned()</c> on the
    /// path where the run did not return.
    /// </summary>
    internal sealed class OwnedFfmpeg : IDisposable
    {
        /// <summary>One child this test started. <see cref="StartUtc"/> is null when it had already
        /// exited before its start time could be read - then there is nothing left to kill.</summary>
        internal sealed record Child(int Pid, DateTime? StartUtc);

        private readonly object _gate = new();
        private readonly List<Child> _children = new();
        private readonly Action<Process>? _previous;

        public OwnedFfmpeg()
        {
            _previous = Ffmpeg.ChildStartedObserver.Value;
            Ffmpeg.ChildStartedObserver.Value = Record;
        }

        /// <summary>The children recorded so far, in start order.</summary>
        public IReadOnlyList<Child> Children
        {
            get { lock (_gate) return _children.ToArray(); }
        }

        private void Record(Process p)
        {
            DateTime? start = null;
            try { start = p.StartTime.ToUniversalTime(); }
            catch (InvalidOperationException) { }   // exited and reaped already: nothing to kill later
            lock (_gate) _children.Add(new Child(p.Id, start));
        }

        /// <summary>Kill every recorded child that is provably still the same process. One report
        /// line per child, saying what was done and why.</summary>
        public List<string> KillOwned() =>
            KillOwned(Children, FfmpegChildRegistry.IdentifyProcess, FfmpegChildRegistry.KillTree);

        /// <summary>Testable core: the identity probe and the kill are injected.</summary>
        internal static List<string> KillOwned(IReadOnlyList<Child> children, Func<int, DateTime, ProcessAnswer> identify, Action<int> kill)
        {
            var report = new List<string>();
            foreach (var c in children)
            {
                if (c.StartUtc == null)
                {
                    report.Add($"pid {c.Pid}: start time never read - not provably ours, left alone");
                    continue;
                }
                var answer = identify(c.Pid, c.StartUtc.Value);
                if (answer.IsNotMine)
                {
                    report.Add($"pid {c.Pid}: already exited (or the pid was reused) - left alone");
                    continue;
                }
                if (answer.IsUnknown)
                {
                    report.Add($"pid {c.Pid}: identity cannot be proved ({answer.Why}) - not provably ours, left alone");
                    continue;
                }
                kill(c.Pid);
                report.Add($"pid {c.Pid}: killed");
            }
            return report;
        }

        public void Dispose() => Ffmpeg.ChildStartedObserver.Value = _previous;
    }
}
