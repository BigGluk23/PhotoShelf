using PhotoShelf.Application.Background;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class OwnedThumbnailCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-cache-test-" + Guid.NewGuid().ToString("N"));
    public OwnedThumbnailCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static string Key(int i) => i.ToString("x64");
    private static OwnedThumbnailCache CreateStore(string path, long quota) { var store = new OwnedThumbnailCache(path, quota); store.Trim(); return store; }
    private static void Write(OwnedThumbnailCache store, string key, int bytes) => store.Write(key, stream => stream.Write(new byte[bytes]));

    [Fact]
    public void QuotaRemovesOldestOwnedEntriesAndPreservesForeignFilesAndSubdirectories()
    {
        var store = CreateStore(_root, 1000);
        Write(store, Key(1), 10); Write(store, Key(2), 10); Write(store, Key(3), 10);
        var oldest = store.TryGetPath(Key(1))!;
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-10));
        var newest = store.TryGetPath(Key(3))!;
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(1));
        var original = Path.Combine(_root, "original.jpg"); File.WriteAllText(original, "must survive");
        var nested = Path.Combine(_root, "nested"); Directory.CreateDirectory(nested);
        var nestedOriginal = Path.Combine(nested, Path.GetFileName(oldest)); File.WriteAllText(nestedOriginal, "must survive too");
        store = new OwnedThumbnailCache(_root, 15);
        var result = store.Trim();
        Assert.Equal(30, result.BytesBefore);
        Assert.Equal(10, result.BytesAfter);
        Assert.Equal(2, result.RemovedCount);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
        Assert.Equal("must survive", File.ReadAllText(original));
        Assert.Equal("must survive too", File.ReadAllText(nestedOriginal));
    }

    [Fact]
    public void CacheTouchKeepsRecentlyUsedEntry()
    {
        var store = CreateStore(_root, 1000);
        Write(store, Key(1), 10); Write(store, Key(2), 10);
        var first = store.TryGetPath(Key(1))!;
        var second = store.TryGetPath(Key(2))!;
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddDays(-5));
        Assert.NotNull(store.TryGetPath(Key(1)));
        store = new OwnedThumbnailCache(_root, 10);
        store.Trim();
        Assert.True(File.Exists(first)); Assert.False(File.Exists(second));
    }

    [Fact]
    public void FailedWritePublishesNothingAndDoesNotOverwriteExistingEntry()
    {
        var store = CreateStore(_root, 100);
        Assert.Throws<IOException>(() => store.Write(Key(1), stream => { stream.WriteByte(1); throw new IOException("disk full"); }));
        Assert.Null(store.TryGetPath(Key(1)));
        Assert.Empty(Directory.GetFiles(_root));
        Write(store, Key(1), 10);
        store.Write(Key(1), _ => throw new IOException("existing cache must not be overwritten"));
        Assert.Equal(10, new FileInfo(store.TryGetPath(Key(1))!).Length);
    }

    [Fact]
    public void LinksCannotRedirectCacheReadsWritesOrCleanupIntoOriginals()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation depends on privileges; run this case on Unix CI too.
        var originals = Path.Combine(_root, "originals"); Directory.CreateDirectory(originals);
        var target = Path.Combine(originals, "ps-thumb-v2-" + Key(1) + ".png"); File.WriteAllText(target, "original bytes");
        var cache = Path.Combine(_root, "cache"); Directory.CreateDirectory(cache);
        var link = Path.Combine(cache, Path.GetFileName(target)); File.CreateSymbolicLink(link, target);
        var store = new OwnedThumbnailCache(cache, 1);
        Assert.Null(store.TryGetPath(Key(1)));
        Assert.Equal(0, store.Trim().RemovedCount);
        Assert.Equal("original bytes", File.ReadAllText(target));
        var directoryLink = Path.Combine(_root, "redirect"); Directory.CreateSymbolicLink(directoryLink, originals);
        var redirected = new OwnedThumbnailCache(directoryLink, 1);
        Assert.Null(redirected.TryGetPath(Key(1)));
        redirected.Write(Key(2), _ => throw new InvalidOperationException("Must not write through link"));
        Assert.Equal(0, redirected.Trim().RemovedCount);
        Assert.Equal("original bytes", File.ReadAllText(target));
    }

    [Fact]
    public void NewWritesNeverPublishBeyondQuotaAndWaitForInventory()
    {
        var store = new OwnedThumbnailCache(_root, 15);
        Write(store, Key(1), 10);
        Assert.Empty(Directory.GetFiles(_root));
        store.Trim();
        Write(store, Key(1), 10);
        Write(store, Key(2), 10);
        Assert.NotNull(store.TryGetPath(Key(1)));
        Assert.Null(store.TryGetPath(Key(2)));
        Assert.Single(Directory.GetFiles(_root));
        store.Trim(reserveBytes: 10);
        Write(store, Key(2), 10);
        Assert.NotNull(store.TryGetPath(Key(2)));
    }

    [Fact]
    public void InvalidKeyCannotEscapeCacheDirectory()
    {
        var store = CreateStore(_root, 100);
        Assert.Throws<ArgumentException>(() => store.Write("../original.jpg", _ => { }));
        Assert.Empty(Directory.GetFiles(_root));
    }
}
