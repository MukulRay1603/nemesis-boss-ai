# One-shot overnight job: train a new boss policy in the real game, then evaluate it.
# Registered as a one-time Windows scheduled task (see training/README.md). It:
#   1. refuses to start outside 04:00-08:00 (so a late start never lands on daytime use)
#   2. trains with training/train_ppo.py (12 headless game instances, CPU, capped at 2.5 h)
#   3. copies the new ONNX into Unity Project/Assets/Models/BossAgent.onnx (v3 stays in results/boss_v3)
#   4. rebuilds and runs the 2 x 3 x 200-episode evaluation into eval/unity/
#   5. unregisters its own scheduled task
# Everything is logged to results/overnight_<date>.log.

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $repo ("results\overnight_" + (Get-Date -Format "yyyyMMdd_HHmm") + ".log")
Start-Transcript -Path $log | Out-Null

try {
    $hour = (Get-Date).Hour
    if ($hour -lt 4 -or $hour -ge 8) {
        Write-Host "It is $(Get-Date -Format HH:mm), outside the 04:00-08:00 window. Not starting."
        return
    }

    $py = Join-Path (Split-Path -Parent $repo) "nemesis-env\Scripts\python.exe"
    Write-Host "Training started $(Get-Date -Format s)"
    & $py (Join-Path $repo "training\train_ppo.py") --envs 12 --total-steps 3000000 --max-hours 2.5 --run-id ppo_v4 2>&1 |
        ForEach-Object { "$_" }
    Write-Host "Training finished $(Get-Date -Format s)"

    $model = Join-Path $repo "results\ppo_v4\BossAgent.onnx"
    if (Test-Path $model) {
        Copy-Item $model (Join-Path $repo "Unity Project\Assets\Models\BossAgent.onnx") -Force
        Write-Host "Evaluating v4 $(Get-Date -Format s)"
        & (Join-Path $repo "eval\run_unity_eval.ps1") -Tag v4
        Write-Host "Evaluation finished $(Get-Date -Format s)"
    } else {
        Write-Host "No trained model found, skipping evaluation."
    }
}
finally {
    Unregister-ScheduledTask -TaskName "NemesisOvernightTraining" -Confirm:$false -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
}
