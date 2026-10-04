$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/nuget'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/dotnet-home'
$dotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet=(Get-Command dotnet).Source }
# Unique staging path prevents old files from contaminating a new release.
$release=Join-Path $taskRoot ('artifacts/releases/SemiInspectX-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
& $dotnet publish src/SemiInspectX.Desktop -c Release -r win-x64 --self-contained true -o $release
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item build/native/Release/semiinspect.dll,build/native/Release/opencv_world4140.dll -Destination $release
New-Item -ItemType Directory -Force (Join-Path $release 'recipes'),(Join-Path $release 'datasets/generated/demo'),(Join-Path $release 'licenses') | Out-Null
Copy-Item recipes/*.json (Join-Path $release 'recipes')
Copy-Item datasets/generated/demo/* (Join-Path $release 'datasets/generated/demo')
Copy-Item LICENSE,README.md,README_CN.md -Destination $release
Copy-Item .tools/opencv/LICENSE.txt -Destination (Join-Path $release 'licenses/OpenCV-LICENSE.txt')
Copy-Item build/native/_deps/googletest-src/LICENSE -Destination (Join-Path $release 'licenses/GoogleTest-LICENSE.txt')
Copy-Item docs/third-party.md (Join-Path $release 'licenses/third-party.md')
$locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt=Get-ChildItem -Path "$vs/VC/Redist/MSVC/*/x64/Microsoft.VC143.CRT" -Directory | Sort-Object FullName -Descending | Select-Object -First 1
if (!$crt) { throw 'MSVC redistributable folder missing.' }
Copy-Item -Path (Join-Path $crt.FullName '*.dll') -Destination $release
& $dotnet publish src/SemiInspectX.Runner -c Release -r win-x64 --self-contained true -o (Join-Path $release 'runner')
if ($LASTEXITCODE -ne 0) { throw 'Runner publish failed.' }
Copy-Item build/native/Release/semiinspect.dll,build/native/Release/opencv_world4140.dll -Destination (Join-Path $release 'runner')
Copy-Item -Path (Join-Path $crt.FullName '*.dll') -Destination (Join-Path $release 'runner')
Compress-Archive -Path "$release/*" -DestinationPath "$release.zip"
Get-FileHash -LiteralPath "$release.zip" -Algorithm SHA256 | Format-List
Write-Output "Release directory: $release"
