[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultsDirectory,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$DotNet = 'dotnet',
    [string]$OldFixtureVersion = '1.10.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Updater process lifecycle proof requires Windows; a cross-build is not a passing result.'
}
if ($Version -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)-ultra$') { throw 'Expected canonical release version with -ultra suffix.' }
$newFixtureVersion = $Matches[1]
if ([version]$newFixtureVersion -le [version]$OldFixtureVersion) { throw 'The compiled candidate version must be newer than the fixture parent.' }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'An exact source commit is required.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'tests/PhotoShelf.UpdateLifecycleHarness/PhotoShelf.UpdateLifecycleHarness.csproj'
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$report = Join-Path $results 'updater-e2e.json'
if (Test-Path -LiteralPath $report) { throw 'Refusing to replace an existing lifecycle report.' }
# Large fixture binaries and synthetic catalogs must not accidentally enter the published package or test report artifact.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('photoshelf-updater-fixtures-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$oldOutput = Join-Path $fixtureRoot 'old'
$newOutput = Join-Path $fixtureRoot 'new'
foreach ($item in @(@{ version = $OldFixtureVersion; output = $oldOutput }, @{ version = $newFixtureVersion; output = $newOutput })) {
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -m:1 -nr:false '-p:UseSharedCompilation=false' `
        '-p:PublishSingleFile=true' '-p:PhotoShelfPublish=true' '-p:RestoreLockedMode=true' `
        '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' `
        ('-p:Version=' + $item.version + '-ultra') -o $item.output
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the versioned Windows lifecycle fixture.' }
    $exe = Join-Path $item.output 'PhotoShelf.UpdateLifecycleHarness.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The compiled lifecycle fixture executable is missing.' }
    if (@(Get-ChildItem -LiteralPath $item.output -File | Where-Object { $_.Name -ne 'PhotoShelf.UpdateLifecycleHarness.exe' }).Count -ne 0) {
        throw 'The fixture must be an actual self-contained single file before it can be packaged.'
    }
}
$oldExe = Join-Path $oldOutput 'PhotoShelf.UpdateLifecycleHarness.exe'
$newExe = Join-Path $newOutput 'PhotoShelf.UpdateLifecycleHarness.exe'
$start = [Diagnostics.ProcessStartInfo]::new($oldExe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('--run', $oldExe, $newExe, $report, $Commit, $Version)) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(600000)) {
        # Only the exact runner process started by this script. Fixture child fault gates also have their own time limits.
        $process.Kill()
        throw 'The updater lifecycle runner exceeded its 10-minute watchdog.'
    }
    if ($process.ExitCode -ne 0) { throw "The updater lifecycle runner failed with exit code $($process.ExitCode)." }
} finally { $process.Dispose() }
if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw 'The lifecycle runner did not produce evidence.' }
$proof = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$required = @('explicit-consent-and-parent-drain', 'verified-two-version-install-and-health', 'old-shortcut-launches-active-version',
    'used-request-replay-rejected', 'downgrade-rejected', 'helper-kill-before-activation', 'helper-kill-after-activation')
if ($proof.schema -ne 1 -or $proof.scope -ne 'production-updater-process-lifecycle-with-fixture-applications' -or
    $proof.status -ne 'passed' -or $proof.platform -ne 'windows' -or $proof.commit -ne $Commit -or $proof.version -ne $Version -or
    -not $proof.originalHashesPreserved -or -not $proof.sqliteIntegrityPassed) { throw 'Lifecycle evidence identity or safety assertions failed.' }
if (@($proof.scenarios).Count -ne $required.Count) { throw 'Lifecycle scenario evidence is incomplete.' }
foreach ($name in $required) {
    $entries = @($proof.scenarios | Where-Object { $_.name -eq $name -and $_.status -eq 'passed' })
    if ($entries.Count -ne 1) { throw "Required lifecycle scenario did not pass exactly once: $name" }
}
Write-Output ('Updater process lifecycle evidence: ' + $report)
