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
    Invoke-Checked $Python @('tools/harness_checks.py', 'repository')
    $result.commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Git revision.' }
    $result.version = (& $Python tools/harness_checks.py version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify application version.' }
    Invoke-Checked $DotNet @('--info')
    Invoke-Checked $DotNet @('build', 'PhotoShelf.sln', '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
    foreach ($project in @('Domain', 'Application', 'Infrastructure.Sqlite', 'Desktop')) {
        $arguments = @('test', "tests/PhotoShelf.$project.Tests/PhotoShelf.$project.Tests.csproj", '-c', 'Release', '--no-build', '--no-restore',
            '-m:1', '-nr:false', '--results-directory', $results, '--logger', "trx;LogFileName=$project.trx")
        if (-not $Scale) { $arguments += @('--filter', 'Category!=CatalogScale') }
        Invoke-Checked $DotNet $arguments
    }
    # Every project must produce a nonempty report; skipped native/WPF tests fail this gate.
    Invoke-Checked $Python @('tools/harness_checks.py', 'trx', $results, '--projects', 'Domain', 'Application', 'Infrastructure.Sqlite', 'Desktop',
        '--summary', (Join-Path $results 'test-summary.json'))

    $packageName = 'PhotoShelf-v' + $result.version + '-win-x64'
    $artifactRoot = Join-Path $repoRoot ('artifacts/windows-' + $runId)
    $publish = Join-Path $artifactRoot $packageName
    New-Item -ItemType Directory -Path $publish | Out-Null
    Invoke-Checked $DotNet @('publish', 'src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $publish)
    $exe = Join-Path $publish 'PhotoShelf.exe'
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
        $smoke.gracefulExit -ne $true -or $smoke.errorCount -ne 0 -or $smoke.elapsedReadySeconds -lt 5 -or
        $smoke.dispatcherTicks -lt 20 -or $smoke.maxDispatcherGapMs -gt 2000 -or $smoke.exitCode -ne 0 -or
        -not ($smoke.version -eq $result.version -or $smoke.version.StartsWith($result.version + '+', [StringComparison]::Ordinal))) {
        throw 'UI smoke did not confirm the published version, ready window, at least 5 seconds of responsiveness, zero errors, and graceful exit.'
    }
    Write-Output 'OK published EXE: embedded startup resources and full main window, isolated catalog, graceful close, no error logs.'
    $archive = Join-Path $artifactRoot ($packageName + '.zip')
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    ($hash + '  ' + [System.IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8
    $result.archive = $archive
    $result.archiveSha256 = $hash
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
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'harness-summary.json') -Encoding utf8
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
