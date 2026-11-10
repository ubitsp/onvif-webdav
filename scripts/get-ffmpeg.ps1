<#
  下载 ffmpeg（Windows 64 位）并放到 third_party\ffmpeg\bin\
  只保留 ffmpeg.exe / ffprobe.exe，其余删掉，省空间。
  用法： powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'third_party\ffmpeg\bin'
$tempZip = Join-Path $env:TEMP 'ffmpeg-essentials.zip'
$tempDir = Join-Path $env:TEMP 'ffmpeg-extract'

$url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'

Write-Host "目标目录：$targetDir"
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

if ((Test-Path (Join-Path $targetDir 'ffmpeg.exe')) -and (Test-Path (Join-Path $targetDir 'ffprobe.exe'))) {
    Write-Host '已存在 ffmpeg.exe / ffprobe.exe，跳过下载。要强制更新请先删掉 third_party\ffmpeg 目录。' -ForegroundColor Yellow
    exit 0
}

Write-Host "下载 ffmpeg：$url"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing

Write-Host '解压…'
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempDir -Force

$ffmpeg = Get-ChildItem -Path $tempDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
$ffprobe = Get-ChildItem -Path $tempDir -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1

if (-not $ffmpeg) { throw '压缩包里没找到 ffmpeg.exe' }

Copy-Item $ffmpeg.FullName (Join-Path $targetDir 'ffmpeg.exe') -Force
if ($ffprobe) { Copy-Item $ffprobe.FullName (Join-Path $targetDir 'ffprobe.exe') -Force }

Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

$version = & (Join-Path $targetDir 'ffmpeg.exe') -version 2>&1 | Select-Object -First 1
Write-Host "完成：$version" -ForegroundColor Green
Write-Host "位置：$targetDir"
