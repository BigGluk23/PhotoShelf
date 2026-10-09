[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportDirectory, [string]$DotNet = 'dotnet', [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (-not [OperatingSystem]::IsWindows()) { throw 'Acceptance requires a real Windows WPF host.' }
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify acceptance source.' }
if (@(& git -C $repo status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0) {
    throw 'Acceptance requires committed source; no personal checkout edits are copied.'
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('PhotoShelf-acceptance-' + [Guid]::NewGuid().ToString('N'))
$process = $null
# Use the same pinned RID/single-file dependency graph as the shipping harness.
$publishProfile = @('-p:PhotoShelfPublish=true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:RestoreLockedMode=true')
try {
    & $Python (Join-Path $repo 'tools/prepare_windows_acceptance.py') --source $repo --work $work
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance source isolation failed.' }
    $project = Join-Path $work 'source/src/PhotoShelf.Desktop/PhotoShelf.Desktop.csproj'
    $app = Join-Path $work 'app'
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -m:1 -nr:false `
        -p:UseSharedCompilation=false -p:TreatWarningsAsErrors=true "-p:SourceRevisionId=$commit" @publishProfile -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance test-copy build failed.' }
    $executable = Join-Path $app 'PhotoShelf.exe'
    $process = Start-Process -FilePath $executable -PassThru
    if (-not $process.WaitForExit(600000)) { throw 'Acceptance parent watchdog expired.' }
    if ($process.ExitCode -ne 0) { throw 'Real Windows acceptance did not pass.' }
    & $Python (Join-Path $repo 'tools/windows_acceptance_checks.py') (Join-Path $work 'acceptance.json') --commit $commit
    if ($LASTEXITCODE -ne 0) { throw 'Incomplete/failed acceptance evidence.' }
}
finally {
    # Only binaries created under this unique owned output may be terminated.
    # No user processes, media, catalog, source copy or journal are deleted.
    if ($process -and -not $process.HasExited) {
        foreach ($candidate in [Diagnostics.Process]::GetProcesses()) {
            try {
                if ($candidate.MainModule.FileName.StartsWith((Join-Path $work 'app') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    $candidate.Kill()
                }
            } catch [System.ComponentModel.Win32Exception] { } catch [InvalidOperationException] { }
            finally { $candidate.Dispose() }
        }
    }
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    # Explicit report allowlist: never collect the owned database, media, source or binaries.
    foreach ($name in @('acceptance.json', 'first.json', 'restart.json', 'changed.json', 'failure.json')) {
        $source = Join-Path $work $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $target = Join-Path $ReportDirectory ('windows-' + $name)
            if (Test-Path -LiteralPath $target) { throw 'Acceptance report already exists; refusing overwrite.' }
            Copy-Item -LiteralPath $source -Destination $target
        }
    }
    Write-Output 'Owned synthetic workspace retained; only allowlisted JSON reports enter CI artifacts.'
}
