# Headless evaluation of the Nemesis boss inside the real Unity build.
#
# For every opponent preset (default bot and "hard" bot) and seed, runs N episodes of:
#   <Tag>       the model currently in Unity Project/Assets/Models/BossAgent.onnx
#   and with -Baselines also:
#   random      same boss body, uniform random action
#   attack      same boss body, always attack
#   fsm         scripted FSM boss (no telegraph or recovery window)
# Headless (-nographics, Sentis on CPU). Results: <Out>/<label>_<preset>_s<seed>_{episodes.csv,summary.json}
#
#   .\eval\run_unity_eval.ps1 -Tag v3 -Baselines          # 200 episodes x seeds 1,2,3 x 2 opponents
#   .\eval\run_unity_eval.ps1 -Tag v4 -SkipBuild -Episodes 50 -Seeds 1
#
# Outcomes do not depend on -TimeScale (all game logic runs in FixedUpdate).

param(
    [string]$Tag = "model",
    [switch]$Baselines,
    [int]$Episodes = 200,
    [int[]]$Seeds = @(1, 2, 3),
    [string[]]$Presets = @("default", "hard"),
    [float]$TimeScale = 100,
    [int]$Parallel = 8,
    [string]$Out = "",
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "Unity Project"
$exe = Join-Path $project "Builds\Windows\Nemesis.exe"
if ($Out -eq "") { $Out = Join-Path $repo "eval\unity" }
New-Item -ItemType Directory -Force $Out | Out-Null

if (-not $SkipBuild) {
    Write-Host "Building player..."
    $p = Start-Process -FilePath $Unity -ArgumentList @("-batchmode", "-quit", "-nographics",
        "-projectPath", "`"$project`"", "-executeMethod", "NemesisBuild.BuildWindows",
        "-logFile", "`"$(Join-Path $Out 'build.log')`"") -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "Unity build failed, see $Out\build.log" }
}

$runs = @()
foreach ($preset in $Presets) {
    foreach ($seed in $Seeds) {
        $runs += @{ name = "${Tag}_${preset}_s$seed"; args = @("-boss", "rl", "-playerPreset", $preset); seed = $seed }
        if ($Baselines) {
            $runs += @{ name = "random_${preset}_s$seed"; args = @("-boss", "rl", "-policy", "random", "-playerPreset", $preset); seed = $seed }
            $runs += @{ name = "attack_${preset}_s$seed"; args = @("-boss", "rl", "-policy", "attack", "-playerPreset", $preset); seed = $seed }
            $runs += @{ name = "fsm_${preset}_s$seed"; args = @("-boss", "fsm", "-playerPreset", $preset); seed = $seed }
        }
    }
}

Write-Host "Running $($runs.Count) evaluations, $Parallel at a time..."
$active = @()
foreach ($run in $runs) {
    while (($active | Where-Object { -not $_.HasExited }).Count -ge $Parallel) { Start-Sleep -Milliseconds 500 }
    # Start-Process does not quote arguments itself, and the repo path may contain spaces.
    $a = @("-batchmode", "-nographics", "-nemesisEval", $Episodes, "-seed", $run.seed,
           "-timeScale", $TimeScale, "-out", "`"$(Join-Path $Out $run.name)`"",
           "-logFile", "`"$(Join-Path $Out "$($run.name).log")`"") + $run.args
    $active += Start-Process -FilePath $exe -ArgumentList $a -PassThru -WindowStyle Hidden
}
$active | Wait-Process
Get-ChildItem $Out -Filter *.log | Where-Object { $_.Name -ne "build.log" } | Remove-Item
python (Join-Path $PSScriptRoot "summarize_unity_eval.py") $Out
