param([switch]$InstallCpp)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
New-Item -ItemType Directory -Force .tools | Out-Null
if (!(Test-Path '.tools/dotnet/dotnet.exe')) {
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools/dotnet-install.ps1'
    & '.tools/dotnet-install.ps1' -Version '10.0.401' -InstallDir "$taskRoot/.tools/dotnet" -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
}
if (!(Test-Path '.tools/opencv/build/OpenCVConfig.cmake')) {
    $opencvFile=Join-Path $taskRoot '.tools/opencv.exe'
    Invoke-WebRequest 'https://github.com/opencv/opencv/releases/download/4.14.0/opencv-4.14.0-windows.exe' -OutFile $opencvFile
    $expected='5F266A8B73BED535962D7E861A6457E32A0DD5F463AD0A7CF8707A135469BE63'
    if ((Get-FileHash -LiteralPath $opencvFile -Algorithm SHA256).Hash -ne $expected) { throw 'OpenCV SHA256 mismatch.' }
    $extract=Start-Process -FilePath $opencvFile -ArgumentList @('-y',('-o"'+(Join-Path $taskRoot '.tools')+'"')) -WindowStyle Hidden -PassThru
    $extract.WaitForExit()
    if ($extract.ExitCode -ne 0) { throw 'OpenCV extraction failed.' }
}
if ($InstallCpp) {
    # Uses the existing VS installation. Toolchain components may require Windows elevation.
    $locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installedCpp=& $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($installedCpp) { Write-Output 'C++ toolchain is already installed.'; return }
    $vs=& $locator -latest -products '*' -property installationPath
    if (!$vs) { throw 'Install Visual Studio Build Tools with the C++ desktop workload first.' }
    $setup=Join-Path (Split-Path $locator) 'setup.exe'
    $installer=Start-Process -FilePath $setup -ArgumentList @('modify','--installPath',('"'+$vs+'"'),'--add','Microsoft.VisualStudio.Workload.NativeDesktop','--includeRecommended','--quiet','--norestart') -Verb RunAs -WindowStyle Hidden -PassThru
    $installer.WaitForExit()
    if ($installer.ExitCode -notin @(0,3010)) { throw "C++ installation failed: $($installer.ExitCode)" }
}
