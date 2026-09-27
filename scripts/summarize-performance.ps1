param([string]$Path = "$env:APPDATA\PhotoShelf\diagnostics\performance.csv")
$ErrorActionPreference = 'Stop'
$rows = Get-Content -LiteralPath $Path | ConvertFrom-Csv -Header TimeUtc,Metric,Milliseconds,Count
$rows | Group-Object Metric | ForEach-Object {
    $values = @($_.Group | ForEach-Object { [double]::Parse($_.Milliseconds, [cultureinfo]::InvariantCulture) } | Sort-Object)
    [pscustomobject]@{
        Metric = $_.Name
        Samples = $values.Count
        P95Milliseconds = $values[[Math]::Max(0, [Math]::Ceiling($values.Count * 0.95) - 1)]
        MaxMilliseconds = $values[-1]
        LastCount = $_.Group[-1].Count
    }
} | Format-Table -AutoSize
