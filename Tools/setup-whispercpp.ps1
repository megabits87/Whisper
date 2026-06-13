<#
.SYNOPSIS
  Installs the whisper.cpp GPU (CUDA/cuBLAS) backend that VoxType uses for recognition.

.DESCRIPTION
  Downloads the prebuilt whisper.cpp server (CUDA 11.8 build) and the matching NVIDIA cuBLAS
  redistributable DLLs, and lays them out in one folder that the app launches.

  Default target: %LocalAppData%\VoxType\whispercpp
  After it finishes, that folder contains whisper-server.exe + all required DLLs, and the app
  will find it automatically (its default WhisperServerExe points there).

.NOTES
  Requires an NVIDIA GPU with a driver new enough for CUDA 11.8 (driver >= 522.x; "nvidia-smi"
  should report CUDA Version 11.8 or higher). ~0.5 GB download, ~0.8 GB on disk.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\setup-whispercpp.ps1
#>
[CmdletBinding()]
param(
    [string] $Target = (Join-Path $env:LOCALAPPDATA "VoxType\whispercpp"),
    [string] $WhisperVersion = "v1.8.6",
    [string] $CublasArchive = "libcublas-windows-x86_64-11.11.3.6-archive.zip",
    [switch] $Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # faster Invoke-WebRequest

function Say($m) { Write-Host "[setup-whispercpp] $m" -ForegroundColor Cyan }

$exe = Join-Path $Target "whisper-server.exe"
if ((Test-Path $exe) -and -not $Force) {
    Say "Already installed at: $Target"
    Say "(use -Force to reinstall)"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("wcpp_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    # 1) whisper.cpp CUDA build (whisper-server.exe + ggml/cuda dlls, but NOT the big cuBLAS dlls)
    $wzipName = "whisper-cublas-11.8.0-bin-x64.zip"
    $wzipUrl  = "https://github.com/ggerganov/whisper.cpp/releases/download/$WhisperVersion/$wzipName"
    $wzip     = Join-Path $tmp $wzipName
    Say "Downloading whisper.cpp $WhisperVersion (CUDA build)..."
    Invoke-WebRequest -Uri $wzipUrl -OutFile $wzip
    Say "Extracting whisper.cpp..."
    $wdir = Join-Path $tmp "whisper"
    Expand-Archive -Path $wzip -DestinationPath $wdir -Force
    $release = Get-ChildItem $wdir -Recurse -Filter "whisper-server.exe" | Select-Object -First 1
    if (-not $release) { throw "whisper-server.exe not found in the downloaded archive" }
    Copy-Item (Join-Path $release.DirectoryName "*") -Destination $Target -Recurse -Force

    # 2) cuBLAS redistributable (the large cublas64_11.dll / cublasLt64_11.dll the build needs at runtime)
    $cuzipUrl = "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/$CublasArchive"
    $cuzip    = Join-Path $tmp "cublas.zip"
    Say "Downloading NVIDIA cuBLAS runtime (~400 MB)..."
    Invoke-WebRequest -Uri $cuzipUrl -OutFile $cuzip
    Say "Extracting cuBLAS DLLs..."
    $cudir = Join-Path $tmp "cublas"
    Expand-Archive -Path $cuzip -DestinationPath $cudir -Force
    Get-ChildItem $cudir -Recurse -Filter "cublas*64_11.dll" | ForEach-Object {
        Copy-Item $_.FullName -Destination $Target -Force
        Say "  + $($_.Name)"
    }

    # 3) ffmpeg — lets the app transcribe non-native inputs (m4a, wma, and video: mp4/mkv/mov/...) by
    #    extracting the audio track. Optional: if this step fails, native WAV/MP3/FLAC/OGG still work.
    if (-not (Test-Path (Join-Path $Target "ffmpeg.exe"))) {
        try {
            $ffUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
            $ffzip = Join-Path $tmp "ffmpeg.zip"
            Say "Downloading ffmpeg (for video/m4a/etc.)..."
            Invoke-WebRequest -Uri $ffUrl -OutFile $ffzip
            Say "Extracting ffmpeg..."
            $ffdir = Join-Path $tmp "ffmpeg"
            Expand-Archive -Path $ffzip -DestinationPath $ffdir -Force
            $ffexe = Get-ChildItem $ffdir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
            if ($ffexe) { Copy-Item $ffexe.FullName -Destination $Target -Force; Say "  + ffmpeg.exe" }
            else { Say "  (ffmpeg.exe not found in archive — skipping; only WAV/MP3/FLAC/OGG will work)" }
        }
        catch {
            Say "  ffmpeg download failed (optional) — only WAV/MP3/FLAC/OGG will be supported. $($_.Exception.Message)"
        }
    }

    if (-not (Test-Path $exe)) { throw "Install failed: $exe is missing" }
    Say "Done. whisper.cpp backend installed at:"
    Say "  $Target"
    Say "Whisper Voice Typer will use it automatically."
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
