param(
    [Parameter(Mandatory = $true)][string]$ReportDirectory,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
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
    foreach ($testVersion in @('1.11.0', '1.11.1')) {
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
    $newPackage = Join-Path $runRoot 'app-1.11.1'
    $zip = Join-Path $runRoot 'PhotoShelf-v1.11.1-ultra-win-x64.zip'
    Compress-Archive -Path (Join-Path $newPackage '*') -DestinationPath $zip -CompressionLevel Optimal
    $unpacked = [long]((Get-ChildItem -LiteralPath $newPackage -File -Recurse | Measure-Object -Property Length -Sum).Sum)
    $manifest = [ordered]@{
        protocolVersion = 1; version = '1.11.1'; runtime = 'win-x64'
        packageUrl = 'https://github.com/BigGluk23/PhotoShelf/releases/download/v1.11.1-ultra/PhotoShelf-v1.11.1-ultra-win-x64.zip'
        packageSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $newPackage 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        packageBytes = (Get-Item -LiteralPath $zip).Length; unpackedBytes = $unpacked
        minCatalogSchema = 5; maxCatalogSchema = 5
        releaseNotesUrl = 'https://github.com/BigGluk23/PhotoShelf/releases/tag/v1.11.1-ultra'
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
    $proof.scope -ne 'production-desktop-update-lifecycle-with-test-trust' -or -not $proof.sourcePublicKeySubstituted) {
    throw 'WPF lifecycle report identity or outcome does not match this run.'
}
Write-Output ('Real WPF updater lifecycle evidence: ' + (Join-Path $ReportDirectory 'updater-desktop-e2e.json'))
