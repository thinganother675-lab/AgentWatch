[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $taskRoot '.tools'
$dotnet = Join-Path $toolRoot 'dotnet\dotnet.exe'
$expectedVersion = '10.0.400'
$expectedHash = '9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366'
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip'
if (-not [Environment]::Is64BitOperatingSystem -or -not [OperatingSystem]::IsWindows()) { throw 'Use Windows x64 and PowerShell 7.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_HOME=Join-Path $toolRoot 'cli-home'
$env:DOTNET_NOLOGO='1'
if (Test-Path -LiteralPath $dotnet) {
    $version = & $dotnet --version
    if ($LASTEXITCODE -eq 0 -and $version -eq $expectedVersion) { Write-Output "Project-local SDK $version is ready."; return }
    throw 'An unexpected project-local SDK already exists. Inspect .tools/dotnet before replacing it.'
}
[IO.Directory]::CreateDirectory($toolRoot) | Out-Null
$archive = Join-Path $toolRoot 'dotnet-sdk-10.0.400-win-x64.zip'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expectedHash) { throw 'SDK SHA-512 mismatch; archive was preserved for inspection and not extracted.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot 'dotnet')
$version = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or $version -ne $expectedVersion) { throw 'Project-local SDK verification failed.' }
Write-Output "Project-local SDK $version installed; no global PATH, registry or machine environment changes."
