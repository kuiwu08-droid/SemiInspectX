param([switch]$Snapshot,[switch]$Demo)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$dotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet=(Get-Command dotnet).Source }
$dll=Join-Path $taskRoot 'src/SemiInspectX.Desktop/bin/Release/net10.0-windows/SemiInspectX.Desktop.dll'
if (!(Test-Path $dll)) { throw 'Run scripts/build.ps1 first.' }
$arguments=@($dll)
if ($Snapshot) { $arguments+='--snapshot' }
if ($Demo) { $arguments+='--demo' }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Desktop failed: $LASTEXITCODE" }
