using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoShelf.Application.Updates;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.10.14", "0.10.9", true)]
    [InlineData("v0.10.14-ultra", "0.10.13-ultra+abcdef", true)]
    [InlineData("0.10.14", "0.10.14-ultra", false)]
    [InlineData("0.10.8", "0.10.14", false)]
    [InlineData("0.10.15-beta", "0.10.14", false)]
    [InlineData("0.010.15", "0.10.14", false)]
    public void VersionsAreComparedNumericallyWithoutBranding(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateVersion.IsNewer(candidate, current));

    [Fact]
    public async Task CheckVerifiesSignatureAndNeverRequestsPackage()
    {
        using var fixture = new UpdateFixture();
        var handler = fixture.Handler();
        var service = fixture.Service(handler);
        var result = await service.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("0.10.14", result.Release!.Version);
        Assert.NotNull(result.CheckedAt);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri => uri.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitDownloadProducesIndependentlyVerifiedStage()
    {
        using var fixture = new UpdateFixture();
        var handler = fixture.Handler();
        var service = fixture.Service(handler);
        var release = (await service.CheckAsync()).Release!;
        Assert.DoesNotContain(handler.Requests, uri => uri.EndsWith(".zip", StringComparison.Ordinal));
        var stage = await service.DownloadAndStageAsync(release, fixture.Root);
        Assert.Single(handler.Requests, uri => uri.EndsWith(".zip", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(stage.PackageDirectory, "PhotoShelf.exe")));
        Assert.NotEmpty(stage.Files);
        var reverified = await UpdatePackageVerifier.VerifyStagedAsync(stage.StageDirectory, fixture.PublicKey);
        Assert.Equal(stage.Release.Version, reverified.Release.Version);
        Assert.Equal(fixture.Files.Count, reverified.Files.Count);
    }

    [Fact]
    public async Task CurrentReleaseDoesNotFetchManifestOrPackage()
    {
        using var fixture = new UpdateFixture();
        var handler = fixture.Handler();
        var result = await new UpdateService(new HttpClient(handler), fixture.PublicKey, "0.10.14-ultra").CheckAsync();
        Assert.Equal(UpdateCheckStatus.Current, result.Status);
        Assert.NotNull(result.CheckedAt);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailuresAreSilentAndNotMistakenForCurrent(HttpStatusCode status)
    {
        using var fixture = new UpdateFixture();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var result = await fixture.Service(handler).CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.CheckedAt);
        Assert.Null(result.Release);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TimeoutStopsCheckAndDoesNotRetry()
    {
        using var fixture = new UpdateFixture();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });
        var service = new UpdateService(new HttpClient(handler), fixture.PublicKey, "0.10.13", checkTimeout: TimeSpan.FromMilliseconds(30));
        var result = await service.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitIsRespectedAcrossManualRetries()
    {
        using var fixture = new UpdateFixture();
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        });
        var service = fixture.Service(handler);
        Assert.Equal(UpdateCheckStatus.Unavailable, (await service.CheckAsync()).Status);
        Assert.Equal(UpdateCheckStatus.Unavailable, (await service.CheckAsync()).Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ParallelChecksDoNotMultiplyNetworkRequests()
    {
        using var fixture = new UpdateFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, token) =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var service = fixture.Service(handler);
        var first = service.CheckAsync();
        await entered.Task;
        var second = await service.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, second.Status);
        Assert.Single(handler.Requests);
        resume.SetResult();
        await first;
    }

    [Fact]
    public async Task NetworkFailureIsSilent()
    {
        using var fixture = new UpdateFixture();
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("offline"));
        Assert.Equal(UpdateCheckStatus.Unavailable, (await fixture.Service(handler).CheckAsync()).Status);
    }

    [Fact]
    public async Task ForgedManifestIsSilentlyRejectedBeforeDownload()
    {
        using var fixture = new UpdateFixture();
        fixture.ManifestBytes[0] ^= 1;
        var handler = fixture.Handler();
        var result = await fixture.Service(handler).CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.DoesNotContain(handler.Requests, uri => uri.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisallowedRedirectIsNotFollowed()
    {
        using var fixture = new UpdateFixture();
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/steal");
            return Task.FromResult(response);
        });
        var result = await fixture.Service(handler).CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HttpsGitHubAssetRedirectWorks()
    {
        using var fixture = new UpdateFixture();
        var ordinary = fixture.Handler();
        var handler = new RecordingHandler((request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == fixture.Manifest.PackageUrl)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/fixture");
                return Task.FromResult(response);
            }
            if (request.RequestUri.Host == "release-assets.githubusercontent.com")
                return Task.FromResult(UpdateFixture.Response(fixture.ZipBytes));
            return ordinary.Respond(request, token);
        });
        var service = fixture.Service(handler);
        var result = await service.CheckAsync();
        var stage = await service.DownloadAndStageAsync(result.Release!, fixture.Root);
        Assert.Equal("0.10.14", stage.Release.Version);
    }

    [Fact]
    public async Task TruncatedPackageDoesNotProduceReadyManifest()
    {
        using var fixture = new UpdateFixture();
        var handler = fixture.Handler(packageOverride: fixture.ZipBytes[..^1]);
        var service = fixture.Service(handler);
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SameLengthTamperedPackageIsRejected()
    {
        using var fixture = new UpdateFixture();
        var tampered = fixture.ZipBytes.ToArray();
        tampered[10] ^= 1;
        var service = fixture.Service(fixture.Handler(packageOverride: tampered));
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SignedUnpackedSizeBoundsExtractionBeforePublishingReadyState()
    {
        using var fixture = new UpdateFixture();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture.Manifest with { UnpackedBytes = 1 }, UpdateFixture.JsonOptions);
        var release = UpdateManifestVerifier.Verify(bytes, fixture.Sign(bytes), fixture.PublicKey);
        var service = fixture.Service(fixture.Handler());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SignedPackageCannotContainFilesOutsideInventory()
    {
        using var fixture = new UpdateFixture(extraName: "unlisted-file.txt");
        var service = fixture.Service(fixture.Handler());
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelledDownloadDoesNotChangeCurrentProgramOrPublishPreparedRelease()
    {
        using var fixture = new UpdateFixture();
        var original = Path.Combine(fixture.Root, "existing-program.exe");
        await File.WriteAllBytesAsync(original, "old-program"u8.ToArray());
        var service = fixture.Service(fixture.Handler());
        var release = (await service.CheckAsync()).Release!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAndStageAsync(release, fixture.Root, cancellationToken: cancellation.Token));
        Assert.Equal("old-program"u8.ToArray(), await File.ReadAllBytesAsync(original));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DescriptorRestoreDoesNotReadPackageAndIsInsufficientForInstall()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.Service(fixture.Handler());
        var stage = await service.DownloadAndStageAsync((await service.CheckAsync()).Release!, fixture.Root);
        File.Delete(stage.PackagePath);
        var descriptor = await UpdatePackageVerifier.ReadStagedDescriptorAsync(stage.StageDirectory, fixture.PublicKey);
        Assert.Equal("0.10.14", descriptor.Release.Version);
        Assert.Empty(descriptor.Files);
        await Assert.ThrowsAsync<FileNotFoundException>(() => UpdatePackageVerifier.VerifyStagedAsync(stage.StageDirectory, fixture.PublicKey));
    }

    [Fact]
    public async Task UnchangedConsentedStagePassesFullVerification()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.Service(fixture.Handler());
        var consent = await service.DownloadAndStageAsync((await service.CheckAsync()).Release!, fixture.Root);
        var verified = await UpdatePackageVerifier.VerifyConsentedStageAsync(consent, fixture.PublicKey);
        Assert.Equal(consent.Release.ManifestBytes.ToArray(), verified.Release.ManifestBytes.ToArray());
        Assert.Equal(consent.Release.SignatureBytes.ToArray(), verified.Release.SignatureBytes.ToArray());
        Assert.NotEmpty(verified.Files);
    }

    [Theory]
    [InlineData("0.10.15", "synthetic-main")]
    [InlineData("0.10.14", "replacement-main")]
    public async Task AnotherValidSignedStageCannotReplaceUserConsent(string replacementVersion, string replacementExecutable)
    {
        using var fixture = new UpdateFixture();
        using var replacement = new UpdateFixture(version: replacementVersion, executablePayload: replacementExecutable);
        var service = fixture.Service(fixture.Handler());
        var consent = await service.DownloadAndStageAsync((await service.CheckAsync()).Release!, fixture.Root);
        await File.WriteAllBytesAsync(consent.PackagePath, replacement.ZipBytes);
        using (var zip = new ZipArchive(new MemoryStream(replacement.ZipBytes), ZipArchiveMode.Read))
            zip.ExtractToDirectory(consent.PackageDirectory, overwriteFiles: true);
        await File.WriteAllBytesAsync(consent.ManifestPath, replacement.ManifestBytes);
        await File.WriteAllBytesAsync(consent.SignaturePath, fixture.Sign(replacement.ManifestBytes));

        // The replacement is an independently valid release signed by the same trusted publisher.
        var verifiedReplacement = await UpdatePackageVerifier.VerifyStagedAsync(consent.StageDirectory, fixture.PublicKey);
        Assert.Equal(replacementVersion, verifiedReplacement.Release.Version);
        Assert.Equal(replacement.Manifest.PackageSha256, verifiedReplacement.Release.Manifest.PackageSha256);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyConsentedStageAsync(consent, fixture.PublicKey));
        Assert.Contains("accepted by the user", error.Message);
    }

    [Fact]
    public async Task ConsentedManifestBytesCannotChangeEvenForTheSamePackage()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.Service(fixture.Handler());
        var consent = await service.DownloadAndStageAsync((await service.CheckAsync()).Release!, fixture.Root);
        var replacement = fixture.ManifestBytes.Concat("\n"u8.ToArray()).ToArray();
        await File.WriteAllBytesAsync(consent.ManifestPath, replacement);
        await File.WriteAllBytesAsync(consent.SignaturePath, fixture.Sign(replacement));
        var reverified = await UpdatePackageVerifier.VerifyStagedAsync(consent.StageDirectory, fixture.PublicKey);
        Assert.Equal(consent.Release.Manifest, reverified.Release.Manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdatePackageVerifier.VerifyConsentedStageAsync(consent, fixture.PublicKey));
    }

    [Fact]
    public async Task ModifiedInventoryCannotAuthenticateModifiedExecutable()
    {
        using var fixture = new UpdateFixture();
        var service = fixture.Service(fixture.Handler());
        var stage = await service.DownloadAndStageAsync((await service.CheckAsync()).Release!, fixture.Root);
        var executable = Path.Combine(stage.PackageDirectory, "PhotoShelf.exe");
        var tampered = File.ReadAllBytes(executable);
        tampered[0] ^= 1;
        File.WriteAllBytes(executable, tampered);
        var inventoryPath = Path.Combine(stage.PackageDirectory, "package-manifest.json");
        var inventory = File.ReadAllText(inventoryPath);
        inventory = inventory.Replace(UpdateFixture.Hash(fixture.Files["PhotoShelf.exe"]), UpdateFixture.Hash(tampered), StringComparison.Ordinal);
        File.WriteAllText(inventoryPath, inventory);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageVerifier.VerifyExtractedDirectoryAsync(stage.PackageDirectory, stage.Release));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/absolute.exe")]
    [InlineData("PhotoShelf.exe:payload")]
    [InlineData("folder\\escape.exe")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.exe")]
    [InlineData("folder./escape.exe")]
    [InlineData("folder /escape.exe")]
    public async Task SignedArchiveStillRejectsUnsafePaths(string name)
    {
        using var fixture = new UpdateFixture(extraName: name);
        var service = fixture.Service(fixture.Handler());
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.Root, "photoshelf-update.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("PhotoShelf.exe", 0)]
    [InlineData("PHOTOSHELF.EXE", 0)]
    [InlineData("CODECS/extra.txt", 0)]
    [InlineData("link", 0xa000)]
    public async Task SignedArchiveRejectsDuplicateCaseCollisionAndSymlink(string name, int unixType)
    {
        using var fixture = new UpdateFixture(extraName: name, extraUnixType: unixType);
        var service = fixture.Service(fixture.Handler());
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, fixture.Root));
    }

    [Fact]
    public async Task ReparseStageDestinationIsRejectedWithoutWritingThroughIt()
    {
        using var fixture = new UpdateFixture();
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(fixture.Root, "link");
        Directory.CreateSymbolicLink(link, target);
        var service = fixture.Service(fixture.Handler());
        var release = (await service.CheckAsync()).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, link));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void WrongKeyAndUnsupportedSchemaRejectManifest()
    {
        using var fixture = new UpdateFixture();
        using var other = RSA.Create(2048);
        Assert.Throws<InvalidDataException>(() => UpdateManifestVerifier.Verify(fixture.ManifestBytes, fixture.Signature, other.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<InvalidDataException>(() => UpdateManifestVerifier.Verify(fixture.ManifestBytes, fixture.Signature, fixture.PublicKey, 6));
    }

    [Fact]
    public void SignedManifestCannotPointToAnotherRepository()
    {
        using var fixture = new UpdateFixture();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture.Manifest with { PackageUrl = "https://github.com/other/project/releases/download/v1/package.zip" }, UpdateFixture.JsonOptions);
        Assert.Throws<InvalidDataException>(() => UpdateManifestVerifier.Verify(bytes, fixture.Sign(bytes), fixture.PublicKey));
    }
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsoluteUri);
        return respond(request, cancellationToken);
    }
}

internal sealed class UpdateFixture : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly RSA _key = RSA.Create(2048);
    public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-update-tests-" + Guid.NewGuid().ToString("N"));
    public string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
    {
        ["PhotoShelf.exe"] = "synthetic-main"u8.ToArray(), ["PhotoShelf.Updater.exe"] = "synthetic-updater"u8.ToArray(),
        ["RUNNING.txt"] = Encoding.UTF8.GetBytes("PhotoShelf Ultra v0.10.14 — Windows x64\nSynthetic instructions"),
        ["codecs/heif/PhotoShelf.HeifWorker.exe"] = "synthetic-worker"u8.ToArray(),
        ["codecs/heif/heif.dll"] = "synthetic-heif"u8.ToArray(), ["codecs/heif/libde265.dll"] = "synthetic-de265"u8.ToArray(),
        ["codecs/heif/VERSION.txt"] = "synthetic-version"u8.ToArray(), ["codecs/heif/sources/sources.json"] = "{}"u8.ToArray(),
        ["licenses/test.txt"] = "synthetic-license"u8.ToArray(), ["codecs/heif/licenses/test.txt"] = "synthetic-license"u8.ToArray()
    };
    public byte[] ZipBytes { get; }
    public byte[] ManifestBytes { get; }
    public byte[] Signature { get; }
    public UpdateManifest Manifest { get; }

    public UpdateFixture(string? extraName = null, int extraUnixType = 0, string version = "0.10.14",
        string executablePayload = "synthetic-main")
    {
        Directory.CreateDirectory(Root);
        Files["PhotoShelf.exe"] = Encoding.UTF8.GetBytes(executablePayload);
        Files["RUNNING.txt"] = Encoding.UTF8.GetBytes($"PhotoShelf Ultra v{version} — Windows x64\nSynthetic instructions");
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, product = "PhotoShelf Ultra", version = version + "-ultra", commit = new string('a', 40),
            files = Files.Select(file => new { path = file.Key, length = file.Value.Length, sha256 = Hash(file.Value) }).ToArray()
        });
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in Files) WriteEntry(zip, name, bytes);
            WriteEntry(zip, "package-manifest.json", inventory);
            if (extraName is not null) WriteEntry(zip, extraName, "extra"u8.ToArray(), extraUnixType);
        }
        ZipBytes = zipBytes.ToArray();
        var tag = $"v{version}-ultra";
        Manifest = new UpdateManifest
        {
            ProtocolVersion = 1, Version = version, Runtime = "win-x64",
            PackageUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/download/{tag}/PhotoShelf-{tag}-win-x64.zip",
            PackageSha256 = Hash(ZipBytes), PackageManifestSha256 = Hash(inventory), PackageBytes = ZipBytes.Length,
            UnpackedBytes = Files.Sum(file => (long)file.Value.Length) + inventory.Length + (extraName is null ? 0 : 5),
            MinCatalogSchema = 5, MaxCatalogSchema = 5, ReleaseNotesUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/tag/{tag}"
        };
        ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(Manifest, JsonOptions);
        Signature = Sign(ManifestBytes);
    }

    public byte[] Sign(byte[] bytes) => _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public UpdateService Service(RecordingHandler handler) => new(new HttpClient(handler), PublicKey, "0.10.13-ultra");
    public RecordingHandler Handler(byte[]? packageOverride = null) => new((request, _) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == UpdateService.LatestReleaseUri.AbsoluteUri)
        {
            var prefix = Manifest.PackageUrl[..(Manifest.PackageUrl.LastIndexOf('/') + 1)];
            var tag = $"v{Manifest.Version}-ultra";
            var assets = new[]
            {
                new { name = "photoshelf-update.json", size = (long)ManifestBytes.Length, browser_download_url = prefix + "photoshelf-update.json" },
                new { name = "photoshelf-update.sig", size = (long)Signature.Length, browser_download_url = prefix + "photoshelf-update.sig" },
                new { name = $"PhotoShelf-{tag}-win-x64.zip", size = (long)ZipBytes.Length, browser_download_url = Manifest.PackageUrl }
            };
            return Task.FromResult(Response(JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = tag, assets })));
        }
        if (url.EndsWith("photoshelf-update.json", StringComparison.Ordinal)) return Task.FromResult(Response(ManifestBytes));
        if (url.EndsWith("photoshelf-update.sig", StringComparison.Ordinal)) return Task.FromResult(Response(Signature));
        if (url == Manifest.PackageUrl) return Task.FromResult(Response(packageOverride ?? ZipBytes));
        throw new InvalidOperationException("Unexpected request " + url);
    });

    public static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, int unixType = 0)
    {
        var entry = zip.CreateEntry(name);
        if (unixType != 0) entry.ExternalAttributes = unixType << 16;
        using var stream = entry.Open();
        stream.Write(bytes);
    }
    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
