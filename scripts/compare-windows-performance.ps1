[CmdletBinding()]
param(
    [string]$Baseline = 'cb6d716b66511c47a24ff6b1b8f10ac9ca84768a',
    [switch]$Million,
    [ValidateRange(5, 50)][int]$Repetitions = 15,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python',
    [switch]$ReportOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repoRoot
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This comparison needs a real Windows WPF desktop.'
}
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed: $LASTEXITCODE" }
}
$current = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify current source revision.' }
$baselineSha = (& git rev-parse ($Baseline + '^{commit}')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Baseline must exist locally; fetch the pinned revision before comparing.' }
$sourceChanges = @(& git status --porcelain)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -gt 0) { throw 'Source changes must be committed before the reproducible comparison.' }
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$resultRoot = Join-Path $repoRoot ('TestResults/performance-' + $runId)
$work = Join-Path $repoRoot ('artifacts/performance-' + $runId)
New-Item -ItemType Directory -Path $resultRoot, $work | Out-Null
Start-Transcript -Path (Join-Path $resultRoot 'performance.log') | Out-Null
try {
    $baselineSource = Join-Path $work 'baseline-source'
    New-Item -ItemType Directory -Path $baselineSource | Out-Null
    $tar = Join-Path $work 'baseline-source.tar'
    Invoke-Checked git @('archive', '--format=tar', ('--output=' + $tar), $baselineSha)
    Invoke-Checked tar @('-xf', $tar, '-C', $baselineSource)
    if ((Get-FileHash -LiteralPath (Join-Path $repoRoot 'global.json')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $baselineSource 'global.json')).Hash) {
        throw 'This comparison requires the pinned baseline/current SDK to match.'
    }
    Invoke-Checked $Python @('-B', (Join-Path $repoRoot 'tools/test_compare_performance.py'))
    $codec = Join-Path $repoRoot 'artifacts/heif-codec/win-x64'
    if (-not (Test-Path (Join-Path $codec 'PhotoShelf.HeifWorker.exe'))) { & (Join-Path $repoRoot 'tools/build-heif-codec.ps1') }
    # The catalog-only workload never invokes HEIF. Copy the same pinned package to satisfy both build contracts.
    $baselineCodecParent = Join-Path $baselineSource 'artifacts/heif-codec'
    New-Item -ItemType Directory -Path $baselineCodecParent -Force | Out-Null
    Copy-Item -LiteralPath $codec -Destination (Join-Path $baselineCodecParent 'win-x64') -Recurse
    & $DotNet --info | Set-Content -LiteralPath (Join-Path $resultRoot 'dotnet-info.txt') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record SDK information.' }
    Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'cpu.json') -Encoding utf8
    $executables = @{}
    foreach ($label in @('baseline', 'current')) {
        $source = if ($label -eq 'baseline') { $baselineSource } else { $repoRoot }
        $sourceRevision = if ($label -eq 'baseline') { $baselineSha } else { $current }
        $runner = Join-Path $work ($label + '-runner')
        New-Item -ItemType Directory -Path $runner | Out-Null
        Copy-Item -Path (Join-Path $repoRoot 'tools/PhotoShelf.PerformanceRunner/*') -Destination $runner -Exclude bin, obj -Recurse
        $project = Join-Path $runner 'PhotoShelf.PerformanceRunner.csproj'
        $output = Join-Path $runner 'out'
        Invoke-Checked $DotNet @('restore', $project, '--locked-mode', '--disable-parallel', '-m:1', '-nr:false', ('-p:PhotoShelfSourceRoot=' + $source))
        Invoke-Checked $DotNet @('build', $project, '-c', 'Release', '--no-restore', '-m:1', '-nr:false',
            '-p:UseSharedCompilation=false', ('-p:PhotoShelfSourceRoot=' + $source), ('-p:SourceRevisionId=' + $sourceRevision), '-o', $output)
        $executables[$label] = Join-Path $output 'PhotoShelf.PerformanceRunner.exe'
        if (-not (Test-Path -LiteralPath $executables[$label])) { throw "Missing runner: $label" }
    }
    $runnerHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'tools/PhotoShelf.PerformanceRunner/Program.cs') -Algorithm SHA256).Hash
    [ordered]@{ baseline = $baselineSha; current = $current; runnerSha256 = $runnerHash; million = $Million.IsPresent;
        repetitions = $Repetitions; order = @('baseline', 'current', 'current', 'baseline'); machine = $env:COMPUTERNAME } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'experiment.json') -Encoding utf8
    $counts = @(100000)
    if ($Million) { $counts += 1000000 }
    foreach ($count in $counts) {
        $reports = @()
        $index = 0
        foreach ($label in @('baseline', 'current', 'current', 'baseline')) {
            $index++
            $report = Join-Path $resultRoot ("$count-$index-$label.json")
            $revision = if ($label -eq 'baseline') { $baselineSha } else { $current }
            $start = [System.Diagnostics.ProcessStartInfo]::new($executables[$label])
            $start.UseShellExecute = $false
            $start.WorkingDirectory = $work
            foreach ($argument in @([string]$count, [string]$Repetitions, $report, $revision, $label)) { $start.ArgumentList.Add($argument) }
            $process = [System.Diagnostics.Process]::Start($start)
            try {
                if (-not $process.WaitForExit(1200000)) { $process.Kill($true); $process.WaitForExit(); throw "Benchmark watchdog: $count/$label exceeded 20 minutes." }
                if ($process.ExitCode -ne 0) { throw "Benchmark failed: $count/$label; inspect $report" }
            }
            finally { $process.Dispose() }
            $reports += $report
        }
        $comparisonJson = Join-Path $resultRoot ("comparison-$count.json")
        $comparisonMd = Join-Path $resultRoot ("comparison-$count.md")
        $arguments = @((Join-Path $repoRoot 'tools/compare_performance.py')) + $reports + @('--json', $comparisonJson, '--markdown', $comparisonMd)
        if (-not $ReportOnly) { $arguments += '--enforce' }
        & $Python @arguments
        $comparisonExit = $LASTEXITCODE
        if ($env:GITHUB_STEP_SUMMARY -and (Test-Path $comparisonMd)) { Get-Content -LiteralPath $comparisonMd | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
        if ($comparisonExit -ne 0) { throw 'Comparison failed or found a material repeatable regression; raw evidence is retained.' }
    }
    Write-Output "Verified comparison reports: $resultRoot"
}
finally { Stop-Transcript | Out-Null }
