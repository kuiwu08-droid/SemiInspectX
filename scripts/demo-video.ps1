$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$ffmpeg=Get-ChildItem -LiteralPath '.tools/ffmpeg' -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if(!$ffmpeg){throw 'Install a Windows FFmpeg build under .tools/ffmpeg first.'}
if(!(Test-Path 'artifacts/demo-frames/frame-0000.png')){throw 'Run scripts/run.ps1 -Demo first.'}
New-Item -ItemType Directory -Force artifacts/video | Out-Null
& $ffmpeg.FullName -hide_banner -y -framerate 1 -i 'artifacts/demo-frames/frame-%04d.png' -vf 'scale=1420:940,fps=30,format=yuv420p' -c:v libx264 -preset fast -crf 20 -movflags +faststart artifacts/video/SemiInspectX-demo.mp4
if($LASTEXITCODE -ne 0){throw 'Demo encoding failed.'}
