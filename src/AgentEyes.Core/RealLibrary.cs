using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AgentEyes
{
    /// <summary>
    /// Issue #161 pass 2: the person's REAL library - the folders a library redirect must stay out of -
    /// derived from the USER, not from this process's environment.
    ///
    /// WHY. A redirect is set through the environment, so the environment cannot also be the authority on
    /// what the redirect may touch. Pass 1 asked .NET for the real folders, and on Linux .NET answers from
    /// $HOME / $XDG_DATA_HOME: with HOME pointed at a scratch folder, the owner's ~/.local/share became "not
    /// the real library", a redirect onto it was ACCEPTED, and the process announced that it was isolated
    /// while it opened the owner's credential, ffmpeg registry and live log (QA, Landlock-measured).
    ///
    /// WHAT. The protected roots are the UNION of:
    ///  - the ACCOUNT's folders, from the passwd entry of the EFFECTIVE uid (getpwuid_r - never $HOME):
    ///      Linux  state      &lt;home&gt;/.local/share            (the XDG default; $XDG_DATA_HOME is NOT read)
    ///             recordings XDG_VIDEOS_DIR from &lt;home&gt;/.config/user-dirs.dirs ($XDG_CONFIG_HOME is NOT read),
    ///                        else &lt;home&gt;/Videos (the XDG default)
    ///      macOS  state      &lt;home&gt;/Library/Application Support   recordings &lt;home&gt;/Movies
    ///  - the folders THIS PROCESS would use unredirected (<see cref="AppDataPaths.MachineLocalAppData"/> /
    ///    <see cref="AppDataPaths.MachineVideos"/>) - pass 1's answer. Kept because the person may legitimately
    ///    run with a non-default XDG_DATA_HOME or user-dirs location, and their library is then there.
    ///
    /// A union can only ADD refusals: spoofing the environment can make the second half name a scratch
    /// folder, but it cannot remove the first. Windows is unchanged: there the real folders come from the
    /// shell's known-folder lookup, not from a variable this process's environment sets (not re-measured
    /// in pass 2 - stated in the handoff).
    ///
    /// Nothing here opens, lists or creates anything under a real root: the only file read is the
    /// account's user-dirs.dirs, which is configuration, not library.
    ///
    /// NOTHING HERE LOGS, deliberately, against the every-public-method-logs rule: it runs before the
    /// redirect, when the only log there is is the person's REAL one (and Log would create the real state
    /// folder). What it derived is reported instead by <see cref="LibraryRedirect.Banner"/> into the
    /// redirected log, and by a refusal on stderr.
    /// </summary>
    internal static class RealLibrary
    {
        /// <summary>The name a refusal and the banner give the real state root.</summary>
        public const string StateName = "the REAL state root";

        /// <summary>The name a refusal and the banner give the real recordings root.</summary>
        public const string RecordingsName = "the REAL recordings root";

        /// <summary>One folder a redirect must stay out of, and how it was found.</summary>
        /// <param name="Name"><see cref="StateName"/> or <see cref="RecordingsName"/>.</param>
        /// <param name="Parent">The PARENT (local app data / Videos); the root is &lt;Parent&gt;/AgentEyes.</param>
        /// <param name="DerivedFrom">Where the parent came from, for the refusal and the banner.</param>
        internal sealed record Root(string Name, string Parent, string DerivedFrom)
        {
            /// <summary>The AgentEyes folder under <see cref="Parent"/>.</summary>
            public string Path => System.IO.Path.Combine(Parent, AppDataPaths.ProductFolder);

            /// <summary>The AgentEyes folder under <see cref="Parent"/> with every link resolved - what the guard
            /// actually compared. Null until <see cref="LibraryRedirect.ApplyFromEnvironment"/> sets it.</summary>
            public string? ResolvedPath { get; init; }
        }

        /// <summary>How a root found through this process's own environment (pass 1's answer) is described.</summary>
        internal const string ProcessEnvironmentSource = "this process's environment (.NET special folders)";

        /// <summary>
        /// The real roots for THIS process: the account's (passwd, effective uid) on Linux and macOS, plus
        /// the ones this process's environment names. Throws when the account cannot be looked up or its
        /// user-dirs.dirs cannot be read - the caller refuses the redirect; it never guesses a home.
        /// </summary>
        public static IReadOnlyList<Root> DeriveForThisProcess()
        {
            string? accountHome = null;
            uint uid = 0;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                uid = Native.GetEffectiveUid();
                accountHome = AccountHome(uid);
            }

            var roots = new List<Root>(Derive(accountHome, uid, OperatingSystem.IsMacOS(), ReadIfPresent,
                AppDataPaths.MachineLocalAppData, AppDataPaths.MachineVideos));

            // Under sudo the effective uid is root's, and so is HOME - neither names the person's library.
            // SUDO_UID is environment, which this class otherwise refuses to trust; it is used here ONLY to
            // ADD the invoking account's roots (it can add refusals, never remove one), so spoofing it gains
            // nothing.
            if (uid == 0 && uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out uint sudoUid) && sudoUid != 0)
                roots.AddRange(Derive(AccountHome(sudoUid), sudoUid, OperatingSystem.IsMacOS(), ReadIfPresent, "", "")
                    .Select(r => r with { DerivedFrom = r.DerivedFrom + " (SUDO_UID: the account that ran sudo)" }));
            return roots;
        }

        /// <summary>
        /// The derivation, PURE: every input is a parameter so each rule is unit-tested, including a
        /// spoofed HOME (which this method never sees - it is given the account's home).
        /// </summary>
        /// <param name="accountHome">The passwd home of the effective uid; null on Windows.</param>
        /// <param name="uid">The effective uid, for the description.</param>
        /// <param name="isMac">True for the macOS layout, false for the Linux (XDG) one.</param>
        /// <param name="readFile">Returns a file's text, or null when it does not exist.</param>
        /// <param name="processLocalAppData">What this process's environment names as local app data.</param>
        /// <param name="processVideos">What this process's environment names as Videos ("" when none - issue #169).</param>
        internal static IReadOnlyList<Root> Derive(string? accountHome, uint uid, bool isMac, Func<string, string?> readFile,
            string processLocalAppData, string processVideos)
        {
            var roots = new List<Root>();
            if (accountHome != null)
            {
                if (!Path.IsPathFullyQualified(accountHome))
                    throw new InvalidOperationException(
                        $"the passwd entry for uid {uid} has a home directory that is not an absolute path ('{accountHome}'), "
                        + "so the real library cannot be located and a redirect cannot be checked against it.");
                string account = $"passwd entry for uid {uid}, home {accountHome}";
                if (isMac)
                {
                    roots.Add(new Root(StateName, Path.Combine(accountHome, "Library", "Application Support"), account));
                    roots.Add(new Root(RecordingsName, Path.Combine(accountHome, "Movies"), account));
                }
                else
                {
                    roots.Add(new Root(StateName, Path.Combine(accountHome, ".local", "share"), account + ", XDG default"));
                    string userDirs = Path.Combine(accountHome, ".config", "user-dirs.dirs");
                    string? text = readFile(userDirs);
                    string? fromUserDirs = text == null ? null : VideosFromUserDirs(text, accountHome);
                    roots.Add(fromUserDirs != null
                        ? new Root(RecordingsName, fromUserDirs, $"{account}, XDG_VIDEOS_DIR in {userDirs}")
                        : new Root(RecordingsName, Path.Combine(accountHome, "Videos"),
                            $"{account}, XDG default ({(text == null ? "no" : "no usable XDG_VIDEOS_DIR in")} {userDirs})"));
                }
            }

            // Pass 1's answer, kept in the union. A folder that is missing or relative (no Videos folder -
            // issue #169) names no fixed place, and comparing against it would resolve it against the
            // working directory, so it is left out rather than guessed at.
            if (Path.IsPathFullyQualified(processLocalAppData))
                roots.Add(new Root(StateName, processLocalAppData, ProcessEnvironmentSource));
            if (Path.IsPathFullyQualified(processVideos))
                roots.Add(new Root(RecordingsName, processVideos, ProcessEnvironmentSource));

            // One entry per distinct folder; a folder found both ways says so. Compared ORDINALLY: two names
            // that differ only in case are two folders on Linux, and each needs its own link resolution.
            // (On a case-insensitive file system the worst case is the same folder listed twice.)
            return roots
                .Select(r => r with { Parent = LibraryRedirect.Normalize(r.Parent) })
                .GroupBy(r => (r.Name, r.Parent))
                .Select(g => new Root(g.Key.Name, g.Key.Parent, string.Join(" + ", g.Select(r => r.DerivedFrom).Distinct())))
                .ToList();
        }

        /// <summary>
        /// XDG_VIDEOS_DIR from the text of a user-dirs.dirs file, per the xdg-user-dirs format: the value is
        /// "$HOME/..." (relative to <paramref name="home"/> - the ACCOUNT's home, never $HOME) or an absolute
        /// path. The last assignment wins, as when a shell sources the file. Null when there is no usable one.
        /// </summary>
        internal static string? VideosFromUserDirs(string text, string home)
        {
            string? found = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.Substring(0, eq).Trim() != "XDG_VIDEOS_DIR") continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value.Substring(1, value.Length - 2);

                if (value == "$HOME") found = home;
                else if (value.StartsWith("$HOME/", StringComparison.Ordinal)) found = Path.Combine(home, value.Substring("$HOME/".Length));
                else if (Path.IsPathFullyQualified(value)) found = value;
                // Anything else is not a valid entry; an earlier valid one (if any) stands.
            }
            return found;
        }

        /// <summary>
        /// The home directory in the passwd entry of <paramref name="uid"/> (getpwuid_r, so NSS sources such
        /// as LDAP count). Throws when there is no entry - a redirect is then refused, never checked against
        /// a guessed home.
        /// </summary>
        internal static string AccountHome(uint uid)
        {
            const int ERANGE = 34;   // the same value on Linux and macOS
            int size = 4096;
            while (true)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                IntPtr pwd = Marshal.AllocHGlobal(Native.PasswdSize);
                try
                {
                    int rc = Native.GetPwUidR(uid, pwd, buf, (UIntPtr)size, out IntPtr result);
                    if (rc == ERANGE && size < (1 << 20)) { size *= 2; continue; }
                    if (rc != 0)
                        throw new InvalidOperationException($"getpwuid_r({uid}) failed with errno {rc}; the real library cannot be located.");
                    if (result == IntPtr.Zero)
                        throw new InvalidOperationException($"there is no passwd entry for uid {uid}; the real library cannot be located.");
                    IntPtr dir = Marshal.ReadIntPtr(pwd, OperatingSystem.IsMacOS() ? Native.MacDirOffset : Native.LinuxDirOffset);
                    string? home = Marshal.PtrToStringUTF8(dir);
                    if (string.IsNullOrEmpty(home))
                        throw new InvalidOperationException($"the passwd entry for uid {uid} has no home directory; the real library cannot be located.");
                    return home;
                }
                finally
                {
                    Marshal.FreeHGlobal(pwd);
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        /// <summary>The effective uid (Linux and macOS only).</summary>
        internal static uint EffectiveUid() => Native.GetEffectiveUid();

        private static string? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        private static class Native
        {
            // struct passwd, 64-bit. Every field before pw_dir is a pointer, a 32-bit id pair, or (macOS) a
            // time_t and a pointer, so the offsets are fixed:
            //   Linux: pw_name 0, pw_passwd 8, pw_uid 16, pw_gid 20, pw_gecos 24, pw_dir 32, pw_shell 40
            //   macOS: pw_name 0, pw_passwd 8, pw_uid 16, pw_gid 20, pw_change 24, pw_class 32, pw_gecos 40,
            //          pw_dir 48, pw_shell 56, pw_expire 64
            public const int LinuxDirOffset = 32;
            public const int MacDirOffset = 48;
            public const int PasswdSize = 128;   // larger than either struct

            // Bound by the real library names, not "libc": on glibc the bare name probes libc.so, which is a
            // linker script shipped only by the C development package - absent on a runtime-only machine.
            public static uint GetEffectiveUid() => OperatingSystem.IsMacOS() ? MacGeteuid() : LinuxGeteuid();

            public static int GetPwUidR(uint uid, IntPtr pwd, IntPtr buf, UIntPtr buflen, out IntPtr result) =>
                OperatingSystem.IsMacOS()
                    ? MacGetpwuidR(uid, pwd, buf, buflen, out result)
                    : LinuxGetpwuidR(uid, pwd, buf, buflen, out result);

            [DllImport("libc.so.6", EntryPoint = "geteuid")]
            private static extern uint LinuxGeteuid();

            [DllImport("libc.so.6", EntryPoint = "getpwuid_r")]
            private static extern int LinuxGetpwuidR(uint uid, IntPtr pwd, IntPtr buf, UIntPtr buflen, out IntPtr result);

            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
            private static extern uint MacGeteuid();

            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getpwuid_r")]
            private static extern int MacGetpwuidR(uint uid, IntPtr pwd, IntPtr buf, UIntPtr buflen, out IntPtr result);
        }
    }
}
