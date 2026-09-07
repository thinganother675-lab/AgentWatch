[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Run scripts/Bootstrap-Dotnet.ps1 first.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts\releases\0.2.0-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$buildRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $buildRoot.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build artifacts must remain inside this workspace.' }
if (Test-Path -LiteralPath $buildRoot) { throw 'Choose a new build output directory; prior artifacts are never overwritten.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools\cli-home'
$env:DOTNET_ROOT=Join-Path $taskRoot '.tools\dotnet'
$env:DOTNET_NOLOGO='1'
$buildLogs=Join-Path $buildRoot 'build'
$publishPath=Join-Path $buildRoot 'publish'
$previousTestRoot=$env:AGENTWATCH_TEST_ARTIFACTS
$env:AGENTWATCH_TEST_ARTIFACTS=Join-Path $buildRoot 'tests'
[IO.Directory]::CreateDirectory($buildLogs) | Out-Null
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $buildLogs ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}
Push-Location $taskRoot
try {
    Invoke-Dotnet 'restore' @('restore','AgentWatch.sln','--locked-mode','--nologo')
    Invoke-Dotnet 'build' @('build','AgentWatch.sln','-c','Release','--no-restore','--nologo','-warnaserror')
    Invoke-Dotnet 'test' @('test','AgentWatch.sln','-c','Release','--no-build','--no-restore','--logger','trx','--results-directory',(Join-Path $buildRoot 'tests\trx'))
    Invoke-Dotnet 'publish' @('publish','src/AgentWatch/AgentWatch.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:PublishTrimmed=false','-p:RestoreLockedMode=true','-o',$publishPath,'--nologo')
    $files=@(Get-ChildItem -LiteralPath $publishPath -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'AgentWatch.exe') { throw 'Expected exactly one published EXE.' }
    $binary=$files[0].FullName
    & $binary version
    if ($LASTEXITCODE -ne 0) { throw 'Published executable smoke test failed.' }
    $manifest=[ordered]@{ File=$binary; BuildRoot=$buildRoot; Bytes=$files[0].Length; Sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash; Sdk=(& $dotnet --version); TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'publish.json') -Encoding utf8
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts\latest-build.json') -Encoding utf8
    $manifest | ConvertTo-Json
} finally { $env:AGENTWATCH_TEST_ARTIFACTS=$previousTestRoot; Pop-Location }
