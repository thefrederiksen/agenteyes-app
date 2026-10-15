using System;
using Xunit;

namespace AgentEyes.Tests.Linux
{
    /// <summary>
    /// A test of a LINUX CONVENTION, not of the engine's logic - one that asserts what the operating
    /// system itself reports, and so can only hold on Linux.
    ///
    /// Almost every test in this suite is pure logic (an argument list, a parser) and runs anywhere,
    /// which is the point: they are fast and need no X server. The few that read a real operating
    /// system convention - where the data folder is, for instance - are a different kind of test, and
    /// on a Mac they would fail for the right reason, which is noise rather than a defect. They are
    /// skipped there instead, with the reason stated in the run output.
    /// </summary>
    public sealed class LinuxOnlyFactAttribute : FactAttribute
    {
        public LinuxOnlyFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
            {
                Skip = "asserts a Linux operating-system convention; this is not Linux";
            }
        }
    }
}
