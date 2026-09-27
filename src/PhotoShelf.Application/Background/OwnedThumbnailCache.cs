using System.Security.Cryptography;
using System.Text;

namespace PhotoShelf.Application.Background;

/// <summary>Only generated, prefixed cache entries in this exact directory may be removed. Never follows links or recurses.</summary>
public sealed class OwnedThumbnailCache
{
    private const string Prefix = "ps-thumb-v2-";
    private readonly string _directory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private long _knownBytes = -1;
    private long _generation;
    private bool _maintenance;
    public OwnedThumbnailCache(string directory, long quotaBytes)
    {
        _directory = Path.GetFullPath(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(quotaBytes, 1);
        QuotaBytes = quotaBytes;
    }
    public long QuotaBytes { get; }
    public string DirectoryPath => _directory;
    public long Generation { get { lock (_sync) return _generation; } }
    public string GetKey(string sourcePath, long length, long lastWriteTicks, int width) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v2|{sourcePath}|{length}|{lastWriteTicks}|{width}"))).ToLowerInvariant();

    public string? TryGetPath(string key)
    {
        lock (_sync)
        {
            if (!IsSafeDirectory()) return null;
            var path = EntryPath(key);
            if (!IsRegularFile(path)) return null;
            try
            {
                // Last write is the cache's LRU timestamp, never a timestamp on the source media.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    // Call Trim once in a background maintenance job to inventory the directory.
    // Until then, or while full, writes are skipped; viewing originals never waits for inventory.
    public void Write(string key, Action<Stream> write, long? requestGeneration = null)
    {
        lock (_sync)
        {
            _ = EntryPath(key);
            if ((requestGeneration.HasValue && requestGeneration != _generation) ||
                _knownBytes < 0 || _maintenance || _knownBytes >= QuotaBytes || !IsSafeDirectory()) return;
            Directory.CreateDirectory(_directory);
            if (!IsSafeDirectory()) return;
            var path = EntryPath(key);
            if (File.Exists(path)) return;
            var temporary = Path.Combine(_directory, $"{Prefix}{key}-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
                if (!IsSafeDirectory()) return;
                var bytes = new FileInfo(temporary).Length;
                if (bytes > QuotaBytes - _knownBytes) return;
                File.Move(temporary, path, overwrite: false);
                _knownBytes += bytes;
            }
            finally
            {
                // Only the temporary file created by this call is eligible for cleanup.
                if (IsSafeDirectory() && IsRegularFile(temporary)) File.Delete(temporary);
            }
        }
    }

    public CacheTrimResult Trim(CancellationToken cancellationToken = default, long reserveBytes = 0)
        => TrimCore(cancellationToken, reserveBytes, clear: false);

    public CacheTrimResult Clear(CancellationToken cancellationToken = default)
        => TrimCore(cancellationToken, QuotaBytes, clear: true);

    private CacheTrimResult TrimCore(CancellationToken cancellationToken, long reserveBytes, bool clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        _maintenanceGate.Wait(cancellationToken);
        lock (_sync) { _maintenance = true; if (clear) _generation++; }
        long finalBytes = -1;
        try
        {
            if (!IsSafeDirectory())
            {
                if (clear) throw new IOException("Cache cleanup refused: the directory path contains a link or reparse point.");
                return new(0, 0, 0);
            }
            if (!Directory.Exists(_directory)) { finalBytes = 0; return new(0, 0, 0); }
            var targetBytes = Math.Max(0, QuotaBytes - reserveBytes);
            var entries = new List<FileInfo>();
            long total = 0;
            foreach (var path in Directory.EnumerateFiles(_directory, clear ? Prefix + "*" : Prefix + "*.png", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!IsOwnedName(name) && !(clear && IsOwnedTemporaryName(name))) continue;
                if (!IsRegularFile(path))
                {
                    if (clear && new FileInfo(path) is { LinkTarget: not null })
                        throw new IOException("Cache cleanup refused an owned-name link; its target was preserved.");
                    if (clear && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Cache cleanup refused an owned-name reparse point.");
                    continue;
                }
                try { var file = new FileInfo(path); total += file.Length; entries.Add(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var before = total;
            var removed = 0;
            foreach (var file in entries.OrderBy(entry => entry.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!clear && total <= targetBytes) break;
                if (!IsSafeDirectory())
                {
                    if (clear) throw new IOException("Cache directory changed during cleanup; no further files were removed.");
                    break;
                }
                try
                {
                    if (!IsRegularFile(file.FullName)) continue;
                    var length = file.Length;
                    File.Delete(file.FullName);
                    total -= length;
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            finalBytes = total;
            return new(before, total, removed);
        }
        finally
        {
            lock (_sync) { _knownBytes = finalBytes; if (clear) _generation++; _maintenance = false; }
            _maintenanceGate.Release();
        }
    }

    private string EntryPath(string key)
    {
        if (key.Length != 64 || !key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Invalid generated cache key.", nameof(key));
        return Path.Combine(_directory, Prefix + key + ".png");
    }
    private static bool IsOwnedName(string name) => name.Length == Prefix.Length + 64 + 4 && name.StartsWith(Prefix, StringComparison.Ordinal)
        && name.EndsWith(".png", StringComparison.Ordinal) && name.AsSpan(Prefix.Length, 64).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsOwnedTemporaryName(string name) => name.Length == Prefix.Length + 64 + 1 + 32 + 4 &&
        name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
        name[Prefix.Length + 64] == '-' &&
        name.AsSpan(Prefix.Length, 64).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        name.AsSpan(Prefix.Length + 65, 32).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private bool IsSafeDirectory()
    {
        // A redirect anywhere in the cache path must not turn cache cleanup into original-file cleanup.
        for (var current = new DirectoryInfo(_directory); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)) return false;
        return true;
    }
    private static bool IsRegularFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.LinkTarget is null && (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    }
}

public sealed record CacheTrimResult(long BytesBefore, long BytesAfter, int RemovedCount);
