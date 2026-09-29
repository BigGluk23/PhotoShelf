using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhotoShelf.Application.Media;

public sealed record RgbaFrame(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>A bounded pipe protocol. The separate worker is containment, not an OS sandbox.</summary>
public sealed partial class HeifDecoderClient
{
    public const long MaxInputBytes = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim DecoderSlot = new(1, 1);
    private readonly string _executablePath;
    private readonly string[] _argumentPrefix;
    private readonly TimeSpan _timeout;
    private readonly bool _requireBundledLibraries;

    public HeifDecoderClient(string? executablePath = null, IReadOnlyList<string>? argumentPrefix = null, TimeSpan? timeout = null,
        bool requireBundledLibraries = false)
    {
        _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "codecs", "heif", "PhotoShelf.HeifWorker.exe");
        _argumentPrefix = argumentPrefix?.ToArray() ?? [];
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _requireBundledLibraries = executablePath is null || requireBundledLibraries;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>Checks the bounded file-type box without consuming the caller's seekable stream.</summary>
    public static bool HasHeifSignature(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) return false;
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[16];
            if (!ReadExactlyOrEnd(stream, header[..8]) || !header.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (length == 1)
            {
                if (!ReadExactlyOrEnd(stream, header[8..])) return false;
                length = BinaryPrimitives.ReadUInt64BigEndian(header[8..]); headerLength = 16;
            }
            if (length < (ulong)(headerLength + 8) || length > 65536 || length > (ulong)stream.Length ||
                (length - (ulong)headerLength) % 4 != 0) return false;
            Span<byte> brand = stackalloc byte[4];
            var hevc = false; var generic = false; var avif = false;
            for (var offset = headerLength; offset < (int)length; offset += 4)
            {
                if (!ReadExactlyOrEnd(stream, brand)) return false;
                if (offset == headerLength + 4) continue; // minor version is not a brand
                hevc |= IsHevcBrand(brand);
                generic |= brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
                avif |= brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);
            }
            return hevc || (generic && !avif);
        }
        catch (EndOfStreamException) { return false; }
        finally { stream.Position = position; }
    }

    private static bool IsHevcBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
        brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8);

    private static bool ReadExactlyOrEnd(Stream stream, Span<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes[offset..]); if (read == 0) return false; offset += read;
        }
        return true;
    }

    public async Task<RgbaFrame> DecodeAsync(string path, int maxDimension, CancellationToken token = default)
    {
        if (maxDimension is < 32 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await DecoderSlot.WaitAsync(token).ConfigureAwait(false);
        FileStream? source = null; Process? process = null; WindowsHeifJob? job = null;
        var started = false; var cleanupTransferred = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>(4);
        try
        {
            VerifyInstallationFiles(); // Diagnose the installation before opening a user's original.
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length is < 16 or > MaxInputBytes) throw new InvalidDataException("HEIF input must be between 16 bytes and 128 MiB.");
            if (!HasHeifSignature(source)) throw new InvalidDataException("The file does not contain a supported HEIF file-type signature.");
            token.ThrowIfCancellationRequested();
            lifetime.CancelAfter(_timeout);
            process = new Process { StartInfo = CreateStartInfo(maxDimension) };
            if (!process.Start()) throw new IOException("Could not start the HEIF worker.");
            started = true;
            // No input reaches the decoder unless Windows containment has been successfully applied.
            if (OperatingSystem.IsWindows()) job = WindowsHeifJob.Attach(process);
            var input = SendInputAsync(source, process.StandardInput.BaseStream, lifetime.Token);
            var output = ReadOutputAsync(process.StandardOutput.BaseStream, maxDimension, lifetime.Token);
            var errors = DrainErrorsAsync(process.StandardError.BaseStream, lifetime.Token);
            var exit = WaitForActualExitAsync(process, lifetime.Token);
            tasks.AddRange([input, output, errors, exit]);
            var pending = new List<Task>(tasks);
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(lifetime.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
                if (ReferenceEquals(completed, exit) && exit.Result != 0)
                    throw new IOException($"HEIF worker exited with code {exit.Result}.");
            }
            if (process.ExitCode != 0) throw new IOException($"HEIF worker exited with code {process.ExitCode}: {errors.Result}");
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && lifetime.IsCancellationRequested)
        { throw new TimeoutException($"HEIF decoding exceeded {_timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds."); }
        finally
        {
            lifetime.Cancel();
            if (started && process is not null)
            {
                var dead = await TerminateAndWaitAsync(process, job).ConfigureAwait(false);
                if (!dead)
                {
                    // Fail closed even if the OS cannot immediately stop the worker: keep both
                    // the file sharing lock and global slot until actual process termination.
                    cleanupTransferred = true;
                    _ = ReapAsync(process, job, source!, tasks);
                }
                else await ObserveAsync(tasks).ConfigureAwait(false);
            }
            if (!cleanupTransferred)
            {
                job?.Dispose(); process?.Dispose();
                if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
                DecoderSlot.Release();
            }
            if (cleanupTransferred) throw new IOException("HEIF worker termination is not yet confirmed. Its input remains locked and further decoding is blocked until it exits.");
        }
    }

    private ProcessStartInfo CreateStartInfo(int maxDimension) =>
        CreateStartInfo(["--max-dimension", maxDimension.ToString(CultureInfo.InvariantCulture)]);

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in _argumentPrefix) info.ArgumentList.Add(argument);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys.Where(key => key.Equals("LIBHEIF_SECURITY_LIMITS", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("LIBHEIF_PLUGIN_PATH", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        // Prevent an inherited plugin search path from loading arbitrary external codec modules.
        info.Environment["LIBHEIF_PLUGIN_PATH"] = Path.GetDirectoryName(Path.GetFullPath(_executablePath))!;
        return info;
    }

    private static async Task SendInputAsync(FileStream source, Stream input, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long sent = 0; int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 65536), token).ConfigureAwait(false)) > 0)
            {
                sent += read; if (sent > MaxInputBytes) throw new InvalidDataException("HEIF input grew beyond 128 MiB.");
                await input.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            await input.FlushAsync(token).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); await input.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task<RgbaFrame> ReadOutputAsync(Stream output, int maximum, CancellationToken token)
    {
        var header = new byte[16]; await output.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("PSH1"u8)) throw new InvalidDataException("Invalid HEIF worker response magic.");
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var stride = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (width < 1 || width > maximum || height < 1 || height > maximum || stride != width * 4)
            throw new InvalidDataException("HEIF worker returned invalid or excessive dimensions.");
        var pixels = new byte[checked((int)(stride * height))];
        await output.ReadExactlyAsync(pixels, token).ConfigureAwait(false);
        var tail = new byte[1];
        if (await output.ReadAsync(tail, token).ConfigureAwait(false) != 0) throw new InvalidDataException("Trailing bytes in HEIF worker response.");
        return new((int)width, (int)height, (int)stride, pixels);
    }

    private static async Task<string> DrainErrorsAsync(Stream errors, CancellationToken token)
    {
        var buffer = new byte[4096]; var captured = new byte[4096]; var count = 0; int read;
        while ((read = await errors.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(read, captured.Length - count);
            buffer.AsSpan(0, take).CopyTo(captured.AsSpan(count)); count += take;
        }
        return new string(Encoding.UTF8.GetString(captured, 0, count).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }

    private static Task<int> WaitForActualExitAsync(Process process, CancellationToken token) => Task.Run(() =>
    {
        while (!process.WaitForExit(100)) token.ThrowIfCancellationRequested();
        return process.ExitCode;
    });

    private static async Task<bool> TerminateAndWaitAsync(Process process, WindowsHeifJob? job)
    {
        try { if (process.WaitForExit(0)) return true; } catch (InvalidOperationException) { return false; }
        try { job?.Terminate(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        try { return await Task.Run(() => process.WaitForExit(5000)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static async Task ObserveAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks) try { await task.ConfigureAwait(false); } catch { /* observe failure after termination */ }
    }

    private static async Task ReapAsync(Process process, WindowsHeifJob? job, FileStream? source, IReadOnlyList<Task> tasks)
    {
        // This exceptional path deliberately retains resources instead of permitting a move
        // while an unconfirmed worker might still be reading the original's pipe.
        while (!await TerminateAndWaitAsync(process, job).ConfigureAwait(false)) await Task.Delay(1000).ConfigureAwait(false);
        await ObserveAsync(tasks).ConfigureAwait(false);
        job?.Dispose(); process.Dispose();
        if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
        DecoderSlot.Release();
    }
}
