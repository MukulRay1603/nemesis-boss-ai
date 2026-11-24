# One-shot overnight job: train two boss policies in the real game and evaluate each.
#   v4  the 14 standard inputs (clean comparison with the shipped v3 and the baselines)
#   v5  the 14 inputs + a 4-value profile of how the opponent fights (adaptive boss)
# Registered as a one-time Windows scheduled task. It:
#   1. refuses to start outside 04:00-08:00 (so a late start never lands on daytime use)
#   2. trains v4 (12 headless game instances, CPU, capped at 75 min), then evaluates it
#   3. trains v5 the same way if it is still before 06:15, then evaluates it
#   4. leaves the last trained model in Unity Project/Assets/Models/BossAgent.onnx
#      (uncommitted; v3 stays in results/boss_v3 and git) and unregisters its own task
# Each evaluation is 2 opponents x 3 seeds x 200 episodes into eval/unity/.
# Everything is logged to results/overnight_<date>.log.

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $repo ("results\overnight_" + (Get-Date -Format "yyyyMMdd_HHmm") + ".log")
Start-Transcript -Path $log | Out-Null
$py = Join-Path (Split-Path -Parent $repo) "nemesis-env\Scripts\python.exe"
$modelPath = Join-Path $repo "Unity Project\Assets\Models\BossAgent.onnx"

function Train-And-Evaluate([string]$runId, [string]$tag, [int]$obs) {
    Write-Host "[$tag] training started $(Get-Date -Format s)"
    & $py (Join-Path $repo "training\train_ppo.py") --envs 12 --obs $obs --total-steps 3000000 `
        --max-hours 1.25 --run-id $runId 2>&1 | ForEach-Object { "$_" }
    Write-Host "[$tag] training finished $(Get-Date -Format s)"
    $model = Join-Path $repo "results\$runId\BossAgent.onnx"
    if (-not (Test-Path $model)) { Write-Host "[$tag] no model produced, skipping evaluation"; return }
    Copy-Item $model $modelPath -Force
    & (Join-Path $repo "eval\run_unity_eval.ps1") -Tag $tag      # rebuilds with the new model
    Write-Host "[$tag] evaluation finished $(Get-Date -Format s)"
}

try {
    $hour = (Get-Date).Hour
    if ($hour -lt 4 -or $hour -ge 8) {
        Write-Host "It is $(Get-Date -Format HH:mm), outside the 04:00-08:00 window. Not starting."
        return
    }
    Train-And-Evaluate "ppo_v4" "v4" 14
    if ((Get-Date) -lt (Get-Date -Hour 6 -Minute 15 -Second 0)) {
        Train-And-Evaluate "ppo_v5" "v5" 18
    } else {
        Write-Host "Past 06:15, skipping v5 so the job ends before 08:00."
    }
}
finally {
    Unregister-ScheduledTask -TaskName "NemesisOvernightTraining" -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Done $(Get-Date -Format s)"
    Stop-Transcript | Out-Null
}
