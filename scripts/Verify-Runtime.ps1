[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\runtime-validation'))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$data = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $data.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation must stay within this repository.' }
if (Test-Path -LiteralPath $data) { throw 'Choose a fresh validation directory.' }
[IO.Directory]::CreateDirectory($data) | Out-Null
$binary = Join-Path $taskRoot 'artifacts\publish\AgentWatch.exe'
function Start-OwnedRun([int]$Seconds) {
    $info = [Diagnostics.ProcessStartInfo]::new($binary)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    foreach ($argument in @('run','--duration',($Seconds.ToString()+'s'),'--data-dir',$data)) { $info.ArgumentList.Add($argument) }
    [void]$info.Environment.Remove('DOTNET_ROOT'); [void]$info.Environment.Remove('DOTNET_ROOT_X64')
    return [Diagnostics.Process]::Start($info)
}
$first = Start-OwnedRun 90
try {
    if ($first.WaitForExit(15000)) { throw "Initial owned process exited early: $($first.ExitCode)" }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'agentwatch.db'))) { throw 'Initial database was not created.' }
    # Deliberately crash only this returned process. It is an isolated validation instance.
    $first.Kill(); if (-not $first.WaitForExit(5000)) { throw 'Owned process did not terminate.' }
} finally { if (-not $first.HasExited) { $first.Kill(); $first.WaitForExit() }; $first.Dispose() }
$second = Start-OwnedRun 25
try {
    if (-not $second.WaitForExit(55000)) { throw 'Restarted owned process exceeded its bounded run.' }
    if ($second.ExitCode -ne 0) { throw "Restart failed: $($second.ExitCode)" }
} finally { if (-not $second.HasExited) { $second.Kill(); $second.WaitForExit() }; $second.Dispose() }
$checks = [Collections.Generic.List[object]]::new()
foreach ($command in @('report','agents','disk','anomalies','self')) {
    $json = & $binary $command --since 7d --data-dir $data --json
    if ($LASTEXITCODE -ne 0) { throw "$command JSON failed." }
    $result = $json | ConvertFrom-Json
    if ($result.schemaVersion -ne 1) { throw "$command returned an unexpected schema." }
    $json | Set-Content -LiteralPath (Join-Path $data ($command + '.json')) -Encoding utf8
    $checks.Add([pscustomobject]@{ Command=$command; JsonSchemaVersion=$result.schemaVersion; ExitCode=0 })
}
$report = Get-Content -LiteralPath (Join-Path $data 'report.json') -Raw | ConvertFrom-Json
if (-not ($report.anomalies | Where-Object type -EQ 'service-restart-gap')) { throw 'Restart did not record the gap.' }
if ($report.period.observedSeconds -le 0 -or $report.period.observedSeconds -gt 35) { throw 'Restart coverage fabricated downtime/uncommitted samples.' }
foreach ($command in @('snapshot','status','doctor')) {
    $json = & $binary $command --data-dir $data --json
    if ($LASTEXITCODE -ne 0) { throw "$command failed." }
    $result = $json | ConvertFrom-Json
    $json | Set-Content -LiteralPath (Join-Path $data ($command + '.json')) -Encoding utf8
    $checks.Add([pscustomobject]@{ Command=$command; ExitCode=0 })
}
& $binary report --since 24h --data-dir $data | Set-Content -LiteralPath (Join-Path $data 'report.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Human report failed.' }
& $binary report --since 30d --data-dir $data --json | Set-Content -LiteralPath (Join-Path $data 'report-30d.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw '30-day query failed.' }
$corrupt = Join-Path $data 'corrupt-startup'
[IO.Directory]::CreateDirectory($corrupt) | Out-Null
$badDatabase = Join-Path $corrupt 'agentwatch.db'
[IO.File]::WriteAllBytes($badDatabase, [byte[]](1..128))
$beforeHash = (Get-FileHash -LiteralPath $badDatabase -Algorithm SHA256).Hash
& $binary run --duration 5s --data-dir $corrupt | Set-Content -LiteralPath (Join-Path $data 'corrupt-startup.txt') -Encoding utf8
if ($LASTEXITCODE -ne 1) { throw 'Fatal foreground startup did not return exit code 1.' }
if ((Get-FileHash -LiteralPath $badDatabase -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Corrupt input database was modified.' }
$summary = [ordered]@{ TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); Sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash;
    ControlledCrashAndRestart='passed'; CorruptStartupExitCode=1; CorruptDatabasePreserved=$true; ObservedSecondsAfterRestart=$report.period.observedSeconds; Commands=$checks;
    ScmServiceLifecycle='not exercised: this test uses only isolated foreground instances' }
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $data 'validation.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
