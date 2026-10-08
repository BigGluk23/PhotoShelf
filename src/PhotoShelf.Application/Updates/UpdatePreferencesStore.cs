using System.Text.Json;

namespace PhotoShelf.Application.Updates;

/// <summary>Independent of catalog migration: a damaged preference file never silently reenables network requests.</summary>
public sealed class UpdatePreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumBytes = 32 * 1024;

    public async Task<UpdatePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    CleanupPendingFiles();
                    UpdatePackageVerifier.RejectReparseAncestors(path);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > MaximumBytes) return Disabled();
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
                        schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                        !root.TryGetProperty("autoCheck", out var autoCheck) ||
                        autoCheck.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                        root.EnumerateObject().Count()) return Disabled();
                    var preferences = root.Deserialize<UpdatePreferences>(UpdateManifestVerifier.JsonOptions);
                    if (preferences?.PreparedStageId is { } stageId &&
                        (!Guid.TryParseExact(stageId, "N", out var id) || id.ToString("N") != stageId))
                        return preferences with { PreparedStageId = null };
                    return preferences ?? Disabled();
                }
                catch (FileNotFoundException) { return new UpdatePreferences(); }
                catch (DirectoryNotFoundException) { return new UpdatePreferences(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { return Disabled(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(UpdatePreferences preferences, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var fullPath = Path.GetFullPath(path);
                UpdatePackageVerifier.RejectReparseAncestors(fullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                UpdatePackageVerifier.RejectReparseAncestors(fullPath);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences with { Schema = 1 }, UpdateManifestVerifier.JsonOptions);
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("Update preferences exceed supported bounds.");
                var temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes);
                        stream.Flush(flushToDisk: true);
                    }
                    // One atomic name replacement: failed writes leave the previous opt-out intact.
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, fullPath, overwrite: true);
                }
                finally { TryDelete(temporary); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static UpdatePreferences Disabled() => new() { AutoCheck = false };

    private void CleanupPendingFiles()
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) return;
        UpdatePackageVerifier.RejectReparseAncestors(directory);
        var prefix = Path.GetFileName(fullPath) + ".pending-";
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            var suffix = Path.GetFileName(candidate)[prefix.Length..];
            if (Guid.TryParseExact(suffix, "N", out _)) TryDelete(candidate);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            UpdatePackageVerifier.RejectReparseAncestors(file);
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException) { }
    }
}
