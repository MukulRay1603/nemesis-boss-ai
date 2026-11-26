# Captures the original April 2026 game (tag v1.0-legacy, commit 0bff669) from its window with
# ffmpeg screen capture, and makes the README GIF.
#
# The legacy game is built from the tag, unmodified, in a separate worktree:
#   git worktree add --detach ..\nemesis-legacy v1.0-legacy
#   copy "Unity Project\Assets\Settings\SampleSceneProfile.asset*" "..\nemesis-legacy\Unity Project\Assets\Settings\"
#     (the scene's post-processing profile was gitignored in the original repo but present on Ray's disk;
#      it is the unchanged Unity template file dated 2024)
#   Unity.exe -batchmode -quit -projectPath "..\nemesis-legacy\Unity Project" -buildTarget Win64
#             -buildWindows64Player "..\nemesis-legacy-build\Nemesis-legacy.exe"
# Note: "Unity Project/Build/Nemesis-ai.exe" in this repo is NOT the legacy fight; it is the
# 30 March ML-Agents training environment and idles without a trainer.
#
# The legacy game has no recorder or options of its own; only Unity's window-size flags are passed.
#   .\eval\capture_legacy.ps1                 # 1280x720 window, 14 s warm-up, 16 s clip (the README GIF used -Seconds 20)
# Outputs: media/archive/legacy_v1.mp4 (gitignored), docs/gallery/legacy.gif
param(
    [int]$Width = 1280, [int]$Height = 720,
    [int]$Warmup = 14, [int]$Seconds = 16,
    [int]$GifWidth = 560, [int]$GifFps = 12, [int]$GifStart = 2, [int]$GifSeconds = 12,
    [string]$Ffmpeg = "ffmpeg",
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$exe = if ($Exe) { $Exe } else { Join-Path (Split-Path $repo -Parent) "nemesis-legacy-build\Nemesis-legacy.exe" }
$mp4 = Join-Path $repo "media\archive\legacy_v1.mp4"
$gif = Join-Path $repo "docs\gallery\legacy.gif"
New-Item -ItemType Directory -Force (Split-Path $mp4) | Out-Null

# The legacy player pauses while its window is not focused, so it is brought to the front.
# Windows only lets a background process take focus right after a key event, hence the ALT tap.
Add-Type -Namespace Nem -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h); [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c); [DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, System.UIntPtr e); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; } [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; } [DllImport("user32.dll")] public static extern bool GetClientRect(System.IntPtr h, out RECT r); [DllImport("user32.dll")] public static extern bool ClientToScreen(System.IntPtr h, ref POINT p);'
[Nem.Win]::SetProcessDPIAware() | Out-Null
function Focus-Game($h) {
    if ($h -eq 0 -or [Nem.Win]::GetForegroundWindow() -eq $h) { return }
    [Nem.Win]::keybd_event(0x12, 0, 0, [System.UIntPtr]::Zero); [Nem.Win]::keybd_event(0x12, 0, 2, [System.UIntPtr]::Zero)
    [Nem.Win]::ShowWindow($h, 9) | Out-Null; [Nem.Win]::SetForegroundWindow($h) | Out-Null
}
$p = Start-Process $exe -ArgumentList "-screen-fullscreen", "0", "-screen-width", $Width, "-screen-height", $Height -PassThru
try {
    for ($i = 0; $i -lt $Warmup; $i++) {
        Start-Sleep 1
        $p.Refresh()
        Focus-Game $p.MainWindowHandle
    }
    # Capture the window's client area from the desktop: grabbing a Direct3D window by title returns a stale frame.
    $h = $p.MainWindowHandle
    $r = New-Object Nem.Win+RECT; [Nem.Win]::GetClientRect($h, [ref]$r) | Out-Null
    $pt = New-Object Nem.Win+POINT; [Nem.Win]::ClientToScreen($h, [ref]$pt) | Out-Null
    $w = ($r.R - $r.L) - (($r.R - $r.L) % 2); $hh = ($r.B - $r.T) - (($r.B - $r.T) % 2)
    $ff = Start-Process $Ffmpeg -NoNewWindow -PassThru -ArgumentList "-y -loglevel error -f gdigrab -draw_mouse 0 -framerate 30 -offset_x $($pt.X) -offset_y $($pt.Y) -video_size ${w}x${hh} -t $Seconds -i desktop -c:v libx264 -pix_fmt yuv420p -crf 18 `"$mp4`""
    while (-not $ff.HasExited) { Focus-Game $p.MainWindowHandle; Start-Sleep -Milliseconds 300 }
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
& $Ffmpeg -y -loglevel error -ss $GifStart -t $GifSeconds -i $mp4 -vf "fps=$GifFps,scale=${GifWidth}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4" $gif
"mp4: $mp4 ($([math]::Round((Get-Item $mp4).Length/1MB,1)) MB)"
"gif: $gif ($([math]::Round((Get-Item $gif).Length/1MB,1)) MB)"
