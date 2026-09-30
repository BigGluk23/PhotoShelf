using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoShelf.Application.Updates;

public static class UpdatePackageVerifier
{
    public const string PackageManifestName = "package-manifest.json";
    private const int MaximumFiles = 1024;
    private const int MaximumInventoryBytes = 256 * 1024;
    private static readonly string[] RequiredFiles =
    ["PhotoShelf.exe", "PhotoShelf.Updater.exe", "RUNNING.txt", "codecs/heif/PhotoShelf.HeifWorker.exe",
        "codecs/heif/heif.dll", "codecs/heif/libde265.dll", "codecs/heif/VERSION.txt", "codecs/heif/sources/sources.json"];

    /// <summary>Only restores display state. Installation must perform full VerifyStagedAsync again.</summary>
    public static Task<StagedUpdate> ReadStagedDescriptorAsync(string stageDirectory, string publicKeyPem,
        int currentCatalogSchema = 5, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(stageDirectory);
        RejectReparseAncestors(root);
        var manifestPath = Path.Combine(root, "photoshelf-update.json");
        var signaturePath = Path.Combine(root, "photoshelf-update.sig");
        var release = UpdateManifestVerifier.Verify(ReadBoundedFile(manifestPath, UpdateManifestVerifier.MaximumManifestBytes),
            ReadBoundedFile(signaturePath, 1024), publicKeyPem, currentCatalogSchema);
        return new StagedUpdate(root, Path.Combine(root, "package.zip"), Path.Combine(root, "package"),
            manifestPath, signaturePath, release, Array.Empty<UpdatePackageFile>());
    }, cancellationToken);

    public static Task<StagedUpdate> VerifyStagedAsync(string stageDirectory, string publicKeyPem,
        int currentCatalogSchema = 5, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(stageDirectory);
        RejectReparseAncestors(root);
        var manifestPath = Path.Combine(root, "photoshelf-update.json");
        var signaturePath = Path.Combine(root, "photoshelf-update.sig");
        var json = ReadBoundedFile(manifestPath, UpdateManifestVerifier.MaximumManifestBytes);
        var signature = ReadBoundedFile(signaturePath, 1024);
        var release = UpdateManifestVerifier.Verify(json, signature, publicKeyPem, currentCatalogSchema);
        var zipPath = Path.Combine(root, "package.zip");
        await VerifyFileAsync(zipPath, release.Manifest.PackageBytes, release.Manifest.PackageSha256, cancellationToken).ConfigureAwait(false);
        var packageDirectory = Path.Combine(root, "package");
        var files = await VerifyExtractedDirectoryAsync(packageDirectory, release, cancellationToken).ConfigureAwait(false);
        // Match the inventory to the signed ZIP, not merely to a separately mutable extracted manifest.
        await VerifyInventoryMatchesArchiveAsync(zipPath, packageDirectory, cancellationToken).ConfigureAwait(false);
        return new StagedUpdate(root, zipPath, packageDirectory, manifestPath, signaturePath, release, files);
    }, cancellationToken);

    /// <summary>Revalidates mutable staging while preserving the exact signed release the user accepted.</summary>
    public static async Task<StagedUpdate> VerifyConsentedStageAsync(StagedUpdate consent, string publicKeyPem,
        int currentCatalogSchema = 5, CancellationToken cancellationToken = default)
    {
        var verified = await VerifyStagedAsync(consent.StageDirectory, publicKeyPem, currentCatalogSchema,
            cancellationToken).ConfigureAwait(false);
        if (!verified.Release.ManifestBytes.Span.SequenceEqual(consent.Release.ManifestBytes.Span) ||
            !verified.Release.SignatureBytes.Span.SequenceEqual(consent.Release.SignatureBytes.Span))
            throw new InvalidDataException("The prepared update differs from the release accepted by the user.");
        return verified;
    }

    public static Task<IReadOnlyList<UpdatePackageFile>> VerifyExtractedDirectoryAsync(string directory,
        VerifiedUpdateRelease release, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(directory);
        RejectReparseAncestors(root);
        var inventoryPath = Path.Combine(root, PackageManifestName);
        var inventoryBytes = ReadBoundedFile(inventoryPath, MaximumInventoryBytes);
        if (Convert.ToHexStringLower(SHA256.HashData(inventoryBytes)) != release.Manifest.PackageManifestSha256)
            throw new InvalidDataException("Package inventory does not match signed update manifest.");
        var expected = ReadInventory(inventoryBytes, release.Version);
        var actual = EnumerateSafeFiles(root, cancellationToken);
        if (actual.Count != expected.Count + 1 || !actual.ContainsKey(PackageManifestName))
            throw new InvalidDataException("Extracted package file set differs from inventory.");
        long total = inventoryBytes.Length;
        foreach (var entry in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(entry.Path, out var actualPath) ||
                !Path.GetRelativePath(root, actualPath).Replace(Path.DirectorySeparatorChar, '/').Equals(entry.Path, StringComparison.Ordinal))
                throw new InvalidDataException("Missing or case-mismatched package file.");
            total = checked(total + entry.Length);
            if (total > release.Manifest.UnpackedBytes) throw new InvalidDataException("Extracted package exceeds declared size.");
            await VerifyFileAsync(actualPath, entry.Length, entry.Sha256, cancellationToken).ConfigureAwait(false);
        }
        if (total != release.Manifest.UnpackedBytes) throw new InvalidDataException("Extracted package size mismatch.");
        var running = ReadBoundedFile(Path.Combine(root, "RUNNING.txt"), 128 * 1024);
        var heading = Encoding.UTF8.GetString(running).TrimStart('\uFEFF').Split('\n')[0].TrimEnd('\r');
        if (heading != $"PhotoShelf Ultra v{release.Version} — Windows x64")
            throw new InvalidDataException("Packaged instructions have a different version.");
        return (IReadOnlyList<UpdatePackageFile>)expected.AsReadOnly();
    }, cancellationToken);

    internal static async Task ExtractAsync(string zipPath, string destination, VerifiedUpdateRelease release,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Extraction target already exists.");
        RejectReparseAncestors(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(destination);
        using var zipFile = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(zipFile, ZipArchiveMode.Read);
        var entries = ValidateArchive(archive, release.Manifest.UnpackedBytes);
        foreach (var (entry, name) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(path)!;
            RejectReparseAncestors(parent);
            Directory.CreateDirectory(parent);
            RejectReparseAncestors(parent);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(input, output, entry.Length, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private static List<(ZipArchiveEntry Entry, string Name)> ValidateArchive(ZipArchive archive, long unpackedBytes)
    {
        if (archive.Entries.Count is 0 or > 4096) throw new InvalidDataException("Invalid ZIP entry count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var name = isDirectory ? entry.FullName[..^1] : entry.FullName;
            ValidateRelativePath(name);
            if (!paths.Add(name)) throw new InvalidDataException("Duplicate or case-colliding ZIP entry.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                unixType != 0 && unixType != (isDirectory ? 0x4000 : 0x8000))
                throw new InvalidDataException("Links and special files are not allowed in update packages.");
            var parts = name.Split('/');
            for (var count = 1; count <= parts.Length; count++)
            {
                var prefix = string.Join('/', parts.Take(count));
                if (pathCasing.TryGetValue(prefix, out var previous) && previous != prefix)
                    throw new InvalidDataException("Case-colliding ZIP directory.");
                pathCasing[prefix] = prefix;
            }
            if (isDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("Nonempty ZIP directory.");
                continue;
            }
            files.Add(name);
            total = checked(total + entry.Length);
            if (entry.Length < 0 || total > unpackedBytes || result.Count >= MaximumFiles + 1)
                throw new InvalidDataException("ZIP size or file count exceeds signed bounds.");
            result.Add((entry, name));
        }
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            for (var count = 1; count < parts.Length; count++)
                if (files.Contains(string.Join('/', parts.Take(count))))
                    throw new InvalidDataException("ZIP file is also used as a directory.");
        }
        if (total != unpackedBytes) throw new InvalidDataException("ZIP unpacked size mismatch.");
        return result;
    }

    private static async Task VerifyInventoryMatchesArchiveAsync(string zipPath, string directory, CancellationToken cancellationToken)
    {
        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => entry.FullName == PackageManifestName).ToArray();
        if (entries.Length != 1 || entries[0].Length > MaximumInventoryBytes)
            throw new InvalidDataException("ZIP has no unique package inventory.");
        await using var stream = entries[0].Open();
        using var expected = new MemoryStream();
        await CopyBoundedAsync(stream, expected, entries[0].Length, cancellationToken).ConfigureAwait(false);
        var actual = ReadBoundedFile(Path.Combine(directory, PackageManifestName), MaximumInventoryBytes);
        if (!actual.AsSpan().SequenceEqual(expected.ToArray()))
            throw new InvalidDataException("Extracted inventory differs from signed package.");
    }

    private static List<UpdatePackageFile> ReadInventory(byte[] bytes, string version)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("product").GetString() != "PhotoShelf Ultra" ||
            root.GetProperty("version").GetString() != version + "-ultra")
            throw new InvalidDataException("Package identity mismatch.");
        var commit = root.GetProperty("commit").GetString();
        if (commit is not { Length: 40 } || commit.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid package revision.");
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > MaximumFiles)
            throw new InvalidDataException("Invalid package inventory count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UpdatePackageFile>();
        foreach (var file in files.EnumerateArray())
        {
            var name = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing inventory path.");
            ValidateRelativePath(name);
            var length = file.GetProperty("length").GetInt64();
            var hash = file.GetProperty("sha256").GetString();
            if (name.Equals(PackageManifestName, StringComparison.OrdinalIgnoreCase) || !names.Add(name) ||
                length < 0 || length > UpdateManifestVerifier.MaximumUnpackedBytes || !UpdateManifestVerifier.IsSha256(hash))
                throw new InvalidDataException("Invalid package inventory entry.");
            result.Add(new UpdatePackageFile(name, length, hash!));
        }
        if (RequiredFiles.Any(required => !result.Any(file => file.Path == required)) ||
            !result.Any(file => file.Path.StartsWith("licenses/", StringComparison.Ordinal)) ||
            !result.Any(file => file.Path.StartsWith("codecs/heif/licenses/", StringComparison.Ordinal)))
            throw new InvalidDataException("Required update executable, decoder or license files are missing.");
        return result;
    }

    private static Dictionary<string, string> EnumerateSafeFiles(string root, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root);
        var directoryCount = 0;
        while (directories.Count != 0)
        {
            if (++directoryCount > 4096) throw new InvalidDataException("Too many package directories.");
            var directory = directories.Pop();
            RejectReparseAncestors(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativePath(name);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) { directories.Push(entry); continue; }
                if (!result.TryAdd(name, entry) || result.Count > MaximumFiles + 1)
                    throw new InvalidDataException("Too many or case-colliding package files.");
            }
        }
        return result;
    }

    public static void ValidateRelativePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Contains('\\') || name.Contains(':') ||
            name.Any(character => character < 32 || "<>\"|?*".Contains(character)))
            throw new InvalidDataException("Unsafe package path.");
        foreach (var part in name.Split('/'))
        {
            var device = part.Split('.')[0].ToUpperInvariant();
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "123456789¹²³".Contains(device[3]))
                throw new InvalidDataException("Unsafe package path component.");
        }
    }

    public static void RejectReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Update location contains a link or reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static byte[] ReadBoundedFile(string path, int maximum)
    {
        RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException("Invalid update metadata length.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Update metadata changed during reading.");
        return bytes;
    }

    internal static async Task VerifyFileAsync(string path, long length, string expectedHash, CancellationToken cancellationToken)
    {
        RejectReparseAncestors(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != length) throw new InvalidDataException("Update file length mismatch.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (hash != expectedHash || stream.Position != length || stream.Length != length)
            throw new InvalidDataException("Update file checksum mismatch.");
    }

    internal static async Task CopyBoundedAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedLength) throw new InvalidDataException("Update content exceeds declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total != expectedLength) throw new InvalidDataException("Truncated update content.");
    }
}
