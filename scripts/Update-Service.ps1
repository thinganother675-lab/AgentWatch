# Run explicitly from elevated PowerShell only when ready to restart the monitor.
[CmdletBinding(SupportsShouldProcess)]
param([string]$Executable, [switch]$Plan)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) {
    $manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts\latest-build.json') -Raw | ConvertFrom-Json
    $Executable = $manifest.File
}
$source = [IO.Path]::GetFullPath($Executable)
$installed = Join-Path $env:ProgramFiles 'AgentWatch\AgentWatch.exe'
$data = Join-Path $env:ProgramData 'AgentWatch'
function Assert-Unlinked([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked path refused: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
foreach ($path in @($source,$installed,$data)) { Assert-Unlinked $path }
foreach ($directory in @([IO.Path]::GetDirectoryName($installed), $data)) {
    $marker = Join-Path $directory 'agentwatch.installation.json'
    Assert-Unlinked $marker
    if (-not (Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker) -cne '{"product":"AgentWatch","layoutVersion":1}') { throw "AgentWatch ownership marker mismatch: $directory" }
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'New executable is missing.' }
if ($source.Equals($installed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a staged EXE outside the installed path.' }
$service = Get-CimInstance Win32_Service -Filter "Name='AgentWatch'"
$expected = '"' + $installed + '" service --data-dir "' + $data + '"'
if (-not $service -or $service.PathName -cne $expected) { throw 'Service ImagePath does not match the owned installation.' }
$newHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($manifest -and $manifest.Sha256 -ne $newHash) { throw 'Staged executable differs from its verified publish manifest.' }
$planResult = [ordered]@{ Source=$source; NewSha256=$newHash; Installed=$installed; ServiceState=$service.State; ProcessId=$service.ProcessId; DataDirectory=$data; Action='Archive old EXE, complete stopped DB/WAL/SHM/config/logs, replace only EXE, start service; preserve history and run logs.' }
if ($Plan) { $planResult | ConvertTo-Json; return }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try { $principal=[Security.Principal.WindowsPrincipal]::new($identity); if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use PowerShell Run as administrator. No automatic elevation is attempted.' } } finally { $identity.Dispose() }
if (-not $PSCmdlet.ShouldProcess('AgentWatch service', 'Stop, archive, replace staged EXE and restart')) { return }
$lockPath=Join-Path ([IO.Path]::GetDirectoryName($installed)) 'agentwatch.update-lock'
Assert-Unlinked $lockPath
$updateLock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$archive=[IO.Path]::GetFullPath((Join-Path $data ('archives\upgrade-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))))
if (-not $archive.StartsWith([IO.Path]::GetFullPath($data) + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive containment check failed.' }
Assert-Unlinked $archive
$wasRunning=$service.State -eq 'Running'; $replaced=$false; $stopped=$false
try {
    [IO.Directory]::CreateDirectory($archive) | Out-Null
    Copy-Item -LiteralPath $installed -Destination (Join-Path $archive 'AgentWatch.previous.exe') -ErrorAction Stop
    if ((Get-FileHash -LiteralPath (Join-Path $archive 'AgentWatch.previous.exe')).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw 'Previous EXE archive verification failed.' }
    Stop-Service -Name AgentWatch -ErrorAction Stop
    $controller=Get-Service -Name AgentWatch
    $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(45)); $stopped=$true
    foreach ($name in @('agentwatch.db','agentwatch.db-wal','agentwatch.db-shm','config.json','agentwatch.log','agentwatch.previous.log','agentwatch.installation.json')) {
        $path=Join-Path $data $name; Assert-Unlinked $path
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $archive $name) -ErrorAction Stop }
    }
    $logs=Join-Path $data 'logs'
    if (Test-Path -LiteralPath $logs) {
        Assert-Unlinked $logs
        foreach ($entry in Get-ChildItem -LiteralPath $logs -Recurse -Force) { Assert-Unlinked $entry.FullName }
        Copy-Item -LiteralPath $logs -Destination (Join-Path $archive 'logs') -Recurse -ErrorAction Stop
    }
    $staged=Join-Path ([IO.Path]::GetDirectoryName($installed)) ('AgentWatch.new-' + [Guid]::NewGuid().ToString('N') + '.exe')
    Copy-Item -LiteralPath $source -Destination $staged -ErrorAction Stop
    if ((Get-FileHash -LiteralPath $staged).Hash -ne $newHash) { throw 'Staged copy hash mismatch.' }
    [IO.File]::Move($staged,$installed,$true); $replaced=$true
    $started=[DateTimeOffset]::UtcNow
    Start-Service -Name AgentWatch -ErrorAction Stop
    $controller=Get-Service AgentWatch; $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
    $healthy=$false
    for($i=0;$i -lt 30;$i++) {
        $status = & $source status --json | ConvertFrom-Json
        if ($LASTEXITCODE -eq 0 -and $status.recordedInstanceState -eq 'running' -and [DateTimeOffset]$status.lastPersistedSampleUtc -ge $started) { $healthy=$true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) { throw 'New service did not persist a fresh startup sample.' }
    $planResult['Archive']=$archive; $planResult['Outcome']='updated-and-running'
    $planResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $archive 'upgrade.json') -Encoding utf8
    $planResult | ConvertTo-Json
} catch {
    $failure=$_
    if ($replaced) {
        Stop-Service AgentWatch -ErrorAction Stop
        (Get-Service AgentWatch).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(45))
        $rollback=Join-Path ([IO.Path]::GetDirectoryName($installed)) ('AgentWatch.rollback-' + [Guid]::NewGuid().ToString('N') + '.exe')
        Copy-Item -LiteralPath (Join-Path $archive 'AgentWatch.previous.exe') -Destination $rollback -ErrorAction Stop
        [IO.File]::Move($rollback,$installed,$true)
    }
    if ($wasRunning -and $stopped) { Start-Service AgentWatch -ErrorAction Stop }
    Write-Error "Update failed; archive retained at $archive. $($failure.Exception.Message)"
} finally { $updateLock.Dispose() }
