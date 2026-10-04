$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$dotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet=(Get-Command dotnet).Source }
function Invoke-Checked([string]$program,[string[]]$arguments) { & $program @arguments; if($LASTEXITCODE -ne 0){throw "$program failed: $LASTEXITCODE"} }
Invoke-Checked 'build/native/Release/si_generate.exe' @('datasets/generated/evaluation','1000','314159','2')
Invoke-Checked 'build/native/Release/si_generate.exe' @('datasets/generated/metrology','1000','271828','0')
Invoke-Checked $dotnet @('src/SemiInspectX.Runner/bin/Release/net10.0/SemiInspectX.Runner.dll','evaluate','--dataset','datasets/generated/evaluation','--output','artifacts/evaluation')
Invoke-Checked $dotnet @('src/SemiInspectX.Runner/bin/Release/net10.0/SemiInspectX.Runner.dll','evaluate','--dataset','datasets/generated/metrology','--output','artifacts/metrology')
