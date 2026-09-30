using System.Text.Json.Serialization;

namespace PhotoShelf.Application.Updates;

public enum UpdateCheckStatus { Available, Current, Unavailable }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, VerifiedUpdateRelease? Release = null,
    DateTimeOffset? CheckedAt = null);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes, string Stage);

public sealed record UpdateManifest
{
    public required int ProtocolVersion { get; init; }
    public required string Version { get; init; }
    public required string Runtime { get; init; }
    public required string PackageUrl { get; init; }
    public required string PackageSha256 { get; init; }
    public required string PackageManifestSha256 { get; init; }
    public required long PackageBytes { get; init; }
    public required long UnpackedBytes { get; init; }
    public required int MinCatalogSchema { get; init; }
    public required int MaxCatalogSchema { get; init; }
    public required string ReleaseNotesUrl { get; init; }
}

/// <summary>Created only after verification against the application's pinned signing key.</summary>
public sealed class VerifiedUpdateRelease
{
    internal VerifiedUpdateRelease(UpdateManifest manifest, byte[] json, byte[] signature)
    {
        Manifest = manifest;
        ManifestBytes = json.ToArray();
        SignatureBytes = signature.ToArray();
    }
    public UpdateManifest Manifest { get; }
    public string Version => Manifest.Version;
    public ReadOnlyMemory<byte> ManifestBytes { get; }
    public ReadOnlyMemory<byte> SignatureBytes { get; }
}

public sealed record StagedUpdate(string StageDirectory, string PackagePath, string PackageDirectory,
    string ManifestPath, string SignaturePath, VerifiedUpdateRelease Release,
    IReadOnlyList<UpdatePackageFile> Files);

public sealed record UpdatePackageFile(string Path, long Length, string Sha256);

public sealed record UpdatePreferences
{
    public int Schema { get; init; } = 1;
    public bool AutoCheck { get; init; } = true;
    public string? SkippedVersion { get; init; }
    public DateTimeOffset? SnoozeUntil { get; init; }
    public DateTimeOffset? LastSuccessfulCheck { get; init; }
    public string? PreparedStageId { get; init; }

    public bool ShouldSuggest(VerifiedUpdateRelease release, DateTimeOffset now) =>
        !string.Equals(SkippedVersion, release.Version, StringComparison.Ordinal) &&
        (SnoozeUntil is null || SnoozeUntil <= now);
}

public static class UpdateVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var metadataIndex = value.IndexOf('+');
        if (metadataIndex >= 0) value = value[..metadataIndex];
        if (value.StartsWith('v')) value = value[1..];
        if (value.EndsWith("-ultra", StringComparison.Ordinal)) value = value[..^6];
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 9 ||
            part.Any(character => character is < '0' or > '9') ||
            (part.Length > 1 && part[0] == '0'))) return false;
        return Version.TryParse(value, out version!);
    }

    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var candidateVersion) && TryParse(current, out var currentVersion) &&
        candidateVersion > currentVersion;
}
