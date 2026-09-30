using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoShelf.Application.Updates;

public static class UpdateManifestVerifier
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumUnpackedBytes = 4L * 1024 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static VerifiedUpdateRelease Verify(byte[] json, byte[] signature, string publicKeyPem,
        int currentCatalogSchema = 5)
    {
        if (json.Length is 0 or > MaximumManifestBytes || signature.Length is 0 or > 1024)
            throw new InvalidDataException("Update manifest exceeds supported bounds.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(json, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Update manifest signature is invalid.");
        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                document.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("Duplicate manifest fields.");
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing update manifest.");
        if (manifest.ProtocolVersion != 1 || manifest.Runtime != "win-x64" ||
            !UpdateVersion.TryParse(manifest.Version, out var version) || version.ToString() != manifest.Version ||
            manifest.PackageBytes is <= 0 or > MaximumPackageBytes ||
            manifest.UnpackedBytes is <= 0 or > MaximumUnpackedBytes ||
            manifest.MinCatalogSchema < 1 || manifest.MaxCatalogSchema < manifest.MinCatalogSchema ||
            currentCatalogSchema < manifest.MinCatalogSchema || currentCatalogSchema > manifest.MaxCatalogSchema ||
            !IsSha256(manifest.PackageSha256) || !IsSha256(manifest.PackageManifestSha256))
            throw new InvalidDataException("Unsupported update manifest or catalog compatibility.");
        var tag = "v" + manifest.Version + "-ultra";
        var expectedPackageUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/download/{tag}/PhotoShelf-{tag}-win-x64.zip";
        if (manifest.PackageUrl != expectedPackageUrl ||
            manifest.ReleaseNotesUrl != $"https://github.com/BigGluk23/PhotoShelf/releases/tag/{tag}")
            throw new InvalidDataException("Unexpected update origin.");
        return new VerifiedUpdateRelease(manifest, json, signature);
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
