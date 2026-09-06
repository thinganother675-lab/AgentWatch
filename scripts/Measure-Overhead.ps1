# Requires PowerShell 7. Uses native read-only counters for only the process this script creates.
[CmdletBinding()]
param(
    [ValidateRange(120,3600)][int]$DurationSeconds = 720,
    [ValidateRange(30,300)][int]$WarmupSeconds = 60,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\performance')
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside the AgentWatch workspace.' }
if (Test-Path -LiteralPath (Join-Path $outputPath 'agentwatch.db')) { throw 'Choose a fresh output directory; this measurement must not merge earlier runs.' }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$binary = Join-Path $taskRoot 'artifacts\publish\AgentWatch.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AgentWatchMeasurementNative {
  [StructLayout(LayoutKind.Sequential)] public struct Io {
    public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
  }
  [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetProcessIoCounters(IntPtr handle, out Io io);
}
'@
$start = [Diagnostics.ProcessStartInfo]::new($binary)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in @('run','--duration',($DurationSeconds.ToString()+'s'),'--data-dir',$outputPath)) { $start.ArgumentList.Add($argument) }
# Self-contained launch must work independently of the local SDK/runtime configuration.
[void]$start.Environment.Remove('DOTNET_ROOT')
[void]$start.Environment.Remove('DOTNET_ROOT_X64')
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $outputPath 'bundle-cache'
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
$clock = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
try {
  while (-not $process.WaitForExit(5000)) {
    $process.Refresh()
    $io = [AgentWatchMeasurementNative+Io]::new()
    if (-not [AgentWatchMeasurementNative]::GetProcessIoCounters($process.Handle, [ref]$io)) { throw 'Native process I/O query failed.' }
    $db = Get-Item -LiteralPath (Join-Path $outputPath 'agentwatch.db') -ErrorAction SilentlyContinue
    $wal = Get-Item -LiteralPath (Join-Path $outputPath 'agentwatch.db-wal') -ErrorAction SilentlyContinue
    $samples.Add([pscustomobject]@{
      ElapsedSeconds=$clock.Elapsed.TotalSeconds; TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O')
      CpuSeconds=$process.TotalProcessorTime.TotalSeconds; WorkingSetBytes=$process.WorkingSet64; PrivateBytes=$process.PrivateMemorySize64
      LogicalReadBytes=$io.ReadBytes; LogicalWriteBytes=$io.WriteBytes; LogicalWriteOperations=$io.WriteOperations
      DatabaseBytes=if($db){$db.Length}else{0}; WalBytes=if($wal){$wal.Length}else{0}
    })
    if ($clock.Elapsed.TotalSeconds -gt $DurationSeconds + 45) { throw 'Owned validation process exceeded its bounded duration.' }
  }
  $exitIo = [AgentWatchMeasurementNative+Io]::new()
  $exitIoAvailable = [AgentWatchMeasurementNative]::GetProcessIoCounters($process.Handle, [ref]$exitIo)
  $exitSeconds = $clock.Elapsed.TotalSeconds
  $samples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'external-samples.json') -Encoding utf8
  $steady = @($samples | Where-Object ElapsedSeconds -GE $WarmupSeconds)
  if ($steady.Count -lt 2) { throw 'Not enough steady observations.' }
  $first = $steady[0]; $last = $steady[-1]; $seconds = $last.ElapsedSeconds - $first.ElapsedSeconds
  $summary = [ordered]@{
    TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O'); Executable=$binary; Sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    ProcessId=$process.Id; ExitCode=$process.ExitCode; LogicalProcessors=[Environment]::ProcessorCount
    RequestedSeconds=$DurationSeconds; ExcludedWarmupSeconds=$WarmupSeconds; MeasuredSteadySeconds=$seconds
    CpuPercent=100*($last.CpuSeconds-$first.CpuSeconds)/$seconds/[Environment]::ProcessorCount
    WorkingSetAverageBytes=($steady.WorkingSetBytes | Measure-Object -Average).Average; WorkingSetPeakBytes=($steady.WorkingSetBytes | Measure-Object -Maximum).Maximum
    PrivateAverageBytes=($steady.PrivateBytes | Measure-Object -Average).Average; PrivatePeakBytes=($steady.PrivateBytes | Measure-Object -Maximum).Maximum
    LogicalReadDeltaBytes=$last.LogicalReadBytes-$first.LogicalReadBytes; LogicalWriteDeltaBytes=$last.LogicalWriteBytes-$first.LogicalWriteBytes
    ProjectedLogicalWriteBytesPerDay=($last.LogicalWriteBytes-$first.LogicalWriteBytes)*86400.0/$seconds
    FirstObservedCumulativeLogicalWriteBytes=$samples[0].LogicalWriteBytes
    ShutdownTailLogicalWriteBytes=if($exitIoAvailable){$exitIo.WriteBytes-$last.LogicalWriteBytes}else{$null}
    PostWarmupThroughExitLogicalWriteBytes=if($exitIoAvailable){$exitIo.WriteBytes-$first.LogicalWriteBytes}else{$null}
    PostWarmupThroughExitSeconds=$exitSeconds-$first.ElapsedSeconds
    FinalDatabaseBytes=(Get-Item -LiteralPath (Join-Path $outputPath 'agentwatch.db')).Length
    MaximumWalBytes=($samples.WalBytes | Measure-Object -Maximum).Maximum; MaximumDatabaseBytes=($samples.DatabaseBytes | Measure-Object -Maximum).Maximum
    Caveat='Short steady-window extrapolation, not measured daily NVMe writes. Natural machine workload; no stress generator. Final shutdown flush is outside steady counters.'
  }
  $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'measurement.json') -Encoding utf8
  $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $outputPath 'run.stdout.txt') -Encoding utf8
  $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $outputPath 'run.stderr.txt') -Encoding utf8
  & $binary report --since 24h --data-dir $outputPath --json | Set-Content -LiteralPath (Join-Path $outputPath 'report.json') -Encoding utf8
  if ($LASTEXITCODE -ne 0 -or $process.ExitCode -ne 0) { throw 'Validation runtime or report failed.' }
  $summary | ConvertTo-Json -Depth 4
} finally {
  if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
  $process.Dispose()
}
