param([Parameter(Mandatory = $true)][string]$ExePath)
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
$report = Join-Path $fixture 'startup-report.json'
$process = Start-Process -FilePath $isolatedExe -ArgumentList @('--verify-startup-resources', '--startup-report', ('"' + $report + '"')) -WorkingDirectory $work -PassThru
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw "Startup check timed out. Fixture retained: $fixture"
}
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
    throw "Startup check failed. Fixture: $fixture. Full errors: %APPDATA%\PhotoShelf\diagnostics\errors"
}
$data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($data.status -ne 'passed' -or $data.check -ne 'startup-resources' -or $data.resources.Count -ne 2) {
    throw "Incomplete startup report: $report"
}
Write-Output "Passed: standalone EXE loaded its embedded icon, PNG, tray icon, theme and startup window without an Assets folder. Report: $report"
Write-Output 'This mode does not open the catalog or scan/move user files. Other Windows UI scenarios still need testing.'
