# Headless evaluation of the Nemesis boss inside the real Unity build.
#
# Builds the Windows player (unless -SkipBuild), then runs N episodes per seed for
# the RL boss, the scripted FSM baseline, and two ablations that keep the RL boss's
# body but replace its policy (random / always-attack), against the scripted player bot,
# with no window and no GPU (-nographics, Sentis on CPU). Results land in eval/unity/.
#
#   .\eval\run_unity_eval.ps1                      # 200 episodes x seeds 1,2,3
#   .\eval\run_unity_eval.ps1 -Episodes 50 -Seeds 1 -SkipBuild
#
# Outcomes do not depend on -TimeScale (all game logic runs in FixedUpdate).

param(
    [int]$Episodes = 200,
    [int[]]$Seeds = @(1, 2, 3),
    [float]$TimeScale = 100,
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "Unity Project"
$exe = Join-Path $project "Builds\Windows\Nemesis.exe"
$out = Join-Path $repo "eval\unity"
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipBuild) {
    Write-Host "Building player..."
    & $Unity -batchmode -quit -nographics -projectPath $project `
        -executeMethod NemesisBuild.BuildWindows -logFile (Join-Path $out "build.log") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unity build failed, see $out\build.log" }
}

$jobs = @()
foreach ($seed in $Seeds) {
    foreach ($run in @(
        @{ name = "rl_s$seed";       args = @("-boss", "rl") },
        @{ name = "fsm_s$seed";      args = @("-boss", "fsm") },
        @{ name = "fsm_dmg10_s$seed"; args = @("-boss", "fsm", "-fsmDamage", "10") },
        @{ name = "ablate_random_s$seed"; args = @("-boss", "rl", "-policy", "random") },
        @{ name = "ablate_attack_s$seed"; args = @("-boss", "rl", "-policy", "attack") }
    )) {
        $a = @("-batchmode", "-nographics", "-nemesisEval", $Episodes, "-seed", $seed,
               "-timeScale", $TimeScale, "-out", "`"$(Join-Path $out $run.name)`"",
               "-logFile", "`"$(Join-Path $out "$($run.name).log")`"") + $run.args
        # (Start-Process does not quote arguments itself, and the repo path may contain spaces.)
        $jobs += Start-Process -FilePath $exe -ArgumentList $a -PassThru -WindowStyle Hidden
    }
}
Write-Host "Running $($jobs.Count) evaluations..."
$jobs | Wait-Process
python (Join-Path $PSScriptRoot "summarize_unity_eval.py") $out
