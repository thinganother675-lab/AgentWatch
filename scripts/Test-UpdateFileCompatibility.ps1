$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot ('../artifacts/update-compat-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$installed=Join-Path $root 'installed.exe'; $staged=Join-Path $root 'staged.exe'; $rollback=Join-Path $root 'rollback.exe'
[IO.File]::WriteAllText($installed,'old'); [IO.File]::WriteAllText($staged,'new'); [IO.File]::WriteAllText($rollback,'old')
[IO.File]::Replace($staged,$installed,[NullString]::Value)
if([IO.File]::ReadAllText($installed) -cne 'new' -or [IO.File]::Exists($staged)){throw 'Replacement failed'}
[IO.File]::Replace($rollback,$installed,[NullString]::Value)
if([IO.File]::ReadAllText($installed) -cne 'old' -or [IO.File]::Exists($rollback)){throw 'Rollback failed'}
[pscustomobject]@{PowerShell=$PSVersionTable.PSVersion.ToString();Replacement='passed';Rollback='passed';Evidence=$root} | ConvertTo-Json
