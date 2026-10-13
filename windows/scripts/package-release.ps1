<#
.SYNOPSIS
    Builds the "bundled" StickerSense release: a self-contained publish of the app with the
    Phi-3.5-vision ONNX model downloaded into models\phi-3.5-vision next to StickerSense.exe.

.DESCRIPTION
    Run from any folder on Windows with the .NET 8 SDK installed:

        powershell -ExecutionPolicy Bypass -File windows\scripts\package-release.ps1

    The model is about 2.6 GB (graphics card build) or 3.2 GB (processor build). Files already
    downloaded are kept, so running the script again resumes rather than starting over.
    Output is plain text with no progress bars, so it reads well with a screen reader.

.PARAMETER Runtime
    win-x64 (default) or win-arm64.

.PARAMETER Variant
    DirectML (default; graphics card, falls back to the processor) or Cpu (processor only).

.PARAMETER Output
    Folder for the release. Default: windows\publish\StickerSense-<Runtime>.

.PARAMETER NvdaControllerDll
    Path to an nvdaControllerClient.dll to include instead of downloading NV Access's.

.PARAMETER SkipNvdaController
    Leave out NVDA's controller client. By default the script downloads NV Access's controller
    client zip (a pinned version, checked against its SHA-256) and puts the DLL for -Runtime next
    to the exe, with its licence. Without it, NVDA users hear StickerSense in the Windows voice.

.PARAMETER SkipModel
    Publish the app only, without the model (users then download it from Setup).

.PARAMETER Zip
    Also create <Output>.zip.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [ValidateSet('DirectML', 'Cpu')]
    [string] $Variant = 'DirectML',

    [string] $Output,

    [string] $NvdaControllerDll,

    [switch] $SkipNvdaController,

    [switch] $SkipModel,

    [switch] $Zip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress bars are slow and noisy for screen readers

$windowsRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $windowsRoot 'src\StickerSense.App\StickerSense.App.csproj'
if (-not $Output) { $Output = Join-Path $windowsRoot "publish\StickerSense-$Runtime" }
$Output = [System.IO.Path]::GetFullPath($Output)

$repository = 'microsoft/Phi-3.5-vision-instruct-onnx'
$folder = if ($Variant -eq 'DirectML') { 'gpu/gpu-int4-rtn-block-32' } else { 'cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4' }
$modelDir = Join-Path $Output 'models\phi-3.5-vision'

function Say([string] $message) { Write-Host $message }

# 1. Publish the app, self-contained so no .NET runtime needs installing.
Say "Publishing StickerSense for $Runtime to $Output"
& dotnet publish $project --configuration Release --runtime $Runtime --self-contained true --output $Output -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item (Join-Path $windowsRoot 'README.md') (Join-Path $Output 'README.md') -Force

# NVDA only reads UI Automation notifications from the app with focus, so StickerSense needs
# NVDA's controller client to speak (and braille) through NVDA while you're in a chat app.
$nvdaVersion = '2026.2'
$nvdaZipUrl = "https://download.nvaccess.org/releases/$nvdaVersion/nvda_${nvdaVersion}_controllerClient.zip"
$nvdaZipSha256 = '510736F021AEFA33378076FA342A4524B586BC1C6B096DB9479AF42B28AAC649'
$nvdaDll = Join-Path $Output 'nvdaControllerClient.dll'
if ($SkipNvdaController) {
    Remove-Item $nvdaDll, (Join-Path $Output 'nvdaControllerClient-license.txt') -Force -ErrorAction SilentlyContinue
}
elseif ($NvdaControllerDll) {
    if (-not (Test-Path $NvdaControllerDll)) { throw "NVDA controller DLL not found: $NvdaControllerDll" }
    Copy-Item $NvdaControllerDll $nvdaDll -Force
    Say 'Copied the NVDA controller client next to StickerSense.exe.'
}
else {
    $architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $zipFile = Join-Path ([System.IO.Path]::GetTempPath()) "nvda_${nvdaVersion}_controllerClient.zip"
    Say "Downloading NVDA's controller client $nvdaVersion from NV Access."
    Invoke-WebRequest -Uri $nvdaZipUrl -OutFile $zipFile -UseBasicParsing
    try {
        $hash = (Get-FileHash $zipFile -Algorithm SHA256).Hash
        if ($hash -ne $nvdaZipSha256) { throw "The NVDA controller client download didn't match its expected SHA-256 ($hash)." }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zipFile)
        try {
            $wanted = @{ "$architecture/nvdaControllerClient.dll" = $nvdaDll; 'license.txt' = (Join-Path $Output 'nvdaControllerClient-license.txt') }
            foreach ($entryName in $wanted.Keys) {
                $entry = $archive.GetEntry($entryName)
                if (-not $entry) { throw "$entryName is missing from the NVDA controller client zip." }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $wanted[$entryName], $true)
            }
        }
        finally { $archive.Dispose() }
    }
    finally { Remove-Item $zipFile -Force -ErrorAction SilentlyContinue }
    Say "Added NVDA's controller client ($architecture) and its licence next to StickerSense.exe."
}

# 2. Download the model from Hugging Face into the release.
if (-not $SkipModel) {
    New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
    # Both builds share file names: clear the other build's files instead of mixing them.
    $marker = Join-Path $modelDir 'stickersense-model.json'
    if (Test-Path $marker) {
        $existing = (Get-Content $marker -Raw | ConvertFrom-Json).variant
        if ($existing -and $existing -ne $Variant) {
            Say "Removing the $existing model files to make room for the $Variant build."
            Get-ChildItem $modelDir -File | Remove-Item -Force
        }
    }

    Say "Listing the $Variant model files ($folder)."
    $listing = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repository/tree/main/$folder"
    $files = @($listing | Where-Object { $_.type -eq 'file' })
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue

    $index = 0
    foreach ($file in $files) {
        $index++
        $name = Split-Path $file.path -Leaf
        $size = if ($file.lfs) { [int64]$file.lfs.size } else { [int64]$file.size }
        $target = Join-Path $modelDir $name
        if ((Test-Path $target) -and ((Get-Item $target).Length -eq $size)) {
            Say "File $index of $($files.Count): $name is already downloaded."
            continue
        }
        $megabytes = [math]::Round($size / 1MB)
        Say "File $index of $($files.Count): downloading $name, $megabytes megabytes."
        $url = "https://huggingface.co/$repository/resolve/main/$($file.path)"
        $part = "$target.part"
        if ($curl) {
            # Resumes a partial download (-C -), follows the redirect to the CDN, retries hiccups.
            & $curl.Source --location --fail --silent --show-error --retry 5 --continue-at - --output $part $url
            if ($LASTEXITCODE -ne 0) { throw "Download of $name failed (curl exit code $LASTEXITCODE). Run the script again to resume." }
        }
        else {
            Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing
        }
        if ((Get-Item $part).Length -ne $size) { throw "$name is incomplete. Run the script again to resume." }
        Move-Item $part $target -Force
    }

    @{
        variant      = $Variant
        repository   = $repository
        folder       = $folder
        downloadedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -Path $marker -Encoding UTF8
    Say "The $Variant model is in $modelDir."
}

# 3. Optionally zip it (tar handles files over 2 GB; Compress-Archive in Windows PowerShell doesn't).
if ($Zip) {
    $zipPath = "$Output.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { throw 'tar.exe was not found, so the zip was not created. The release folder is ready to copy as it is.' }
    Say "Creating $zipPath"
    Push-Location (Split-Path $Output -Parent)
    try {
        & $tar.Source -a -c -f $zipPath (Split-Path $Output -Leaf)
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

Say "Done. The release is in $Output. Run StickerSense.exe from there."
