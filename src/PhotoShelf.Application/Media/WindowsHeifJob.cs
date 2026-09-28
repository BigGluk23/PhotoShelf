using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoShelf.Application.Media;

/// <summary>Resource/process containment, not a filesystem or privilege sandbox.</summary>
internal sealed class WindowsHeifJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private WindowsHeifJob(SafeFileHandle handle) => _handle = handle;

    public static WindowsHeifJob Attach(Process process)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot create HEIF worker job."); }
        var job = new WindowsHeifJob(handle);
        try
        {
            var limits = new ExtendedLimits
            {
                BasicLimitInformation = new BasicLimits
                {
                    // ACTIVE_PROCESS | PROCESS_MEMORY | DIE_ON_UNHANDLED_EXCEPTION | KILL_ON_JOB_CLOSE
                    LimitFlags = 0x8 | 0x100 | 0x400 | 0x2000,
                    ActiveProcessLimit = 1
                },
                ProcessMemoryLimit = (UIntPtr)(1024UL * 1024 * 1024)
            };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set HEIF worker limits.");
            if (!AssignProcessToJobObject(handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot assign HEIF worker to its job.");
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    public void Terminate()
    {
        if (!TerminateJobObject(_handle, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate HEIF worker job.");
    }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
