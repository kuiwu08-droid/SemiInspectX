param([string]$Release)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if (!$Release) { $Release=(Get-ChildItem (Join-Path $taskRoot 'artifacts/releases') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName }
if (!(Test-Path -LiteralPath (Join-Path $Release 'SemiInspectX.Desktop.exe'))) { throw 'Published release not found.' }
$smoke=Join-Path $taskRoot ('artifacts/relocated-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $smoke | Out-Null
Copy-Item -Path (Join-Path $Release '*') -Destination $smoke -Recurse
Set-Location -LiteralPath $smoke
$env:PATH="$env:SystemRoot\System32;$env:SystemRoot"
Remove-Item Env:DOTNET_ROOT,Env:DOTNET_ROOT_X64,Env:DOTNET_HOST_PATH -ErrorAction SilentlyContinue
$env:DOTNET_MULTILEVEL_LOOKUP='0'
& (Join-Path $smoke 'runner/SemiInspectX.Runner.exe') inspect --dataset datasets/generated/demo --recipe recipes/default.json --output artifacts/smoke --count 100
if ($LASTEXITCODE -ne 0) { throw 'Portable CLI smoke failed.' }
$desktop=Start-Process -FilePath (Join-Path $smoke 'SemiInspectX.Desktop.exe') -ArgumentList '--snapshot' -WindowStyle Hidden -PassThru
if (!$desktop.WaitForExit(30000)) { throw 'Portable desktop smoke timed out.' }
if ($desktop.ExitCode -ne 0 -or !(Test-Path 'artifacts/screenshots/workstation.png')) { throw 'Portable desktop smoke failed.' }
$report=@{Timestamp=(Get-Date -Format 'o');Release=$Release;Relocated=$smoke;SdkOnPath=$false;DesktopExit=$desktop.ExitCode;CliDies=100;SeparateCleanMachine=$false}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts/release-smoke.json') -Encoding utf8
Write-Output 'Portable CLI and desktop smoke passed with minimal PATH.'
