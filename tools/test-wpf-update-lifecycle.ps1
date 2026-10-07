param(
    [Parameter(Mandatory = $true)][string]$ReportDirectory,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -or
    $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'The normal-entrypoint WPF updater test requires a disposable Windows GitHub Actions runner.'
}
$localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PhotoShelf'
$roamingRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'PhotoShelf'
foreach ($path in @($localRoot, $roamingRoot)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to test against an existing PhotoShelf profile: $path" }
}
if (@(Get-Process -Name 'PhotoShelf', 'PhotoShelf.Updater' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Refusing to run alongside an existing PhotoShelf application/updater.'
}
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotoShelf-WpfUpdateLifecycle-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$sourceRoot = Join-Path $runRoot 'source'
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify source revision.' }
$version = (& $Python (Join-Path $repoRoot 'tools/harness_checks.py') version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source version.' }
# The copied working files must be the exact committed source represented by the evidence SHA.
& git -C $repoRoot diff --quiet HEAD -- src tests/Fixtures docs/licenses Directory.Build.props global.json NuGet.Config nuget.config
if ($LASTEXITCODE -ne 0) { throw 'Refusing to label modified working source as the committed revision.' }
$tracked = @(& git -C $repoRoot ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the source revision.' }
# Copy tracked source only. Production checkout/public key/versions are never patched.
foreach ($relative in $tracked) {
    if ($relative -notmatch '^(src/|tests/Fixtures/|docs/licenses/|Directory.Build.props$|global.json$|NuGet.Config$|nuget.config$)') { continue }
    if ($relative -eq 'src/PhotoShelf.Application/Updates/TrustedUpdateKey.pem') { continue }
    $source = Join-Path $repoRoot $relative
    $target = Join-Path $sourceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
# Deterministic scheduling is compiled into this owned source copy only. Shipping executables
# have no environment/argument/config switch for these gates, and test packages are never released.
$controlRoot = Join-Path $runRoot 'lifecycle-control'
New-Item -ItemType Directory -Path $controlRoot | Out-Null
$controlOwner = [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $controlRoot 'owned-test-control.txt'), $controlOwner)
$gateSource = @'
using System.Diagnostics;
using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

public static class UpdateLifecycleTestGate
{
    private const string ControlRoot = __CONTROL_ROOT__;
    private const string Owner = __CONTROL_OWNER__;

    public static void Wait(string checkpoint)
    {
        Record(checkpoint + ".reached.json", checkpoint);
        var timer = Stopwatch.StartNew();
        var release = Path.Combine(ControlRoot, checkpoint + ".release");
        while (!File.Exists(release))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Owned WPF lifecycle gate was not released: " + checkpoint);
            Thread.Sleep(25);
        }
    }

    public static void RecordCatalogEntry() => Record("catalog-entry-" + Environment.ProcessId + ".json", "catalog-entry");

    private static void Record(string file, string checkpoint)
    {
        UpdatePackageVerifier.RejectReparseAncestors(ControlRoot);
        if (File.ReadAllText(Path.Combine(ControlRoot, "owned-test-control.txt")) != Owner)
            throw new InvalidOperationException("Missing owned WPF lifecycle control marker.");
        var path = Path.Combine(ControlRoot, file);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, new { checkpoint, processId = Environment.ProcessId,
                version = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3), atUtc = DateTimeOffset.UtcNow });
            output.Flush(true);
        }
        File.Move(pending, path, overwrite: false);
    }
}
'@
$gateSource = $gateSource.Replace('__CONTROL_ROOT__', ($controlRoot | ConvertTo-Json -Compress)).Replace('__CONTROL_OWNER__', ($controlOwner | ConvertTo-Json -Compress))
[IO.File]::WriteAllText((Join-Path $sourceRoot 'src/PhotoShelf.Application/Updates/Installation/UpdateLifecycleTestGate.cs'), $gateSource, [Text.UTF8Encoding]::new($false))
function Replace-OwnedSourceOnce([string]$RelativePath, [string]$Before, [string]$After) {
    $path = Join-Path $sourceRoot $RelativePath
    $text = [IO.File]::ReadAllText($path)
    if ([regex]::Matches($text, [regex]::Escape($Before)).Count -ne 1) { throw "Expected exactly one test instrumentation target in $RelativePath" }
    [IO.File]::WriteAllText($path, $text.Replace($Before, $After), [Text.UTF8Encoding]::new($false))
}
foreach ($checkpoint in @('before-pointer-publish', 'pointer-published')) {
    $before = '_options.Checkpoint?.Invoke("' + $checkpoint + '");'
    Replace-OwnedSourceOnce 'src/PhotoShelf.Application/Updates/Installation/UpdateInstaller.cs' $before `
        ($before + "`n        UpdateLifecycleTestGate.Wait(`"$checkpoint`");")
}
$beforeHealth = 'UpdateStartupHealth.ReportReady(Environment.ProcessPath!, _updatePaths)'
$instrumentedHealth = '{ if (Environment.GetEnvironmentVariable(UpdateStartupHealth.RequestVariable) is not null) UpdateLifecycleTestGate.Wait("candidate-before-health"); ' + $beforeHealth + '; }'
Replace-OwnedSourceOnce 'src/PhotoShelf.Desktop/MainWindow.Updates.cs' $beforeHealth $instrumentedHealth
$beforeStorage = 'var location = LocalCatalogStore.StorageLocation;'
Replace-OwnedSourceOnce 'src/PhotoShelf.Desktop/App.Storage.cs' $beforeStorage `
    ('PhotoShelf.Application.Updates.Installation.UpdateLifecycleTestGate.RecordCatalogEntry();' + "`n        " + $beforeStorage)
$codecSource = Join-Path $repoRoot 'artifacts/heif-codec/win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $codecSource 'PhotoShelf.HeifWorker.exe') -PathType Leaf)) { throw 'Build bundled HEIF codecs first.' }
New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'artifacts/heif-codec') -Force | Out-Null
Copy-Item -LiteralPath $codecSource -Destination (Join-Path $sourceRoot 'artifacts/heif-codec/win-x64') -Recurse
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $publicKey = $rsa.ExportSubjectPublicKeyInfoPem()
    $keyPath = Join-Path $sourceRoot 'src/PhotoShelf.Application/Updates/TrustedUpdateKey.pem'
    [IO.File]::WriteAllText($keyPath, $publicKey, [Text.UTF8Encoding]::new($false))
    function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
    }
    foreach ($testVersion in @('1.11.1', '1.11.2')) {
        $destination = Join-Path $runRoot ('app-' + $testVersion)
        $helperDestination = Join-Path $runRoot ('helper-' + $testVersion)
        foreach ($project in @('Desktop', 'Updater')) {
            $output = if ($project -eq 'Desktop') { $destination } else { $helperDestination }
            Invoke-Checked $DotNet @('publish', (Join-Path $sourceRoot "src/PhotoShelf.$project/PhotoShelf.$project.csproj"),
                '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-m:1', '-nr:false',
                '-p:UseSharedCompilation=false', '-p:PublishSingleFile=true', '-p:PhotoShelfPublish=true', '-p:RestoreLockedMode=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None',
                ('-p:Version=' + $testVersion + '-ultra'), '-o', $output)
        }
        Copy-Item -LiteralPath (Join-Path $helperDestination 'PhotoShelf.Updater.exe') -Destination (Join-Path $destination 'PhotoShelf.Updater.exe')
        Invoke-Checked $Python @((Join-Path $repoRoot 'tools/package_checks.py'), 'create', $destination,
            '--version', ($testVersion + '-ultra'), '--commit', $commit)
    }
    $newPackage = Join-Path $runRoot 'app-1.11.2'
    $zip = Join-Path $runRoot 'PhotoShelf-v1.11.2-ultra-win-x64.zip'
    Compress-Archive -Path (Join-Path $newPackage '*') -DestinationPath $zip -CompressionLevel Optimal
    $unpacked = [long]((Get-ChildItem -LiteralPath $newPackage -File -Recurse | Measure-Object -Property Length -Sum).Sum)
    $manifest = [ordered]@{
        protocolVersion = 1; version = '1.11.2'; runtime = 'win-x64'
        packageUrl = 'https://github.com/BigGluk23/PhotoShelf/releases/download/v1.11.2-ultra/PhotoShelf-v1.11.2-ultra-win-x64.zip'
        packageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $newPackage 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        packageBytes = (Get-Item -LiteralPath $zip).Length; unpackedBytes = $unpacked
        minCatalogSchema = 5; maxCatalogSchema = 5
        releaseNotesUrl = 'https://github.com/BigGluk23/PhotoShelf/releases/tag/v1.11.2-ultra'
    }
    $manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5 -Compress))
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'photoshelf-update.json'), $manifestBytes)
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'photoshelf-update.sig'),
        $rsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
    # No private key is exported, printed, stored, or uploaded. Test packages never enter release artifacts.
}
finally { $rsa.Dispose() }
$driverProject = Join-Path $repoRoot 'tools/PhotoShelf.UpdateDesktopDriver/PhotoShelf.UpdateDesktopDriver.csproj'
Invoke-Checked $DotNet @('build', $driverProject, '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:RestoreLockedMode=true')
$driver = Join-Path $repoRoot 'tools/PhotoShelf.UpdateDesktopDriver/bin/Release/net10.0-windows/PhotoShelf.UpdateDesktopDriver.dll'
$driverStart = [Diagnostics.ProcessStartInfo]::new($DotNet)
$driverStart.UseShellExecute = $false
$driverStart.CreateNoWindow = $true
foreach ($argument in @($driver, $runRoot, $ReportDirectory, $commit, $version)) { $driverStart.ArgumentList.Add($argument) }
$driverProcess = [Diagnostics.Process]::Start($driverStart)
try {
    if (-not $driverProcess.WaitForExit(600000)) {
        # UI Automation includes synchronous cross-process calls; its own polling deadlines cannot
        # bound a stuck COM call. Stop only our driver handle and retain the synthetic profile/evidence.
        $driverProcess.Kill()
        $driverProcess.WaitForExit(10000) | Out-Null
        throw 'The real WPF updater lifecycle exceeded its 10-minute external watchdog. Fixtures were retained.'
    }
    if ($driverProcess.ExitCode -ne 0) { throw "WPF updater lifecycle driver failed with exit code $($driverProcess.ExitCode)." }
}
finally { $driverProcess.Dispose() }
$proof = Get-Content -LiteralPath (Join-Path $ReportDirectory 'updater-desktop-e2e.json') -Raw | ConvertFrom-Json
if ($proof.schema -ne 1 -or $proof.status -ne 'passed' -or $proof.commit -ne $commit -or $proof.version -ne $version -or
    $proof.scope -ne 'production-desktop-update-lifecycle-with-test-trust' -or -not $proof.sourcePublicKeySubstituted -or
    -not $proof.sourceFaultCheckpointsInserted) {
    throw 'WPF lifecycle report identity or outcome does not match this run.'
}
Write-Output ('Real WPF updater lifecycle evidence: ' + (Join-Path $ReportDirectory 'updater-desktop-e2e.json'))
