[CmdletBinding()]
param(
    [switch]$Scale,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repoRoot
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The Windows gate requires Windows: cross-compilation does not verify WPF or native file safety.'
}
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$results = Join-Path $repoRoot ('TestResults/windows-' + $runId)
New-Item -ItemType Directory -Path $results | Out-Null
$transcriptStarted = $false
$smokeProcess = $null
$browseSmokeProcess = $null
$result = [ordered]@{ status = 'failed'; platform = 'windows'; commit = ''; version = ''; scaleIncluded = $Scale.IsPresent; results = $results }

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}

try {
    Start-Transcript -Path (Join-Path $results 'harness.log') | Out-Null
    $transcriptStarted = $true
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Invoke-Checked $Python @('-B', 'tools/test_harness_checks.py')
    Invoke-Checked $Python @('-B', 'tools/test_package_checks.py')
    Invoke-Checked $Python @('tools/harness_checks.py', 'repository')
    $result.commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Git revision.' }
    $result.version = (& $Python tools/harness_checks.py version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify application version.' }
    Invoke-Checked $DotNet @('--info')
    & (Join-Path $repoRoot 'tools/build-heif-codec.ps1')
    # Lockfiles pin direct/transitive package hashes; NuGet audit is required and warnings fail restore.
    Invoke-Checked $DotNet @('restore', 'PhotoShelf.sln', '--locked-mode', '--force', '--disable-parallel')
    & $DotNet list PhotoShelf.sln package --include-transitive --no-restore --format json | Set-Content -LiteralPath (Join-Path $results 'dependencies.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Could not record resolved dependency inventory.' }
    & $DotNet list PhotoShelf.sln package --vulnerable --include-transitive --no-restore --format json | Set-Content -LiteralPath (Join-Path $results 'dependency-audit.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Could not check dependency advisories.' }
    Invoke-Checked $DotNet @('build', 'PhotoShelf.sln', '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
    $hadTestFailure = $false
    foreach ($project in @('Domain', 'Application', 'Infrastructure.Sqlite', 'Desktop')) {
        $arguments = @('test', "tests/PhotoShelf.$project.Tests/PhotoShelf.$project.Tests.csproj", '-c', 'Release', '--no-build', '--no-restore',
            '-m:1', '-nr:false', '--results-directory', $results, '--logger', "trx;LogFileName=$project.trx")
        if (-not $Scale) { $arguments += @('--filter', 'Category!=CatalogScale') }
        if ($project -eq 'Desktop') {
            # Native malformed-image regressions run in a disposable testhost with a hard watchdog.
            $arguments += @('--blame-hang-timeout', '30s', '--blame-hang-dump-type', 'none')
        }
        # Collect every suite's result so one failure does not hide independent Windows regressions.
        & $DotNet @arguments
        if ($LASTEXITCODE -ne 0) { $hadTestFailure = $true }
    }
    # Every project must produce a nonempty report; skipped native/WPF tests fail this gate.
    Invoke-Checked $Python @('tools/harness_checks.py', 'trx', $results, '--projects', 'Domain', 'Application', 'Infrastructure.Sqlite', 'Desktop',
        '--summary', (Join-Path $results 'test-summary.json'))
    if ($hadTestFailure) { throw 'At least one dotnet test process failed; publishing is blocked.' }

    $packageName = 'PhotoShelf-v' + $result.version + '-win-x64'
    $artifactRoot = Join-Path $repoRoot ('artifacts/windows-' + $runId)
    $publish = Join-Path $artifactRoot $packageName
    New-Item -ItemType Directory -Path $publish | Out-Null
    Invoke-Checked $DotNet @('publish', 'src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=true',
        '-p:PhotoShelfPublish=true', '-p:RestoreLockedMode=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $publish)
    # Read the just-restored publish graph without replacing it with the ordinary build graph.
    & $DotNet list src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj package --include-transitive --no-restore --format json | Set-Content -LiteralPath (Join-Path $results 'publish-dependencies.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Could not record resolved publish dependency inventory.' }
    & $DotNet list src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj package --vulnerable --include-transitive --no-restore --format json | Set-Content -LiteralPath (Join-Path $results 'publish-dependency-audit.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Could not check publish dependency advisories.' }
    # Validate what the user downloads: generate the versioned instructions/inventory,
    # archive once, extract into a new directory, then run every EXE smoke from there.
    Invoke-Checked $Python @('tools/package_checks.py', 'create', $publish, '--version', $result.version, '--commit', $result.commit)
    $archive = Join-Path $artifactRoot ($packageName + '.zip')
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $extracted = Join-Path $artifactRoot ('extracted-' + $packageName)
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    Invoke-Checked $Python @('tools/package_checks.py', 'verify', $extracted, '--version', $result.version, '--commit', $result.commit,
        '--report', (Join-Path $results 'package-verification.json'))
    $exe = Join-Path $extracted 'PhotoShelf.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Publish did not produce PhotoShelf.exe.' }
    & (Join-Path $repoRoot 'scripts/test-startup-package.ps1') -ExePath $exe -ReportDirectory $results

    # This flag creates its own fresh temporary catalog and never reads the user's settings/library.
    $emptyWork = Join-Path $results 'empty-working-directory'
    New-Item -ItemType Directory -Path $emptyWork | Out-Null
    $smokeReport = Join-Path $results 'ui-smoke.json'
    $smokeProcess = Start-Process -FilePath $exe -ArgumentList @('--ui-smoke', '--smoke-report', ('"' + $smokeReport + '"')) -WorkingDirectory $emptyWork -PassThru
    if (-not $smokeProcess.WaitForExit(45000)) {
        $smokeProcess.Kill()
        $smokeProcess.WaitForExit()
        throw 'Isolated full UI smoke timed out after 45 seconds; process terminated and fixture retained.'
    }
    if (-not (Test-Path -LiteralPath $smokeReport -PathType Leaf)) { throw "UI smoke exited $($smokeProcess.ExitCode) without a report: $results" }
    $smoke = Get-Content -LiteralPath $smokeReport -Raw | ConvertFrom-Json
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $catalogRoot = [System.IO.Path]::GetFullPath([string]$smoke.catalogRoot).TrimEnd('\')
    $logRoot = [System.IO.Path]::GetFullPath([string]$smoke.logRoot)
    if (-not $catalogRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($catalogRoot).StartsWith('PhotoShelf-ui-smoke-', [StringComparison]::Ordinal) -or
        -not $logRoot.StartsWith($catalogRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Smoke report does not point to an isolated temporary catalog/log directory.'
    }
    if ((Test-Path -LiteralPath $logRoot) -and @(Get-ChildItem -LiteralPath $logRoot -File -Recurse).Count -gt 0) {
        # Only logs from the validated temporary catalog may be copied into a CI artifact.
        Copy-Item -LiteralPath $logRoot -Destination (Join-Path $results 'smoke-errors') -Recurse
        throw 'New error logs were written during the isolated UI smoke.'
    }
    if ($smokeProcess.ExitCode -ne 0 -or $smoke.status -ne 'passed' -or $smoke.check -ne 'ui-smoke' -or $smoke.ready -ne $true -or $smoke.previewRendered -ne $true -or
        $smoke.nativeDecoderVerified -ne $true -or $smoke.heifDecoderVerified -ne $true -or $smoke.heifInstallationVerified -ne $true -or
        $smoke.heifProtocol -ne 'PSH1' -or $smoke.libheifVersion -ne '1.23.5' -or $smoke.libde265Version -ne '1.1.3' -or
        $smoke.gracefulExit -ne $true -or $smoke.errorCount -ne 0 -or $smoke.elapsedReadySeconds -lt 5 -or
        $smoke.dispatcherTicks -lt 20 -or $smoke.maxDispatcherGapMs -gt 2000 -or $smoke.exitCode -ne 0 -or
        -not ($smoke.version -eq $result.version -or $smoke.version.StartsWith($result.version + '+', [StringComparison]::Ordinal))) {
        throw 'UI smoke did not confirm the published version, ready window, at least 5 seconds of responsiveness, zero errors, and graceful exit.'
    }
    Write-Output 'OK published EXE: embedded startup resources and full main window, isolated catalog, graceful close, no error logs.'
    # A separate process preserves catalog/cache isolation while explicitly enabling
    # the production watcher + metadata + browse path omitted by the basic smoke.
    $browseReport = Join-Path $results 'ui-browse-smoke.json'
    $browseSmokeProcess = Start-Process -FilePath $exe -ArgumentList @('--ui-browse-smoke', '--smoke-report', ('"' + $browseReport + '"')) -WorkingDirectory $emptyWork -PassThru
    if (-not $browseSmokeProcess.WaitForExit(90000)) {
        $browseSmokeProcess.Kill()
        $browseSmokeProcess.WaitForExit()
        throw 'Production browse/monitor smoke timed out after 90 seconds; isolated fixtures retained.'
    }
    if (-not (Test-Path -LiteralPath $browseReport -PathType Leaf)) { throw "Browse smoke exited $($browseSmokeProcess.ExitCode) without a report." }
    $browseSmoke = Get-Content -LiteralPath $browseReport -Raw | ConvertFrom-Json
    $browseCatalog = [System.IO.Path]::GetFullPath([string]$browseSmoke.catalogRoot).TrimEnd('\')
    $browseLogs = [System.IO.Path]::GetFullPath([string]$browseSmoke.logRoot)
    $browseMedia = [System.IO.Path]::GetFullPath([string]$browseSmoke.mediaRoot).TrimEnd('\')
    if (-not $browseCatalog.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($browseCatalog).StartsWith('PhotoShelf-ui-smoke-', [StringComparison]::Ordinal) -or
        -not $browseLogs.StartsWith($browseCatalog + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetDirectoryName($browseMedia) + '\').Equals($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($browseMedia).StartsWith('PhotoShelf-browse-smoke-', [StringComparison]::Ordinal) -or
        $browseMedia.StartsWith($browseCatalog + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Browse smoke did not use separate owned temporary catalog/media roots.'
    }
    if ((Test-Path -LiteralPath $browseLogs) -and @(Get-ChildItem -LiteralPath $browseLogs -File -Recurse).Count -gt 0) {
        Copy-Item -LiteralPath $browseLogs -Destination (Join-Path $results 'browse-smoke-errors') -Recurse
        throw 'New application errors were written during production browse/monitor smoke.'
    }
    if ($browseSmokeProcess.ExitCode -ne 0 -or $browseSmoke.status -ne 'passed' -or $browseSmoke.check -ne 'ui-browse-smoke' -or
        $browseSmoke.version -ne ($result.version + '+' + $result.commit) -or $browseSmoke.isolatedCatalog -ne $true -or
        $browseSmoke.monitoringObserved -ne $true -or $browseSmoke.directCountVerified -ne $true -or
        $browseSmoke.recursiveCountVerified -ne $true -or $browseSmoke.checkboxVerified -ne $true -or
        $browseSmoke.freshDiscoveryVerified -ne $true -or $browseSmoke.rapidSelectionVerified -ne $true -or
        $browseSmoke.wrongFolderPublications -ne 0 -or $browseSmoke.originalHashesVerified -ne $true -or
        $browseSmoke.originalsChecked -ne 5 -or $browseSmoke.decoderReadersDrained -ne $true -or
        $browseSmoke.catalogWritersDrained -ne $true -or $browseSmoke.gracefulExit -ne $true -or
        $browseSmoke.errorCount -ne 0 -or $browseSmoke.exitCode -ne 0) {
        throw 'Production browse/monitor smoke did not pass every functional/safety assertion.'
    }
    foreach ($measurement in @($browseSmoke.firstDecodedPreviewMs, $browseSmoke.firstDiscoveryMs)) {
        if ([double]::IsNaN([double]$measurement) -or [double]::IsInfinity([double]$measurement) -or $measurement -le 0 -or $measurement -gt 10000) {
            throw 'Browse smoke did not decode its existing/fresh direct thumbnail within 10 seconds of selection.'
        }
    }
    if (@($browseSmoke.stability).Count -ne 2) { throw 'Browse smoke is missing empty/nonempty stability observations.' }
    foreach ($observation in $browseSmoke.stability) {
        if ($observation.noiseDirectories -le 1024 -or $observation.noiseFiles -le 1024 -or
            $observation.rowPublications -ne 0 -or $observation.emptyTransitions -ne 0 -or $observation.monitorGenerationChanges -ne 0 -or
            $observation.quietWallMs -lt 3000 -or $observation.quietCpuMs -lt 0) {
            throw 'Filesystem noise reset/flashed the grid or monitoring failed to settle.'
        }
    }
    if ($browseSmoke.background.pauseVerified -ne $true -or $browseSmoke.background.pausePersisted -ne $true -or
        $browseSmoke.background.resumeVerified -ne $true -or $browseSmoke.background.idleVerified -ne $true) {
        throw 'Browse smoke did not verify persisted background pause, queued media discovery and idle after resume.'
    }
    $searchSort = $browseSmoke.searchSort
    if ($searchSort.passed -ne $true -or $searchSort.originalHashesVerified -ne $true -or
        $searchSort.modificationTimesVerified -ne $true -or $searchSort.rapidSearchVerified -ne $true -or
        $searchSort.decodedPngVerified -ne $true -or $searchSort.fixtureCount -ne 96 -or
        $searchSort.hashesChecked -ne 96 -or $searchSort.selectionChecks -ne 4 -or
        $searchSort.anchorChecks -ne 4 -or $searchSort.wrongSearchPublications -ne 0 -or @($searchSort.cases).Count -ne 7) {
        throw 'Search/sort smoke did not preserve its results, selection, anchor, decoded previews and all 96 originals.'
    }
    $expectedSearchCases = @{
        'all-desc' = @('', $true, 96)
        'all-asc' = @('', $false, 96)
        'all-desc-restored' = @('', $true, 96)
        'search-desc' = @('p01-alpha', $true, 48)
        'search-asc' = @('p01-alpha', $false, 48)
        'search-desc-restored' = @('p01-alpha', $true, 48)
        'rapid-final-search' = @('p01-alpha', $true, 48)
    }
    foreach ($case in $searchSort.cases) {
        if (-not $expectedSearchCases.ContainsKey([string]$case.scenario)) { throw 'Unexpected/repeated search/sort scenario.' }
        $expected = $expectedSearchCases[[string]$case.scenario]
        if ($case.search -ne $expected[0] -or $case.newestFirst -ne $expected[1] -or
            $case.expectedCount -ne $expected[2] -or $case.actualCount -ne $expected[2] -or
            $case.fullOrderVerified -ne $true -or $case.equalDateTiesVerified -ne $true -or
            $case.decodedVisiblePng -ne $true -or $case.monitoringEnabled -ne $true) {
            throw 'Search/sort smoke changed result order/count or failed to decode with monitoring enabled.'
        }
        $expectedSearchCases.Remove([string]$case.scenario)
    }
    if ($expectedSearchCases.Count -ne 0) { throw 'Missing search/sort smoke scenarios.' }
    $result.browseSmokeVerified = $true
    $result.searchSortSmokeVerified = $true
    Write-Output 'OK production browse/search/sort smoke: isolated monitoring, decoded PNG, full order/count, selection/anchor, cancellation, stable views, unchanged originals, graceful close.'
    # Smoke must not mutate the installation, and the published ZIP must be the exact
    # archive whose extracted bytes were tested. Catalog/log writes stay in owned TEMP.
    Invoke-Checked $Python @('tools/package_checks.py', 'verify', $extracted, '--version', $result.version, '--commit', $result.commit)
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Release ZIP changed after extraction.' }
    ($hash + '  ' + [System.IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8
    $result.archive = $archive
    $result.archiveSha256 = $hash
    $result.extractedPackageVerified = $true
    Invoke-Checked $Python @('tools/harness_checks.py', 'repository')
    $result.status = 'passed'
    if (-not $Scale) { Write-Output 'NOT RUN: CatalogScale (opt in with -Scale).' }
    Write-Output 'Manual Windows UX, removable drives and power-loss testing remain separate from this automated gate.'
    Write-Output "OK Windows harness. Results: $results. Package: $archive"
    if ($env:GITHUB_STEP_SUMMARY) {
        @("### PhotoShelf Windows harness", '', "- Revision: ``$($result.commit)``", "- Version: ``$($result.version)``", '- Build, tests, publish, embedded resources and isolated full UI smoke: passed',
            "- Scale tests included: $($Scale.IsPresent)", '- Manual UX and hardware failure scenarios: not covered') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}
catch {
    $result.error = $_.Exception.ToString()
    Write-Host "FAILED Windows harness: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $smokeProcess -and -not $smokeProcess.HasExited) { $smokeProcess.Kill() }
    if ($null -ne $browseSmokeProcess -and -not $browseSmokeProcess.HasExited) { $browseSmokeProcess.Kill() }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'harness-summary.json') -Encoding utf8
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
