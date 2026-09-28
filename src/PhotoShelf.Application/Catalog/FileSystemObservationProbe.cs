using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoShelf.Application.Catalog;

public sealed record FileSystemProbeResult(string Path, FileAvailability Availability, DateTime CheckedAtUtc,
    long? Length = null, DateTime? LastWriteTimeUtc = null, DateTime? CreationTimeUtc = null,
    string? FileIdentity = null, string? ErrorCode = null, bool IsDirectory = false);

/// <summary>Synchronous, read-only I/O. Call on a worker and await its actual completion before moving files.</summary>
public interface IFileSystemObservationProbe
{
    FileSystemProbeResult ProbeFile(string path);
    FileSystemProbeResult ProbeRoot(string path);
}

/// <summary>Failure observations intentionally contain no replacement size/date/identity.</summary>
public sealed class FileSystemObservationProbe : IFileSystemObservationProbe
{
    public FileSystemProbeResult ProbeFile(string path) => Probe(path, requireDirectory: false);
    public FileSystemProbeResult ProbeRoot(string path) => Probe(path, requireDirectory: true);

    private static FileSystemProbeResult Probe(string path, bool requireDirectory)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            // Never enumerate or read through a known junction/symlink, including ancestors.
            var chain = new Stack<string>();
            for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
                chain.Push(current);
            FileAttributes attributes = default;
            while (chain.TryPop(out var component))
            {
                attributes = File.GetAttributes(component);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return Failure(path, FileAvailability.NeedsVerification, "ReparsePoint");
            }
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (requireDirectory && !directory) return Failure(path, FileAvailability.NeedsVerification, "NotDirectory");
            if (OperatingSystem.IsWindows()) return ProbeWindows(path, directory);
            // Unix identity is deliberately absent: portable file IDs are not assumed stable.
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            info.Refresh();
            return new(path, FileAvailability.Available, DateTime.UtcNow,
                directory ? null : ((FileInfo)info).Length, info.LastWriteTimeUtc, info.CreationTimeUtc, IsDirectory: directory);
        }
        catch (UnauthorizedAccessException) { return Failure(path, FileAvailability.AccessDenied, "AccessDenied"); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return FromError(path, exception is Win32Exception native ? native.NativeErrorCode : exception.HResult & 0xffff, exception.GetType().Name); }
    }

    private static FileSystemProbeResult ProbeWindows(string path, bool directory)
    {
        // FILE_READ_ATTRIBUTES only; sharing never blocks an external writer/mover.
        // OPEN_REPARSE_POINT prevents following the final component after the preflight.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return FromError(path, Marshal.GetLastPInvokeError(), "OpenAttributes");
        if (!GetFileInformationByHandle(handle, out var info)) return FromError(path, Marshal.GetLastPInvokeError(), "HandleInformation");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return Failure(path, FileAvailability.NeedsVerification, "ReparsePoint");
        // Detect an ancestor swapped to a junction between preflight and open. No image bytes have been read.
        var finalPath = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (count == 0 || count >= finalPath.Capacity) return Failure(path, FileAvailability.NeedsVerification, "UnverifiedPhysicalPath");
        if (!string.Equals(NormalizeWindowsPath(path), NormalizeWindowsPath(finalPath.ToString()), StringComparison.OrdinalIgnoreCase))
            return Failure(path, FileAvailability.NeedsVerification, "PhysicalPathChanged");
        var created = DateTime.FromFileTimeUtc(((long)info.CreationHigh << 32) | info.CreationLow);
        var modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
        // FILE_ID_INFO is the 128-bit identifier (including ReFS); old 64-bit index fields
        // are not assumed stable on every filesystem. Unsupported volumes get no identity.
        var identity = GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInformation>())
            && (id.Low != 0 || id.High != 0)
            ? $"win:{id.VolumeSerial:X16}:{id.High:X16}{id.Low:X16}:{created.Ticks:X16}" : null;
        return new(path, FileAvailability.Available, DateTime.UtcNow,
            directory ? null : checked(((long)info.SizeHigh << 32) | info.SizeLow), modified, created, identity, IsDirectory: directory);
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        return path.TrimEnd('\\', '/');
    }

    private static FileSystemProbeResult FromError(string path, int code, string operation)
    {
        if (code is 5 or 13) return Failure(path, FileAvailability.AccessDenied, operation + ":" + code);
        if (code is 21 or 53 or 64 or 67 or 1167 or 1231)
            return Failure(path, FileAvailability.RootOffline, operation + ":" + code);
        if (code is 2 or 3)
        {
            // File.Exists/Directory.Exists hide access and I/O errors; do not use them to infer deletion.
            try
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return Failure(path, FileAvailability.NeedsVerification, "InvalidRoot");
                _ = File.GetAttributes(root);
                return Failure(path, FileAvailability.Missing, operation + ":" + code);
            }
            catch (UnauthorizedAccessException) { return Failure(path, FileAvailability.AccessDenied, "RootAccessDenied"); }
            catch (IOException exception)
            {
                var rootCode = exception.HResult & 0xffff;
                return Failure(path, rootCode is 2 or 3 or 21 or 53 or 64 or 67 or 1167 or 1231
                    ? FileAvailability.RootOffline : FileAvailability.NeedsVerification, "RootUnavailable:" + rootCode);
            }
        }
        return Failure(path, FileAvailability.NeedsVerification, operation + ":" + code);
    }

    private static FileSystemProbeResult Failure(string path, FileAvailability availability, string error)
        => new(path, availability, DateTime.UtcNow, ErrorCode: error);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerial, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
