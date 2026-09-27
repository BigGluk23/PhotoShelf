param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This smoke test requires Windows; cross-compilation is not a runtime test.'
}
$source = (Resolve-Path -LiteralPath $ExePath).Path
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($source)
if ($info.ProductName -ne 'PhotoShelf Ultra' -or [Version]$info.FileVersion -lt [Version]'0.10.1.0') {
    throw 'Use PhotoShelf Ultra 0.10.1 or newer: older builds do not implement the safe test switch.'
}
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('photoshelf-startup-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$isolatedExe = Join-Path $fixture 'PhotoShelf.exe'
Copy-Item -LiteralPath $source -Destination $isolatedExe
$work = Join-Path $fixture 'empty-working-directory'
New-Item -ItemType Directory -Path $work | Out-Null
$reportRoot = if ($ReportDirectory) { (Resolve-Path -LiteralPath $ReportDirectory).Path } else { $fixture }
$report = Join-Path $reportRoot 'startup-report.json'
if (Test-Path -LiteralPath $report) { throw "Report already exists and will not be overwritten: $report" }
$process = Start-Process -FilePath $isolatedExe -ArgumentList @('--verify-startup-resources', '--startup-report', ('"' + $report + '"')) -WorkingDirectory $work -PassThru
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw "Startup check timed out. Fixture retained: $fixture"
}
$data = $null
if (Test-Path -LiteralPath $report) {
    $data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    # Copy only diagnostics produced in this check's own temporary catalog, never profile logs.
    if ($data.PSObject.Properties['catalogRoot'] -and $data.PSObject.Properties['logRoot'] -and $data.catalogRoot -and $data.logRoot) {
        $catalogRoot = [System.IO.Path]::GetFullPath([string]$data.catalogRoot).TrimEnd('\', '/')
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $expectedLogs = Join-Path $catalogRoot 'diagnostics/errors'
        if (-not $catalogRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([System.IO.Path]::GetFileName($catalogRoot)).StartsWith('PhotoShelf-ui-smoke-', [StringComparison]::Ordinal) -or
            -not ([System.IO.Path]::GetFullPath([string]$data.logRoot)).Equals([System.IO.Path]::GetFullPath($expectedLogs), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Invalid isolated diagnostic paths in $report"
        }
        if (Test-Path -LiteralPath $expectedLogs) {
            $savedLogs = Join-Path $reportRoot 'startup-errors'
            New-Item -ItemType Directory -Force -Path $savedLogs | Out-Null
            Get-ChildItem -LiteralPath $expectedLogs -Filter 'error-*.log' -File | Copy-Item -Destination $savedLogs
        }
    }
}
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
    throw "Startup check failed. Fixture: $fixture. Report: $report. Current builds retain errors in their isolated TEMP catalog (logRoot in report)."
}
if ($data.status -ne 'passed' -or $data.check -ne 'startup-resources' -or $data.resources.Count -ne 2) {
    throw "Incomplete startup report: $report"
}
Write-Output "Passed: standalone EXE loaded its embedded icon, PNG, tray icon, theme and startup window without an Assets folder. Report: $report"
Write-Output 'This mode does not open the catalog or scan/move user files. Other Windows UI scenarios still need testing.'
