using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoShelf.Application.Updates;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    Task<StagedUpdate> DownloadAndStageAsync(VerifiedUpdateRelease release, string updatesDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Checking never downloads packages; download is a separate explicit user action.</summary>
public sealed class UpdateService : IUpdateService
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/BigGluk23/PhotoShelf/releases/latest");
    private readonly HttpClient _client;
    private readonly string _publicKeyPem;
    private readonly string _currentVersion;
    private readonly int _currentCatalogSchema;
    private readonly TimeSpan _checkTimeout;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private string? _etag;
    private byte[]? _cachedReleaseJson;
    private DateTimeOffset _nextAllowedCheck;

    public UpdateService(HttpClient client, string publicKeyPem, string currentVersion, int currentCatalogSchema = 5,
        TimeSpan? checkTimeout = null)
    {
        if (!UpdateVersion.TryParse(currentVersion, out _)) throw new ArgumentException("Invalid current version.", nameof(currentVersion));
        _client = client;
        _publicKeyPem = publicKeyPem;
        _currentVersion = currentVersion;
        _currentCatalogSchema = currentCatalogSchema;
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(10);
    }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _checkGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
        try
        {
            if (DateTimeOffset.UtcNow < _nextAllowedCheck) return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_checkTimeout);
            var token = timeout.Token;
            using var response = await SendGetAsync(LatestReleaseUri, _etag, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // Respect GitHub throttling on manual clicks too; no scheduled retries are created.
                var now = DateTimeOffset.UtcNow;
                var retry = response.Headers.RetryAfter?.Date ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                    long.TryParse(resets.FirstOrDefault(), out var unix) && unix >= 0 && unix <= 253402300799)
                    retry = DateTimeOffset.FromUnixTimeSeconds(unix);
                _nextAllowedCheck = retry < now.AddSeconds(1) ? now.AddSeconds(1) :
                    retry > now.AddDays(1) ? now.AddDays(1) : retry;
            }
            byte[] releaseJson;
            if (response.StatusCode == HttpStatusCode.NotModified)
                releaseJson = _cachedReleaseJson ?? throw new InvalidDataException("Missing cached GitHub response.");
            else
            {
                response.EnsureSuccessStatusCode();
                releaseJson = await ReadBoundedResponseAsync(response, 256 * 1024, token).ConfigureAwait(false);
                _cachedReleaseJson = releaseJson;
                _etag = response.Headers.ETag?.ToString();
            }
            using var document = JsonDocument.Parse(releaseJson, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("GitHub did not return a stable release.");
            var tag = root.GetProperty("tag_name").GetString();
            if (!UpdateVersion.TryParse(tag, out var availableVersion) || tag != $"v{availableVersion}-ultra")
                throw new InvalidDataException("Unrecognized release tag.");
            if (!UpdateVersion.IsNewer(tag, _currentVersion))
                return new UpdateCheckResult(UpdateCheckStatus.Current, CheckedAt: DateTimeOffset.UtcNow);
            var assets = root.GetProperty("assets");
            var manifestUrl = FindAsset(assets, "photoshelf-update.json", tag, UpdateManifestVerifier.MaximumManifestBytes);
            var signatureUrl = FindAsset(assets, "photoshelf-update.sig", tag, 1024);
            var manifestBytes = await GetSmallAsync(manifestUrl, UpdateManifestVerifier.MaximumManifestBytes, token).ConfigureAwait(false);
            var signatureBytes = await GetSmallAsync(signatureUrl, 1024, token).ConfigureAwait(false);
            var release = UpdateManifestVerifier.Verify(manifestBytes, signatureBytes, _publicKeyPem, _currentCatalogSchema);
            if (release.Version != availableVersion.ToString()) throw new InvalidDataException("Signed manifest differs from release tag.");
            FindAsset(assets, $"PhotoShelf-{tag}-win-x64.zip", tag, UpdateManifestVerifier.MaximumPackageBytes,
                release.Manifest.PackageBytes);
            token.ThrowIfCancellationRequested();
            return new UpdateCheckResult(UpdateCheckStatus.Available, release, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or
            JsonException or InvalidOperationException or ArgumentException or CryptographicException or KeyNotFoundException or OverflowException or FormatException)
        {
            // Automatic and manual checks deliberately share the same silent failure contract.
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
        }
        finally { _checkGate.Release(); }
    }

    public async Task<StagedUpdate> DownloadAndStageAsync(VerifiedUpdateRelease release, string updatesDirectory,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _downloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An update download is already running.");
        try
        {
            // Revalidate consent's exact signed bytes, including schema support; never substitute the latest release.
            var verified = UpdateManifestVerifier.Verify(release.ManifestBytes.ToArray(), release.SignatureBytes.ToArray(),
                _publicKeyPem, _currentCatalogSchema);
            if (!UpdateVersion.IsNewer(verified.Version, _currentVersion)) throw new InvalidDataException("Update is not newer than installed version.");
            return await Task.Run(async () =>
            {
                var root = Path.GetFullPath(updatesDirectory);
                UpdatePackageVerifier.RejectReparseAncestors(root);
                Directory.CreateDirectory(root);
                UpdatePackageVerifier.RejectReparseAncestors(root);
                RequireSpace(root, verified.Manifest);
                var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                var zipPath = Path.Combine(stage, "package.zip");
                var pendingPath = Path.Combine(stage, "package.download");
                await DownloadPackageAsync(verified, pendingPath, progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pendingPath, zipPath, overwrite: false);
                progress?.Report(new UpdateDownloadProgress(verified.Manifest.PackageBytes, verified.Manifest.PackageBytes, "Проверка и распаковка"));
                await UpdatePackageVerifier.ExtractAsync(zipPath, Path.Combine(stage, "package"), verified, cancellationToken).ConfigureAwait(false);
                var packageDirectory = Path.Combine(stage, "package");
                var files = await UpdatePackageVerifier.VerifyExtractedDirectoryAsync(packageDirectory, verified, cancellationToken).ConfigureAwait(false);
                WriteNewDurable(Path.Combine(stage, "photoshelf-update.json"), verified.ManifestBytes.Span);
                WriteNewDurable(Path.Combine(stage, "photoshelf-update.sig"), verified.SignatureBytes.Span);
                // ZIP was authenticated while streaming; avoid hashing the complete download and extraction twice.
                // The separate helper performs full independent VerifyStagedAsync again after explicit install consent.
                return new StagedUpdate(stage, zipPath, packageDirectory, Path.Combine(stage, "photoshelf-update.json"),
                    Path.Combine(stage, "photoshelf-update.sig"), verified, files);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _downloadGate.Release(); }
    }

    private async Task DownloadPackageAsync(VerifiedUpdateRelease release, string path,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await SendGetAsync(new Uri(release.Manifest.PackageUrl), null, requestTimeout.Token).ConfigureAwait(false);
        requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != release.Manifest.PackageBytes)
            throw new InvalidDataException("Update download length differs from signed manifest.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var lastProgress = Stopwatch.StartNew();
        long total = 0;
        while (true)
        {
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var read = await input.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > release.Manifest.PackageBytes) throw new InvalidDataException("Update download exceeds signed size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (lastProgress.ElapsedMilliseconds >= 100)
            {
                progress?.Report(new UpdateDownloadProgress(total, release.Manifest.PackageBytes, "Скачивание"));
                lastProgress.Restart();
            }
        }
        if (total != release.Manifest.PackageBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Manifest.PackageSha256)
            throw new InvalidDataException("Update download is incomplete or has an invalid checksum.");
        output.Flush(flushToDisk: true);
    }

    private async Task<byte[]> GetSmallAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var response = await SendGetAsync(uri, null, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedResponseAsync(response, maximum, token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri uri, string? etag, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            ValidateNetworkUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PhotoShelf-Ultra-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Missing redirect target.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            // Also catches an injected HttpClient that followed a redirect before returning.
            if (response.RequestMessage?.RequestUri is { } finalUri) ValidateNetworkUri(finalUri);
            return response;
        }
        throw new HttpRequestException("Too many update redirects.");
    }

    private static void ValidateNetworkUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Unexpected update network origin.");
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Update response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) throw new InvalidDataException("Update response is too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static Uri FindAsset(JsonElement assets, string name, string tag, long maximumBytes, long? exactBytes = null)
    {
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 64) throw new InvalidDataException("Invalid GitHub assets.");
        var matching = assets.EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("Release asset is missing or ambiguous.");
        var size = matching[0].GetProperty("size").GetInt64();
        var url = matching[0].GetProperty("browser_download_url").GetString();
        if (size <= 0 || size > maximumBytes || exactBytes is not null && size != exactBytes ||
            url != $"https://github.com/BigGluk23/PhotoShelf/releases/download/{tag}/{name}")
            throw new InvalidDataException("Invalid GitHub asset metadata.");
        return new Uri(url);
    }

    private static void RequireSpace(string directory, UpdateManifest manifest)
    {
        var root = Path.GetPathRoot(directory) ?? throw new IOException("Update storage has no drive.");
        // Keep room for the helper's independent installed copy as well as this staged package.
        var required = checked(manifest.PackageBytes + 2 * manifest.UnpackedBytes +
            Math.Max(64L * 1024 * 1024, manifest.UnpackedBytes / 10));
        if (new DriveInfo(root).AvailableFreeSpace < required) throw new IOException("Not enough free space to prepare the update.");
    }

    private static void WriteNewDurable(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
