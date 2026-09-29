using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) options.Add(args[i], args[i + 1]);
var mode = options["--probe-mode"]; var report = options["--probe-report"];
var versionProbe = args.Contains("--version", StringComparer.Ordinal);
var maximum = versionProbe ? 256 : int.Parse(options["--max-dimension"], System.Globalization.CultureInfo.InvariantCulture);
var input = Console.OpenStandardInput();
using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
var chunk = new byte[65536]; long received = 0; int count;
while ((count = await input.ReadAsync(chunk)) > 0) { digest.AppendData(chunk, 0, count); received += count; }
var job = OperatingSystem.IsWindows() ? ProbeJob.ReadLimits() : (0u, 0u, 0UL);
var data = new
{
    pid = Environment.ProcessId, arguments = args, inputBytes = received,
    inputHash = Convert.ToHexString(digest.GetHashAndReset()),
    securityLimits = Environment.GetEnvironmentVariable("LIBHEIF_SECURITY_LIMITS"),
    pluginPath = Environment.GetEnvironmentVariable("LIBHEIF_PLUGIN_PATH"),
    jobFlags = job.Item1, jobProcesses = job.Item2, jobMemory = job.Item3
};
await File.WriteAllTextAsync(report + ".tmp", JsonSerializer.Serialize(data));
File.Move(report + ".tmp", report);
if (versionProbe)
{
    if (mode == "version-timeout") { await Task.Delay(Timeout.Infinite); return 0; }
    if (mode == "version-nonzero") return 9;
    if (mode == "version-stderr-flood")
        for (var i = 0; i < 2048; i++) await Console.Error.WriteAsync(new string('x', 4096));
    await Console.Out.WriteAsync(mode switch
    {
        "version-compatible" => "PhotoShelf.HeifWorker PSH1; libheif 1.24.0-trusted; libde265 1.1.4+local\n",
        "version-old" => "PhotoShelf.HeifWorker PSH1; libheif 1.20.0; libde265 1.1.3\n",
        "version-invalid" => "PhotoShelf.HeifWorker PSH2; libheif 1.23.5; libde265 1.1.3\n",
        "version-overflow" => new string('x', 4096),
        _ => "PhotoShelf.HeifWorker PSH1; libheif 1.23.5; libde265 1.1.3\n"
    });
    return 0;
}
if (mode == "timeout") { await Task.Delay(Timeout.Infinite); return 0; }
if (mode == "nonzero") { await Console.Error.WriteAsync("Synthetic decoder failure."); return 9; }
if (mode == "stderr-flood")
{
    var text = new string('x', 4096);
    for (var i = 0; i < 2048; i++) await Console.Error.WriteAsync(text);
}
var width = mode == "oversize" ? (uint)maximum + 1 : 2u;
var height = mode == "zero-height" ? 0u : 1u;
var header = new byte[16]; (mode == "bad-header" ? "BAD!"u8 : "PSH1"u8).CopyTo(header);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), width);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), height);
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), mode == "bad-stride" ? 0 : width * 4);
var output = Console.OpenStandardOutput();
await output.WriteAsync(header);
await output.WriteAsync(mode == "truncated" ? new byte[] { 255, 0 } : new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 });
if (mode == "trailing") await output.WriteAsync(new byte[] { 42 });
await output.FlushAsync();
return 0;

internal static class ProbeJob
{
    public static (uint, uint, ulong) ReadLimits()
    {
        if (!QueryInformationJobObject(IntPtr.Zero, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (limits.Basic.LimitFlags, limits.Basic.ActiveProcessLimit, limits.ProcessMemoryLimit.ToUInt64());
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out ExtendedLimits information, uint length, IntPtr returnLength);
}
