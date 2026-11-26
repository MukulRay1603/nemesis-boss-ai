# Captures the original April 2026 build (Unity Project/Build/Nemesis-ai.exe) unmodified, from its
# window, with ffmpeg screen capture, and makes the README GIF.
# The legacy build has no recorder or command-line options of its own; only Unity's standard
# window-size flags are passed.
#
#   .\eval\capture_legacy.ps1                 # 1280x720 window, 6 s warm-up, 16 s clip
# Outputs: media/archive/legacy_v1.mp4 (gitignored), docs/gallery/legacy.gif
param(
    [int]$Width = 1280, [int]$Height = 720,
    [int]$Warmup = 6, [int]$Seconds = 16,
    [int]$GifWidth = 640, [int]$GifFps = 15,
    [string]$Ffmpeg = "ffmpeg"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "Unity Project\Build\Nemesis-ai.exe"
$mp4 = Join-Path $repo "media\archive\legacy_v1.mp4"
$gif = Join-Path $repo "docs\gallery\legacy.gif"
New-Item -ItemType Directory -Force (Split-Path $mp4) | Out-Null

$p = Start-Process $exe -ArgumentList "-screen-fullscreen", "0", "-screen-width", $Width, "-screen-height", $Height -PassThru
try {
    Start-Sleep $Warmup
    & $Ffmpeg -y -loglevel error -f gdigrab -framerate 30 -t $Seconds -i "title=Nemesis-ai" `
        -vf "crop=trunc(iw/2)*2:trunc(ih/2)*2" -c:v libx264 -pix_fmt yuv420p -crf 18 $mp4
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
& $Ffmpeg -y -loglevel error -i $mp4 -vf "fps=$GifFps,scale=${GifWidth}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4" $gif
"mp4: $mp4 ($([math]::Round((Get-Item $mp4).Length/1MB,1)) MB)"
"gif: $gif ($([math]::Round((Get-Item $gif).Length/1MB,1)) MB)"
