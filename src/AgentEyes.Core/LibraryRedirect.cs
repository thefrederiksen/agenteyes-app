using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentEyes
{
    /// <summary>
    /// Issue #161: the SUPPORTED way to point a real AgentEyes process (agenteyes serve / package /
    /// record / every other command, the Windows tray app, the Linux app) at a scratch library.
    ///
    /// Two environment variables, read ONCE at the top of each entry point, before anything resolves
    /// a path:
    ///
    ///   AGENTEYES_REDIRECT_DATA   stands in for the per-user local app-data folder; the state root
    ///                             (config, logs, credential, ffmpeg registry, models) becomes
    ///                             &lt;it&gt;/AgentEyes
    ///   AGENTEYES_REDIRECT_VIDEOS stands in for the Videos (macOS: Movies) folder; the recordings
    ///                             root becomes &lt;it&gt;/AgentEyes
    ///
    /// The value is the PARENT, exactly as <see cref="AppDataPaths.RedirectForThisProcess"/> takes it,
    /// so a redirected process and a redirected test host lay their folders out identically.
    ///
    /// WHY AN ENVIRONMENT VARIABLE AND NOT A CLI OPTION. Every AgentEyes entry point needs it (the CLI,
    /// the tray app, the Linux app), and a process this one starts (selftest re-runs the exe) inherits
    /// the environment but not the command line. A flag would have to be declared on every command in
    /// <see cref="CliCommandTable"/> and threaded into every child; a missed one would silently run
    /// against the real library. The environment cannot be forgotten by a child.
    ///
    /// The rules, every one of which REFUSES the start rather than guessing:
    ///  - both variables or neither: one alone is a partial redirect, and mixing a scratch state root
    ///    with the real recordings is how a test deletes a real recording;
    ///  - both values absolute;
    ///  - no other variable named AGENTEYES_REDIRECT_* (a typo of one of the two would otherwise leave
    ///    the process on the real library while the person believes it is redirected);
    ///  - neither redirected root may be, contain or sit inside the machine's REAL state or recordings
    ///    root, and the two redirected roots may not overlap each other.
    ///
    /// A redirected process is LOUD: the entry point prints the redirect on stderr, it is logged at
    /// WARN into the redirected log, and /status carries both roots and the redirect's source.
    /// A refusal is printed on stderr (and shown in a dialog by the tray app) and is deliberately NOT
    /// written to the log - the only log it could reach is the real one it is refusing to touch.
    ///
    /// Not to be confused with AGENTEYES_ROOT, which is the INSTALLER's variable for its own install
    /// layout (tools/AgentEyes.Setup.Engine/InstallLayout.cs) and has nothing to do with this.
    /// </summary>
    internal static class LibraryRedirect
    {
        /// <summary>The stand-in for the local app-data folder; the state root is &lt;it&gt;/AgentEyes.</summary>
        public const string DataVariable = "AGENTEYES_REDIRECT_DATA";

        /// <summary>The stand-in for the Videos folder; the recordings root is &lt;it&gt;/AgentEyes.</summary>
        public const string VideosVariable = "AGENTEYES_REDIRECT_VIDEOS";

        /// <summary>Any variable with this prefix that is not one of the two above is refused.</summary>
        public const string VariablePrefix = "AGENTEYES_REDIRECT_";

        /// <summary>The CLI's exit code for a refused redirect. 0-4 belong to <see cref="CliDispatcher"/>.</summary>
        public const int RefusedExitCode = 5;

        /// <summary>What /status reports as the source of an environment redirect.</summary>
        public const string EnvironmentSource = "environment (" + DataVariable + ", " + VideosVariable + ")";

        /// <summary>What /status reports when the roots were moved in-process by a test host.</summary>
        public const string TestHostSource = "in-process (test host)";

        private static volatile bool _appliedFromEnvironment;
        private static IReadOnlyList<RealLibrary.Root> _realRoots = Array.Empty<RealLibrary.Root>();

        /// <summary>True once <see cref="ApplyFromEnvironment"/> redirected this process.</summary>
        public static bool AppliedFromEnvironment => _appliedFromEnvironment;

        /// <summary>The real roots <see cref="ApplyFromEnvironment"/> derived and checked the redirect
        /// against (empty until it redirected this process).</summary>
        public static IReadOnlyList<RealLibrary.Root> RealRoots => _realRoots;

        /// <summary>
        /// Where this process's roots came from, for /status: <see cref="EnvironmentSource"/>,
        /// <see cref="TestHostSource"/>, or null when the process is on the machine's real folders.
        /// </summary>
        public static string? Source =>
            _appliedFromEnvironment ? EnvironmentSource
            : AppDataPaths.IsRedirected ? TestHostSource
            : null;

        /// <summary>The outcome of reading the two variables. Exactly one of the three shapes.</summary>
        internal sealed class Decision
        {
            private Decision(bool redirect, string? data, string? videos, string? refusal)
            {
                Redirect = redirect;
                Data = data;
                Videos = videos;
                Refusal = refusal;
            }

            /// <summary>True: move both roots to <see cref="Data"/> and <see cref="Videos"/>.</summary>
            public bool Redirect { get; }

            /// <summary>The stand-in for the app-data folder (the variable's value, full path).</summary>
            public string? Data { get; }

            /// <summary>The stand-in for the Videos folder (the variable's value, full path).</summary>
            public string? Videos { get; }

            /// <summary>Non-null: the start is refused, and this says why and how to fix it.</summary>
            public string? Refusal { get; }

            public bool Refused => Refusal != null;

            public static Decision NotRequested() => new(false, null, null, null);
            public static Decision RedirectTo(string data, string videos) => new(true, data, videos, null);
            public static Decision Refuse(string why) => new(false, null, null, why);
        }

        /// <summary>The outcome of <see cref="ApplyFromEnvironment"/>, for the entry point to report.</summary>
        internal enum Outcome { NotRequested, Redirected, Refused }

        /// <summary>
        /// Decide what the two variables ask for. PURE: every input is a parameter, so each rule is
        /// unit-tested without touching this process's environment or its (one-shot) redirect.
        /// </summary>
        /// <param name="data">The value of <see cref="DataVariable"/>, or null when unset.</param>
        /// <param name="videos">The value of <see cref="VideosVariable"/>, or null when unset.</param>
        /// <param name="variableNames">The names of every variable in the environment.</param>
        /// <param name="realLocalAppData">The machine's real local app-data folder.</param>
        /// <param name="realVideos">The machine's real Videos folder ("" when the machine has none - issue #169).</param>
        /// <param name="namesAreCaseInsensitive">True on Windows, where variable names ignore case.</param>
        /// <param name="resolveLinks">Maps a folder to the folder it really is, every symbolic link along it
        /// resolved (<see cref="ResolveLinks"/> in a real process). Every overlap check compares RESOLVED
        /// folders: "&lt;scratch&gt;/videos/AgentEyes" that is a link to the real recordings root is the real
        /// recordings root. Null compares the paths as written (the pure tests).</param>
        internal static Decision Decide(string? data, string? videos, IEnumerable<string> variableNames,
            string realLocalAppData, string realVideos, bool namesAreCaseInsensitive,
            Func<string, string>? resolveLinks = null)
        {
            var real = new List<RealLibrary.Root>();
            if (Path.IsPathFullyQualified(realLocalAppData))
                real.Add(new RealLibrary.Root(RealLibrary.StateName, realLocalAppData, "given"));
            if (Path.IsPathFullyQualified(realVideos))
                real.Add(new RealLibrary.Root(RealLibrary.RecordingsName, realVideos, "given"));
            return Decide(data, videos, variableNames, real, namesAreCaseInsensitive, resolveLinks);
        }

        /// <summary>
        /// Decide what the two variables ask for, against the real roots <paramref name="realRoots"/> -
        /// in a real process <see cref="RealLibrary.DeriveForThisProcess"/>: the ACCOUNT's folders (passwd,
        /// effective uid), never only what this process's spoofable environment says (issue #161 pass 2).
        /// PURE. A root whose parent is missing or relative is the caller's to leave out.
        /// </summary>
        internal static Decision Decide(string? data, string? videos, IEnumerable<string> variableNames,
            IReadOnlyList<RealLibrary.Root> realRoots, bool namesAreCaseInsensitive,
            Func<string, string>? resolveLinks = null, Func<string, string?>? usableFolder = null)
        {
            var resolve = resolveLinks ?? (p => p);
            var nameComparison = namesAreCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var unknown = variableNames
                .Where(n => n.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(n => !string.Equals(n, DataVariable, nameComparison) && !string.Equals(n, VideosVariable, nameComparison))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
                return Decision.Refuse(
                    $"unknown library-redirect variable(s): {string.Join(", ", unknown)}. The only two are "
                    + $"{DataVariable} and {VideosVariable}. Fix: unset {string.Join(", ", unknown)} and set both of those two.");

            bool hasData = !string.IsNullOrEmpty(data);
            bool hasVideos = !string.IsNullOrEmpty(videos);
            if (!hasData && !hasVideos) return Decision.NotRequested();

            if (!hasData || !hasVideos)
            {
                string missing = hasData ? VideosVariable : DataVariable;
                string present = hasData ? DataVariable : VideosVariable;
                return Decision.Refuse(
                    $"partial library redirect: {present} is set but {missing} is not. Both roots move together or "
                    + $"not at all - a scratch {(hasData ? "state" : "recordings")} root with the REAL "
                    + $"{(hasData ? "recordings" : "state")} root would mix the two libraries. "
                    + $"Fix: set {missing} as well, or unset {present}.");
            }

            if (!Path.IsPathFullyQualified(data!))
                return Decision.Refuse($"{DataVariable} must be an absolute path (got '{data}'). Fix: give the full path.");
            if (!Path.IsPathFullyQualified(videos!))
                return Decision.Refuse($"{VideosVariable} must be an absolute path (got '{videos}'). Fix: give the full path.");

            string dataFull = Normalize(data!);
            string videosFull = Normalize(videos!);
            string stateRoot = Path.Combine(dataFull, AppDataPaths.ProductFolder);
            string recordingsRoot = Path.Combine(videosFull, AppDataPaths.ProductFolder);

            string stateResolved = resolve(stateRoot);
            string recordingsResolved = resolve(recordingsRoot);
            if (Overlaps(stateResolved, recordingsResolved))
                return Decision.Refuse(
                    $"the redirected state root {Described(stateRoot, stateResolved)} and recordings root "
                    + $"{Described(recordingsRoot, recordingsResolved)} overlap. They must be two "
                    + $"separate folders, as they are on a real machine. Fix: point {DataVariable} and {VideosVariable} at "
                    + "two different directories, e.g. <scratch>/data and <scratch>/videos.");

            // The real roots this redirect must stay out of. Only their PARENTS (Videos, local app data)
            // are resolved: resolving the AgentEyes folder itself would stat the real library on every
            // redirected start, which is the thing a redirect exists to stop doing.
            // A real folder that is missing or relative
            // (no Videos folder - issue #169) names no fixed place to protect, and comparing against it
            // would resolve it against the working directory, so it is skipped rather than guessed at.
            var real = realRoots
                .Where(r => Path.IsPathFullyQualified(r.Parent))
                .Select(r => (Name: r.Name, Root: Path.Combine(resolve(Normalize(r.Parent)), AppDataPaths.ProductFolder), r.DerivedFrom))
                .ToList();

            foreach (var (variable, root, resolved, what) in new[]
                     {
                         (DataVariable, stateRoot, stateResolved, "state root"),
                         (VideosVariable, recordingsRoot, recordingsResolved, "recordings root"),
                     })
            {
                foreach (var (realName, realRoot, derivedFrom) in real)
                {
                    if (Overlaps(resolved, realRoot))
                        return Decision.Refuse(
                            $"{variable} puts the redirected {what} at {Described(root, resolved)}, which overlaps {realName} {realRoot} "
                            + $"(derived from: {derivedFrom}). "
                            + "A redirect exists to keep a process OUT of the real library. "
                            + $"Fix: point {variable} at a scratch directory outside {realRoot}.");
                }
            }

            // LAST, and only once nothing above refused: each value must be an EXISTING folder this process
            // can really WRITE to (Lead's ruling N1, issue #161 pass 2). Last, because the check writes a
            // probe file - run before the overlap rules, it would write one beside or inside the real
            // library. Never created on the person's behalf: a mistyped path would silently become a new,
            // empty library and the recordings would land where nobody looks.
            if (usableFolder != null)
            {
                foreach (var (variable, folder) in new[] { (DataVariable, dataFull), (VideosVariable, videosFull) })
                {
                    if (usableFolder(folder) is { } why)
                        return Decision.Refuse($"{variable}={folder} {why}");
                }
            }

            return Decision.RedirectTo(dataFull, videosFull);
        }

        /// <summary>
        /// Null when <paramref name="folder"/> exists and a file can really be created, written and removed in
        /// it; otherwise why not, with the fix. A REAL write, not access(): access() and permission bits can
        /// say writable where a write then fails (read-only mounts, ACLs, quotas). The probe file is removed.
        /// Used by <see cref="ApplyFromEnvironment"/> before anything else touches the folder.
        /// </summary>
        internal static string? UsableFolderProblem(string folder)
        {
            if (File.Exists(folder))
                return $"is a file, not a folder. Fix: point it at a directory.";
            if (!Directory.Exists(folder))
                return $"does not exist. A redirect never creates its own roots, so a mistyped path cannot quietly become "
                       + $"a new, empty library. Fix: create it (mkdir -p '{folder}') or correct the path.";
            string probe = Path.Combine(folder, $".agenteyes-redirect-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            try
            {
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0x2A);
                    fs.Flush(flushToDisk: true);
                }
                File.Delete(probe);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The probe IS the check; its failure is the answer, reported - not swallowed.
                return $"is not writable: a test write of {probe} failed ({ex.GetType().Name}: {ex.Message}). "
                       + "Fix: point it at a folder this account can write to.";
            }
        }

        /// <summary>
        /// Read the two variables from this process's environment and, when they ask for it, move both
        /// roots for the rest of the process. Call it FIRST in an entry point - before anything logs or
        /// resolves a path. On <see cref="Outcome.Refused"/> nothing was moved and nothing was logged;
        /// the caller must report <paramref name="message"/> and exit.
        /// </summary>
        /// <param name="message">The refusal on Refused; the one-line redirect banner on Redirected.</param>
        public static Outcome ApplyFromEnvironment(out string message)
        {
            var names = new List<string>();
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                names.Add((string)e.Key);

            // No AGENTEYES_REDIRECT_* variable at all - the normal run: nothing to decide, and the
            // machine's real folders are not even looked up, so a normal start does exactly what it
            // did before issue #161 (and a future change to how a missing Videos folder is handled -
            // issue #169 - cannot make every start fail here).
            if (!names.Any(n => n.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                message = "";
                return Outcome.NotRequested;
            }

            // The REAL roots come from the USER - the passwd entry of the effective uid - and not only from
            // this process's HOME / XDG_* (issue #161 pass 2): the environment is what a redirect manipulates,
            // so it cannot also be the authority on what the redirect may touch. Pass 1 asked only the
            // environment, and HOME=<scratch> turned the owner's ~/.local/share into "not the real library".
            // MachineLocalAppData / MachineVideos (read inside) do NOT hand out a path
            // (AppDataPaths._handedOut stays false), so the redirect below is still allowed after this.
            IReadOnlyList<RealLibrary.Root> realRoots;
            try
            {
                realRoots = RealLibrary.DeriveForThisProcess();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException
                                           or DllNotFoundException or EntryPointNotFoundException)
            {
                // Not a fallback: without the real library there is nothing to check a redirect against,
                // so the redirect is REFUSED, with the reason.
                message = "library redirect refused: the person's real library could not be located, so the redirect "
                          + $"cannot be checked against it: {ex.Message} Fix: run as the account that owns the library.";
                return Outcome.Refused;
            }

            var decision = Decide(
                Environment.GetEnvironmentVariable(DataVariable),
                Environment.GetEnvironmentVariable(VideosVariable),
                names,
                realRoots,
                OperatingSystem.IsWindows(),
                ResolveLinks,
                UsableFolderProblem);

            if (decision.Refused)
            {
                message = "library redirect refused: " + decision.Refusal;
                return Outcome.Refused;
            }
            if (!decision.Redirect)
            {
                // Deliberately NOT logged: Log creates the AgentEyes folder, and a log line here - before
                // the entry point's StorageMigration.Run - would make the one-time qa-record -> AgentEyes
                // move skip itself for good. Each entry point logs its roots after the migration.
                message = "";
                return Outcome.NotRequested;
            }

            AppDataPaths.RedirectForThisProcess(decision.Data!, decision.Videos!);
            // What the banner prints is what was COMPARED: each root with its links resolved (parent only,
            // exactly as Decide resolved it - the real AgentEyes folder itself is never touched).
            _realRoots = realRoots
                .Select(r => r with { ResolvedPath = Path.Combine(ResolveLinks(r.Parent), AppDataPaths.ProductFolder) })
                .ToList();
            _appliedFromEnvironment = true;
            message = Banner();
            Log.Warn($"[LibraryRedirect] ApplyFromEnvironment: {message}");
            return Outcome.Redirected;
        }

        /// <summary>
        /// The one-line statement of where this redirected process keeps its state and recordings - and,
        /// so a reader can CHECK the comparison instead of trusting it (issue #161 pass 2), the real roots
        /// the guard derived and kept this process out of, each with where it was derived from.
        /// </summary>
        public static string Banner() =>
            $"LIBRARY REDIRECTED by {DataVariable}/{VideosVariable}: state -> {AppDataPaths.Root} ; "
            + $"recordings -> {AppDataPaths.RecordingsRoot} ; log -> {Log.Dir} (pid {Environment.ProcessId}) ; "
            + RealRootsStatement();

        /// <summary>
        /// "real library kept out: ..." - every real root the guard compared against, with its derivation.
        /// Empty list (a test host, which never ran the guard) says so rather than claiming anything.
        /// </summary>
        public static string RealRootsStatement() =>
            _realRoots.Count == 0
                ? "real library: NOT CHECKED (no derivation ran in this process)"
                : "real library kept out (each root with where it was derived from): "
                  + string.Join(" ; ", _realRoots.Select(r => $"{r.Name} {Shown(r)} [{r.DerivedFrom}]"));

        /// <summary>A real root as printed: its path, and what it resolved to when a link moved it.</summary>
        internal static string Shown(RealLibrary.Root r) => Described(r.Path, r.ResolvedPath ?? r.Path);

        /// <summary>
        /// The refusal for a service that would listen on <paramref name="port"/>, or null when it may.
        /// A REDIRECTED service must never take the default port: that port is the address of the
        /// person's real service, and every client of it (the tray app, the Linux app, the CLI, scripts)
        /// would then be driving the scratch library while believing it was the real one - or, the other
        /// way round, a redirected client would drive the real one. Pure: the redirect state is a parameter.
        /// </summary>
        internal static string? ServicePortRefusal(int port, bool redirected)
        {
            if (!redirected || port != LocalAppConfig.DefaultPort) return null;
            return $"a redirected AgentEyes ({DataVariable}/{VideosVariable}) must not listen on the default port "
                   + $"{LocalAppConfig.DefaultPort} - that is the address of the person's real AgentEyes. "
                   + "Fix: pass --port N (agenteyes serve), or put {\"Port\": N} in the scratch config.json, "
                   + "which the redirected CLI client reads too.";
        }

        /// <summary>The refusal for this process acting through the service, or null when it may.</summary>
        public static string? ServicePortRefusal(int port) => ServicePortRefusal(port, _appliedFromEnvironment);

        /// <summary>
        /// The refusal for a CLI client acting through a service whose /status reported
        /// <paramref name="serviceData"/>, <paramref name="serviceRecordings"/> and
        /// <paramref name="serviceRedirected"/>, or null when it may. Pure.
        ///
        /// The check applies only when a redirect is involved on EITHER side. Two unredirected processes
        /// are both on "the real library" even when their roots differ as strings - on Linux the systemd
        /// user service can see a different XDG_DATA_HOME from the person's shell - and refusing them
        /// would break a CLI that worked before issue #161. A redirected client, or a service that
        /// reports it is redirected, must be on exactly the same two roots.
        ///
        /// A service that predates issue #161 reports neither root (null). Such a service cannot have
        /// been redirected - the redirect did not exist - so it is on its machine's real library: a
        /// client that is NOT redirected may use it, and a redirected client may not, because nothing
        /// proves that service is on the scratch library.
        /// </summary>
        internal static string? ServiceLibraryRefusal(string? serviceData, string? serviceRecordings, bool serviceRedirected,
            string myData, string myRecordings, bool iAmRedirected, string serviceUrl)
        {
            if (!iAmRedirected && !serviceRedirected) return null;

            if (string.IsNullOrEmpty(serviceData) || string.IsNullOrEmpty(serviceRecordings))
            {
                if (!iAmRedirected) return null;
                return $"the AgentEyes service at {serviceUrl} does not report which library it is acting on (it predates "
                       + $"issue #161), so it is on its machine's REAL library - and this command is redirected to "
                       + $"{myRecordings}. Fix: start a redirected service with the same {DataVariable}/{VideosVariable} "
                       + "and point this command at its port (Port in the scratch config.json).";
            }

            if (SamePath(serviceData, myData) && SamePath(serviceRecordings, myRecordings)) return null;
            return $"the AgentEyes service at {serviceUrl} is acting on a DIFFERENT library: state {serviceData}, recordings "
                   + $"{serviceRecordings}; this command's library is state {myData}, recordings {myRecordings}. Nothing was "
                   + $"sent to it. Fix: run this command with the same {DataVariable}/{VideosVariable} as the service "
                   + "(or neither), and the port of THAT service in its config.json.";
        }

        /// <summary>
        /// <paramref name="path"/> with every symbolic link (and Windows junction) along it resolved, as
        /// far as the path exists; the part that does not exist yet is appended as written. Used so a
        /// redirect cannot reach the real library through a link. A link loop deeper than 40 levels throws
        /// (a broken file system is a reason to refuse the start, never to guess).
        /// </summary>
        internal static string ResolveLinks(string path) => ResolveLinks(path, 0);

        private static string ResolveLinks(string path, int depth)
        {
            if (depth > 40)
                throw new IOException($"too many levels of symbolic links resolving {path}");
            string full = Normalize(path);
            string root = Path.GetPathRoot(full)!;
            string current = root;
            string[] parts = full.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string next = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget != null)
                {
                    // Relative targets are relative to the link's own folder.
                    string target = Path.IsPathFullyQualified(info.LinkTarget)
                        ? info.LinkTarget
                        : Path.GetFullPath(Path.Combine(current, info.LinkTarget));
                    string rest = Path.Combine(parts.Skip(i + 1).Prepend(target).ToArray());
                    return ResolveLinks(rest, depth + 1);
                }
                if (!info.Exists)
                    return Path.Combine(parts.Skip(i).Prepend(current).ToArray());
                current = next;
            }
            return current;
        }

        private static string Described(string path, string resolved) =>
            string.Equals(path, resolved, StringComparison.Ordinal) ? path : $"{path} (which resolves to {resolved})";

        private static bool SamePath(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        internal static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>True when the two folders are the same or one is inside the other. Case is ignored
        /// on every platform: the default macOS and Windows file systems ignore it, and refusing a
        /// case-only difference on Linux is the safe direction.</summary>
        internal static bool Overlaps(string a, string b)
        {
            string x = Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar;
            string y = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
        }
    }
}
