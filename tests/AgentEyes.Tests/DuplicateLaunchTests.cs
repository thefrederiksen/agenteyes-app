using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgentEyes.Setup.Engine;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #61 - the "AgentEyes is already running" popup the owner kept getting.
    ///
    /// Three defects, three groups of tests:
    ///  1. the scripts that launch the app looked for a process name that cannot exist, so their
    ///     "is one already running" check found nothing every time and they started a duplicate;
    ///  2. the single-instance guard threw a MODAL dialog at whoever was at the machine, even when
    ///     the launch had asked to start hidden and nobody was waiting for a window;
    ///  3. the auto-update restart started the new exe with no arguments, so an app launched as
    ///     "--tray" came back from an update with a window on screen.
    /// </summary>
    public sealed class DuplicateLaunchTests
    {
        // ---- 2. the refusal decision ------------------------------------------------

        [Theory]
        [InlineData("--tray")]
        [InlineData("--minimized")]
        [InlineData("--TRAY")]
        [InlineData("--Minimized")]
        public void Decide_LaunchAskedToStartHidden_ExitsQuietly(string flag)
        {
            // Nobody is waiting for a window, so a modal box would only land on top of whatever
            // the person is actually doing - and block until it is clicked away.
            Assert.Equal(SecondInstanceResponse.ExitQuietly, SecondInstancePolicy.Decide(new[] { flag }));
        }

        [Fact]
        public void Decide_HiddenFlagAmongOthers_ExitsQuietly()
        {
            Assert.Equal(SecondInstanceResponse.ExitQuietly,
                SecondInstancePolicy.Decide(new[] { "--something", "--tray", "--else" }));
        }

        [Fact]
        public void Decide_InteractiveLaunch_TellsThePerson()
        {
            // A person double-clicked the shortcut and is waiting for a window. Say why none came.
            Assert.Equal(SecondInstanceResponse.TellThePerson, SecondInstancePolicy.Decide(Array.Empty<string>()));
        }

        [Fact]
        public void Decide_NullArguments_TellsThePerson()
        {
            Assert.Equal(SecondInstanceResponse.TellThePerson, SecondInstancePolicy.Decide(null));
        }

        [Fact]
        public void AsksForHiddenStart_IgnoresSurroundingWhitespace()
        {
            Assert.True(LaunchArguments.AsksForHiddenStart(new[] { " --tray " }));
        }

        [Fact]
        public void AsksForHiddenStart_UnrelatedArgument_IsNotAHiddenStart()
        {
            Assert.False(LaunchArguments.AsksForHiddenStart(new[] { "--traytable", "-tray" }));
        }

        // ---- 3. the restart carries the command line --------------------------------

        [Fact]
        public void ToCarryAcrossRestart_DropsTheExecutablePath_AndKeepsTheRest()
        {
            // Environment.GetCommandLineArgs()[0] is the exe itself, not an argument. Passing it on
            // would hand the app its own path as an argument.
            var carried = LaunchArguments.ToCarryAcrossRestart(
                new[] { @"C:\Users\x\AppData\Local\AgentEyes\app\AgentEyesApp.exe", "--tray" });

            Assert.Equal(new[] { "--tray" }, carried);
        }

        [Fact]
        public void ToCarryAcrossRestart_TrayAppStaysATrayApp()
        {
            // The defect this pins: an app started as "--tray" came back from an automatic update
            // with a window, because the restart dropped the argument.
            var carried = LaunchArguments.ToCarryAcrossRestart(new[] { @"D:\app\AgentEyesApp.exe", "--tray" });
            Assert.True(LaunchArguments.AsksForHiddenStart(carried));
        }

        [Fact]
        public void ToCarryAcrossRestart_NoArguments_CarriesNothing()
        {
            Assert.Empty(LaunchArguments.ToCarryAcrossRestart(new[] { @"D:\app\AgentEyesApp.exe" }));
        }

        [Fact]
        public void ToCarryAcrossRestart_EmptyOrNull_CarriesNothing()
        {
            Assert.Empty(LaunchArguments.ToCarryAcrossRestart(Array.Empty<string>()));
            Assert.Empty(LaunchArguments.ToCarryAcrossRestart(null));
        }

        [Fact]
        public void ToCarryAcrossRestart_SkipsBlankArguments()
        {
            var carried = LaunchArguments.ToCarryAcrossRestart(new[] { @"D:\app\AgentEyesApp.exe", "  ", "--tray", "" });
            Assert.Equal(new[] { "--tray" }, carried);
        }

        // ---- 1. no script may carry the broken process-name check any more -----------

        /// <summary>
        /// The defect that caused the popups: every script that launches the app opened with
        /// <c>Get-Process AgentEyes ... | Stop-Process -Force</c>. The process is named
        /// AgentEyesApp, Get-Process takes no wildcard there, and the suppressed error made a
        /// failed lookup look exactly like "nothing is running" - so the check passed by finding
        /// nothing and the script started a duplicate. This test fails if that line comes back, in
        /// any script, under any name.
        /// </summary>
        [Fact]
        public void NoScriptLooksForAProcessNamedAgentEyes()
        {
            var offenders = ScriptFiles()
                .Where(f => Regex.IsMatch(File.ReadAllText(f),
                    "Get-Process\\s+(-Name\\s+)?[\"']?AgentEyes[\"']?(\\s|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                "These scripts look for a process named 'AgentEyes', which can never match the real "
                + "process 'AgentEyesApp' - the check passes by finding nothing and a duplicate "
                + "instance is launched (issue #61): " + string.Join(", ", offenders));
        }

        /// <summary>
        /// These scripts must never force-kill an AgentEyes they did not start: the owner's tray
        /// app may be mid-recording, and killing it destroys that recording. Stopping is done
        /// through Stop-ScriptOwnedAgentEyes, which only touches the process the script started.
        /// </summary>
        [Fact]
        public void NoScriptForceKillsAgentEyesByName()
        {
            var offenders = ScriptFiles()
                .Where(f => Regex.IsMatch(File.ReadAllText(f),
                    "Get-Process[^\\r\\n]*\\|\\s*Stop-Process", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                "These scripts pipe Get-Process straight into Stop-Process, which can force-kill an "
                + "AgentEyes the script did not start - including one that is mid-recording "
                + "(issue #61). Use Stop-ScriptOwnedAgentEyes instead: " + string.Join(", ", offenders));
        }

        /// <summary>Every script that starts the app must go through the shared, running-aware helper.</summary>
        [Fact]
        public void EveryScriptThatLaunchesTheAppUsesTheRunningAwareHelper()
        {
            var offenders = ScriptFiles()
                .Where(f => Regex.IsMatch(File.ReadAllText(f),
                    "Start-Process\\s+\\$exe", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                "These scripts start the app directly instead of through Start-AgentEyesForScript, "
                + "so they can launch a second instance on top of a running one (issue #61): "
                + string.Join(", ", offenders));
        }

        /// <summary>
        /// The refusal must be raised BEFORE the script's try block, never inside it.
        ///
        /// PowerShell runs a finally block when a script exits from inside its try, and at least
        /// two of these scripts read "no backup file exists" as "this presets.json is mine, delete
        /// it". A refusal raised inside the try therefore ran a cleanup written for a run that had
        /// actually started, and deleted the person's presets - in exactly the situation this whole
        /// change is about, their app being open. Refusing before the try means nothing has been
        /// touched and nothing needs undoing.
        /// </summary>
        [Fact]
        public void TheRefusalIsRaisedBeforeAnyTryBlock()
        {
            var offenders = new List<string>();

            foreach (var file in ScriptFiles())
            {
                var text = File.ReadAllText(file);
                var refusal = text.IndexOf("Assert-NoAgentEyesRunning", StringComparison.OrdinalIgnoreCase);
                if (refusal < 0) continue;   // this script does not launch the app

                var firstTry = Regex.Match(text, @"^\s*try\s*\{", RegexOptions.Multiline);
                if (firstTry.Success && refusal > firstTry.Index)
                    offenders.Add(Path.GetFileName(file));
            }

            Assert.True(offenders.Count == 0,
                "These scripts raise the already-running refusal from INSIDE a try block. PowerShell "
                + "runs the finally when a script exits from inside its try, so the refusal triggers a "
                + "cleanup written for a run that never started - which in these scripts deletes the "
                + "person's presets.json (issue #61): " + string.Join(", ", offenders));
        }

        /// <summary>
        /// A script that starts the app must stop it from a finally block. Without one, a bare
        /// Invoke-RestMethod throwing under ErrorActionPreference 'Stop' - or any early exit -
        /// leaves the instance it started running. That orphan holds the single-instance lock and
        /// port 7882, so with the refusal in place every later run refuses until somebody finds it
        /// and quits it from the tray by hand.
        /// </summary>
        [Fact]
        public void EveryScriptThatStartsTheAppStopsItFromAFinallyBlock()
        {
            var offenders = new List<string>();

            foreach (var file in ScriptFiles())
            {
                var text = File.ReadAllText(file);
                if (!text.Contains("Start-AgentEyesForScript", StringComparison.OrdinalIgnoreCase)) continue;

                var finallyAt = text.IndexOf("finally", StringComparison.OrdinalIgnoreCase);
                var stopAt = text.IndexOf("Stop-ScriptOwnedAgentEyes", StringComparison.OrdinalIgnoreCase);

                if (finallyAt < 0 || stopAt < 0 || stopAt < finallyAt)
                    offenders.Add(Path.GetFileName(file));
            }

            Assert.True(offenders.Count == 0,
                "These scripts start the app but do not stop it from a finally block, so a failure "
                + "part way through orphans the instance they started - and that orphan then makes "
                + "every later run refuse (issue #61): " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Both projects set Platforms=x64, so a "-c Release" build lands in bin\x64\Release. A
        /// script pointing at bin\Release finds nothing on a fresh checkout and a months-stale
        /// binary on an older one - silently driving code nobody built. The path lives once, in
        /// Get-BuiltExePath.
        /// </summary>
        [Fact]
        public void NoScriptPointsAtTheNonExistentBuildOutputPath()
        {
            var offenders = ScriptFiles()
                .Where(f => File.ReadAllText(f).Contains(@"bin\Release\", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                @"These scripts point at bin\Release, which this solution never builds to - it builds "
                + @"to bin\x64\Release. On an older checkout bin\Release holds a months-stale binary, "
                + "so the script drives code nobody built (issue #61). Use Get-BuiltExePath: "
                + string.Join(", ", offenders));
        }

        /// <summary>The helper the three guards above point at must actually exist.</summary>
        [Fact]
        public void TheSharedScriptHelperExists()
        {
            var helper = Path.Combine(ScriptsDir(), "lib", "AgentEyesProcess.ps1");
            Assert.True(File.Exists(helper), "The shared helper is missing: " + helper);

            var text = File.ReadAllText(helper);
            foreach (var fn in new[] { "Assert-NoAgentEyesRunning", "Start-AgentEyesForScript",
                                       "Stop-ScriptOwnedAgentEyes", "Get-BuiltExePath" })
                Assert.Contains("function " + fn, text, StringComparison.Ordinal);

            // The process name is derived from the exe path, never typed as a literal - a literal is
            // exactly what drifted and caused this defect.
            Assert.Contains("GetFileNameWithoutExtension", text, StringComparison.Ordinal);
        }

        // ---- locating the scripts ---------------------------------------------------

        private static string[] ScriptFiles()
        {
            var dir = ScriptsDir();
            var files = Directory.GetFiles(dir, "*.ps1", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("AgentEyesProcess.ps1", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // An empty sweep would pass all three guards above by checking nothing at all.
            Assert.True(files.Length > 0, "No scripts found under " + dir + " - this guard is checking nothing.");
            return files;
        }

        private static string ScriptsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts")))
                dir = dir.Parent;

            Assert.True(dir != null, "Could not find the repository root (no 'scripts' directory above "
                + AppContext.BaseDirectory + ").");
            return Path.Combine(dir!.FullName, "scripts");
        }
    }
}
