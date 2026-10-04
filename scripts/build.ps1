param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/nuget'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/dotnet-home'
$dotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet=(Get-Command dotnet).Source }
$cmakeCommand=Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmake=$cmakeCommand.Source }
else {
    $locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $cmake=& $locator -latest -products '*' -find '**/cmake.exe' | Select-Object -First 1
}
if (!$cmake) { throw 'CMake not found. Install the Visual C++ desktop workload.' }
$opencv=Join-Path $taskRoot '.tools/opencv/build'
if (!(Test-Path -LiteralPath $opencv)) { throw 'OpenCV 4.14.0 is missing. Run scripts/bootstrap.ps1.' }
function Invoke-Checked([string]$program,[string[]]$arguments) {
    & $program @arguments
    if ($LASTEXITCODE -ne 0) { throw "$program failed with exit $LASTEXITCODE" }
}
Invoke-Checked $cmake @('-S','native','-B','build/native','-A','x64',"-DOpenCV_DIR=$opencv")
Invoke-Checked $cmake @('--build','build/native','--config','Release','--parallel')
Get-ChildItem -Path "$opencv/x64/vc*/bin/opencv_world4140.dll" | Copy-Item -Destination 'build/native/Release'
Invoke-Checked $dotnet @('build','SemiInspectX.slnx','-c','Release')
if (!(Test-Path 'datasets/generated/demo/manifest.json')) {
    Invoke-Checked (Join-Path $taskRoot 'build/native/Release/si_generate.exe') @('datasets/generated/demo','100','42','2')
}
if (!$SkipTests) {
    $ctest=Join-Path (Split-Path $cmake) 'ctest.exe'
    Invoke-Checked $ctest @('--test-dir','build/native','-C','Release','--output-on-failure')
    Invoke-Checked $dotnet @('test','tests/SemiInspectX.Tests','-c','Release','--no-build','--logger','trx','--results-directory','artifacts/tests')
}
