using System;

namespace AgentEyes.Video
{
    /// <summary>
    /// Issue #156 pass 4: the answer to "does this pid still name the process that was recorded?"
    /// (<see cref="FfmpegChildRegistry.IdentifyProcess"/>). THREE answers, because there are three
    /// states of knowledge, and the third is not allowed to masquerade as either of the others.
    /// </summary>
    internal enum ProcessIdentity
    {
        /// <summary>Not provable from this process either way. Callers must NOT TOUCH what the record
        /// protects, and must say so: Unknown delays a recovery (recoverable); guessing NotMine can
        /// finalize a recording that is still being written (not recoverable). It is value 0 on
        /// purpose: an answer nobody set - default(ProcessAnswer) - reads as the safe one.</summary>
        Unknown = 0,

        /// <summary>Proved: the pid is running and started at the recorded time - it IS the recorded process.</summary>
        Mine,

        /// <summary>Proved: the recorded process is not running under this pid (gone, exited, a zombie,
        /// the pid reused, or another user's process).</summary>
        NotMine,
    }

    /// <summary>One answer from <see cref="FfmpegChildRegistry.IdentifyProcess"/>, with the fact behind it.</summary>
    internal readonly record struct ProcessAnswer(ProcessIdentity Identity, string Why)
    {
        public static ProcessAnswer Mine(string why) => new(ProcessIdentity.Mine, why);
        public static ProcessAnswer NotMine(string why) => new(ProcessIdentity.NotMine, why);
        public static ProcessAnswer Unknown(string why) => new(ProcessIdentity.Unknown, why);

        public bool IsMine => Identity == ProcessIdentity.Mine;
        public bool IsNotMine => Identity == ProcessIdentity.NotMine;
        public bool IsUnknown => Identity == ProcessIdentity.Unknown;
    }

    /// <summary>What the macOS kernel says about a pid (sysctl KERN_PROC_PID).</summary>
    internal enum MacProcessStateKind
    {
        /// <summary>The state could not be read (the call failed, or the reply had an unexpected size).
        /// Value 0 on purpose: a state nobody read - default(MacProcessState) - must not read as a proof.</summary>
        Unreadable = 0,

        /// <summary>p_stat is SZOMB: the process has exited and only awaits its parent's wait().</summary>
        Zombie,

        /// <summary>Any other p_stat (idle, running, sleeping, stopped): the process exists and has not exited.</summary>
        Alive,

        /// <summary>The kernel returned no process for the pid.</summary>
        Gone,
    }

    /// <summary>One read of a pid's kernel state on macOS.</summary>
    internal readonly record struct MacProcessState(MacProcessStateKind Kind, int PStat, string Detail,
        uint? RealUid = null, uint? SavedUid = null, uint? EffectiveUid = null);

    /// <summary>Issue #156 pass 5: what one statx of /proc/&lt;pid&gt; says about a pid on Linux.</summary>
    internal enum LinuxProcOwnerKind
    {
        /// <summary>The directory could not be examined (refused, or an unexpected reply). Value 0 on
        /// purpose: a read nobody made - default(LinuxProcOwner) - must not read as a proof.</summary>
        Unreadable = 0,

        /// <summary>The directory exists and its owner uid was read.</summary>
        Owned,

        /// <summary>No /proc/&lt;pid&gt; (ENOENT): the process does not exist.</summary>
        Gone,
    }

    /// <summary>One read of the owner of /proc/&lt;pid&gt; on Linux. The kernel sets that owner to the
    /// process's effective uid (root's when the process is not dumpable).</summary>
    internal readonly record struct LinuxProcOwner(LinuxProcOwnerKind Kind, string Detail, uint? Uid = null);
}
