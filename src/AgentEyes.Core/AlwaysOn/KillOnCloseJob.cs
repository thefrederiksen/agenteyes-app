using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentEyes.AlwaysOn
{
    /// <summary>
    /// A Windows job object that kills every process in it when AgentEyes exits - however it exits
    /// (issue #66). Always-on starts an ffmpeg that is meant to run all day; without this, an
    /// AgentEyes that crashed or was ended from Task Manager would leave that ffmpeg recording the
    /// screen with nobody deciding what to keep and nobody deleting the pieces, until the disk filled.
    ///
    /// The job's handle is held for the life of the process and never closed by hand: the kernel closes
    /// it when the process ends, and KILL_ON_JOB_CLOSE ends every process assigned to it.
    /// </summary>
    internal static class KillOnCloseJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private static readonly object Gate = new();
        private static IntPtr _job;

        /// <summary>Put <paramref name="process"/> in the job. Throws when Windows refuses - the caller
        /// must not keep a capture running that could outlive AgentEyes.</summary>
        public static void Assign(Process process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            lock (Gate)
            {
                if (_job == IntPtr.Zero) _job = Create();
                if (!AssignProcessToJobObject(_job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"could not tie ffmpeg (pid {process.Id}) to AgentEyes' lifetime");
            }
            Log.Info($"[KillOnCloseJob] Assign: pid {process.Id} ends when AgentEyes ends");
        }

        private static IntPtr Create()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "could not create the always-on job object");

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "could not set kill-on-close on the always-on job object");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
