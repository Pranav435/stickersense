#!/bin/zsh
# Builds the Windows release zips on a Windows PC over SSH, from the current commit, and copies
# them back. Needs OpenSSH Server on the PC (key login) and PowerShell; installs the .NET 8 SDK for
# that user (no admin) if it's missing. Used by scripts/release.sh.
#
#   windows/scripts/build-release-remote.sh user@windows-pc <output-folder>
#
# Produces StickerSense-Windows-x64.zip (app plus the fast model, works offline straight away) and
# StickerSense-Windows-x64-app-only.zip (the app alone; Setup downloads the model).
set -euo pipefail
host="${1:?Usage: windows/scripts/build-release-remote.sh user@windows-pc <output-folder>}"
out="${2:?Usage: windows/scripts/build-release-remote.sh user@windows-pc <output-folder>}"
ROOT="${0:A:h:h:h}"
mkdir -p "$out"

# Runs a PowerShell script on the PC (encoded, so no quoting surprises).
run_ps() {
  local encoded
  encoded="$(printf '%s' "$1" | iconv -f UTF-8 -t UTF-16LE | base64)"
  ssh -o BatchMode=yes "$host" "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
}

tmp="$(mktemp -d)"
git -C "$ROOT" archive --format=zip -o "$tmp/src.zip" HEAD
run_ps '$w = Join-Path $env:USERPROFILE "StickerSense-release-build"; Remove-Item -Recurse -Force $w -ErrorAction SilentlyContinue; New-Item -ItemType Directory $w | Out-Null'
scp -q "$tmp/src.zip" "$host:StickerSense-release-build/src.zip"
rm -rf "$tmp"

run_ps '
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$work = Join-Path $env:USERPROFILE "StickerSense-release-build"
Expand-Archive (Join-Path $work "src.zip") (Join-Path $work "src") -Force

# A .NET 8 SDK: one on PATH, an earlier per-user install, or a fresh per-user install.
if (-not ((& dotnet --list-sdks 2>$null) -match "^8\.")) {
    $dir = Join-Path $env:LOCALAPPDATA "StickerSense-dotnet"
    foreach ($candidate in @((Join-Path $env:USERPROFILE "StickerSense-test\dotnet"), $dir)) {
        if (Test-Path (Join-Path $candidate "dotnet.exe")) { $dir = $candidate; break }
    }
    if (-not (Test-Path (Join-Path $dir "dotnet.exe"))) {
        Write-Host "Installing the .NET 8 SDK for this user into $dir"
        Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile (Join-Path $work "dotnet-install.ps1")
        & (Join-Path $work "dotnet-install.ps1") -Channel 8.0 -InstallDir $dir | Out-Null
    }
    $env:PATH = "$dir;$env:PATH"
    $env:DOTNET_ROOT = $dir
}

$package = Join-Path $work "src\windows\scripts\package-release.ps1"
# The model (1.4 GB) is cached between releases; package-release.ps1 resumes and checks it.
$cache = Join-Path $env:LOCALAPPDATA "StickerSense-release-cache\qwen3-vl-2b"
$modelDir = Join-Path $work "StickerSense-Windows-x64\models\qwen3-vl-2b"
if (Test-Path $cache) { New-Item -ItemType Directory $modelDir -Force | Out-Null; Copy-Item (Join-Path $cache "*") $modelDir -Recurse -Force }
& $package -Zip -Output (Join-Path $work "StickerSense-Windows-x64")
New-Item -ItemType Directory $cache -Force | Out-Null
Copy-Item (Join-Path $modelDir "*") $cache -Recurse -Force
& $package -Zip -SkipModel -Output (Join-Path $work "StickerSense-Windows-x64-app-only")
'

scp -q "$host:StickerSense-release-build/StickerSense-Windows-x64.zip" "$host:StickerSense-release-build/StickerSense-Windows-x64-app-only.zip" "$out/"
run_ps 'Remove-Item -Recurse -Force (Join-Path $env:USERPROFILE "StickerSense-release-build") -ErrorAction SilentlyContinue'
ls -la "$out"/StickerSense-Windows-*.zip
