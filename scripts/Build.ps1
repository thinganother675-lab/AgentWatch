[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Run scripts/Bootstrap-Dotnet.ps1 first.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools\cli-home'
$env:DOTNET_ROOT=Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_NOLOGO='1'
$buildLogs = Join-Path $taskRoot 'artifacts\build'
[IO.Directory]::CreateDirectory($buildLogs) | Out-Null
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $buildLogs ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}
Push-Location $taskRoot
try {
    Invoke-Dotnet 'clean' @('clean','AgentWatch.sln','-c','Release','--nologo','-v','minimal')
    Invoke-Dotnet 'restore' @('restore','AgentWatch.sln','--locked-mode','--nologo')
    Invoke-Dotnet 'build' @('build','AgentWatch.sln','-c','Release','--no-restore','--nologo','-warnaserror')
    Invoke-Dotnet 'test' @('test','AgentWatch.sln','-c','Release','--no-build','--no-restore','--logger','trx','--results-directory','artifacts/tests/final')
    Invoke-Dotnet 'publish' @('publish','src/AgentWatch/AgentWatch.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:PublishTrimmed=false','-p:RestoreLockedMode=true','-o','artifacts/publish','--nologo')
    $files = @(Get-ChildItem -LiteralPath 'artifacts\publish' -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'AgentWatch.exe') { throw 'Expected exactly one published EXE.' }
    $binary = $files[0].FullName
    & $binary version
    if ($LASTEXITCODE -ne 0) { throw 'Published executable smoke test failed.' }
    [ordered]@{ File=$binary; Bytes=$files[0].Length; Sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash; Sdk=(& $dotnet --version); TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildLogs 'publish.json') -Encoding utf8
} finally { Pop-Location }
