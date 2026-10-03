using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

/// <summary>Program updates never accept a caller-selected catalog or media directory.</summary>
public sealed class UpdateInstallationPaths
{
    public UpdateInstallationPaths(string? appRoot = null)
    {
        AppRoot = Path.GetFullPath(appRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoShelf"));
    }

    public string AppRoot { get; }
    public string ProgramRoot => Path.Combine(AppRoot, "program");
    public string VersionsRoot => Path.Combine(ProgramRoot, "versions");
    public string OperationsRoot => Path.Combine(ProgramRoot, "operations");
    public string StagingRoot => Path.Combine(AppRoot, "updates", "staging");
    public string RequestsRoot => Path.Combine(AppRoot, "updates", "requests");
    public string ActivePointerPath => Path.Combine(ProgramRoot, "active-v1.json");
    public string InstallationLockPath => Path.Combine(ProgramRoot, "installation.lock");
    public string StartupEntryLockPath => Path.Combine(ProgramRoot, "startup-entry.lock");

    internal string InstallationDirectory(string installationId) => Path.Combine(VersionsRoot, RequireId(installationId));
    internal string OperationDirectory(string requestId) => Path.Combine(OperationsRoot, RequireId(requestId));
    internal static string RequireId(string value) => Guid.TryParseExact(value, "N", out _) ? value :
        throw new InvalidDataException("Invalid update operation identity.");

    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void RequireDescendant(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("The update path is outside its owned directory.");
    }

    internal static void RejectLinks(string path, bool allowMissing = false)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Links and reparse points are not allowed in program update storage.");
            }
            catch (FileNotFoundException) when (allowMissing) { return; }
            catch (DirectoryNotFoundException) when (allowMissing) { return; }
        }
    }

    internal static void CreateDirectory(string path)
    {
        RejectLinks(path, allowMissing: true);
        Directory.CreateDirectory(path);
        RejectLinks(path);
    }

    internal static byte[] ReadBounded(string path, int maximumBytes)
    {
        RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 || input.Length > maximumBytes)
            throw new InvalidDataException("Update metadata exceeds supported bounds.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    internal static void WriteNewDurably(string path, ReadOnlySpan<byte> bytes)
    {
        RejectLinks(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    internal static T ReadJson<T>(string path, int maximumBytes = 64 * 1024)
    {
        var bytes = ReadBounded(path, maximumBytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
            document.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("Duplicate update metadata fields.");
        return JsonSerializer.Deserialize<T>(bytes, UpdateManifestVerifier.JsonOptions)
            ?? throw new InvalidDataException("Missing update metadata.");
    }
}
