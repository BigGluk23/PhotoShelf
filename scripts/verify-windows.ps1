param([switch]$Scale)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This gate requires Windows: native file handles and WPF must actually run.'
}
$results = Join-Path (Get-Location) ('TestResults/windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $results | Out-Null
& dotnet build PhotoShelf.sln -c Release -m:1
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$filter = if ($Scale) { '' } else { 'Category!=CatalogScale' }
$arguments = @('test', 'PhotoShelf.sln', '-c', 'Release', '--no-build', '--no-restore', '--results-directory', $results, '--logger', 'trx')
if ($filter) { $arguments += @('--filter', $filter) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
$notExecuted = 0
$executed = 0
Get-ChildItem $results -Filter '*.trx' | ForEach-Object {
    [xml]$report = Get-Content $_.FullName
    $counts = $report.TestRun.ResultSummary.Counters
    # Some xUnit TRX adapters report notExecuted=0 even when total > executed.
    $notExecuted += [Math]::Max([int]$counts.notExecuted, [int]$counts.total - [int]$counts.executed)
    $executed += [int]$counts.executed
}
if ($notExecuted -gt 0) { throw "Windows gate has $notExecuted skipped tests; inspect TRX before release." }
if ($executed -eq 0) { throw 'No executed tests were reported.' }
Write-Output "Automated Windows gate passed. Results: $results"
Write-Output 'The manual Windows scenarios in docs/v0.10.0-validation.md are still required.'
