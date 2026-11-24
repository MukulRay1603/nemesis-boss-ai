# Measured results

Only measured numbers. Each row says how it was measured and where the raw data is. Re-measurements are added as new rows with their date; old rows are not edited.

Hardware for all rows unless noted: Intel i9-10900K (10 cores / 20 threads), RTX 3080 10 GB, 32 GB RAM, Windows 11. Unity 2022.3.62f3, Sentis 1.2.0-exp.2.

## Win rates (Unity build, headless)

Method: `EvalRunner` (`-nemesisEval`) in the Windows player, `-batchmode -nographics`, Sentis CPU backend, time scale 100, fights capped at 90 s, opponent is the scripted `PlayerController` bot. Seeds 1, 2, 3 x 200 episodes = 600 fights per row. 95% Wilson intervals. Summaries via `eval/summarize_unity_eval.py` or `eval/update_results.py`.

### Before the bug fixes (2026-11-23, build at 73c12f6), default bot only. Data: `eval/unity_pre_fix/`

| Boss | Fights | Boss wins | Player wins | Timeouts | Boss win rate (95% CI) | Mean fight |
|---|---|---|---|---|---|---|
| v3 PPO policy | 600 | 510 | 0 | 90 | 85.0% (81.9 to 87.6) | 74.3 s |
| Same body, always attack | 600 | 496 | 0 | 104 | 82.7% (79.4 to 85.5) | 76.5 s |
| Same body, uniform random | 600 | 598 | 0 | 2 | 99.7% (98.8 to 99.9) | 55.3 s |
| FSM boss, 8 damage (scene default then) | 600 | 600 | 0 | 0 | 100.0% (99.4 to 100.0) | 19.6 s |
| FSM boss, 10 damage | 600 | 600 | 0 | 0 | 100.0% (99.4 to 100.0) | 15.3 s |

v3 action mix (default bot): attack 88.2 to 89.5%, dodge 5.6 to 6.4%, special 2.1 to 2.8%, idle 2.6 to 2.8% (per seed). Boss hit rate per swing 23.8%.

### After the bug fixes (2026-11-23, build at b775261), both opponents. Data: `eval/unity/`

"Hard" bot = default bot with move speed x1.3, attack cooldown x0.75, preferred gap x1.2, retreat speed x1.2 (`EvalRunner.ApplyHardPlayer`).

| Boss | vs default bot | vs hard bot |
|---|---|---|
| v3 PPO policy | 85.0% (81.9 to 87.6), 74.3 s | 82.3% (79.1 to 85.2), 77.9 s, 1 player win |
| Same body, always attack | 82.7% (79.4 to 85.5), 76.5 s | 86.7% (83.7 to 89.2), 76.7 s, 2 player wins |
| Same body, uniform random | 99.7% (98.8 to 99.9), 55.3 s | 70.2% (66.4 to 73.7), 76.9 s |
| FSM boss, 10 damage | 100.0% (99.4 to 100.0), 15.3 s | 100.0% (99.4 to 100.0), 16.6 s |

Every non-win is a timeout except where player wins are listed.

### v4: retrained in the shipped game (2026-11-24, eval finished 05:20). Data: `eval/unity/v4_*`, model `results/ppo_v4/`

Training: custom PPO, 12 instances, 1,327,104 decisions in 75 min (time cap), 14 inputs, randomised opponent.

| Boss | vs default bot | vs hard bot |
|---|---|---|
| v4 PPO policy | 100.0% (99.4 to 100.0), 27 s avg | 100.0% (99.4 to 100.0), 27 s avg |

Action mix: default bot idle 0.0 to 0.2%, attack 0.0%, dodge 12.6 to 13.2%, special 86.8 to 87.2%; hard bot dodge 6.3 to 6.6%, special 93.4 to 93.6% (per seed).

Reading: v4 beats every baseline in the same boss body and finishes fights 2.7x faster than v3, but by a degenerate strategy: special whenever stamina allows (20 damage, longer lunge, 20 stamina which refills in 5 s), dodge while waiting, never the normal attack. RL exposed that the special attack is dominant, which is a game balance finding (see D-019).

### v5: adaptive, 18 inputs (2026-11-24, eval finished 06:37). Data: `eval/unity/v5_*`, model `results/ppo_v5/`

Training: same setup as v4 plus the 4 opponent-profile inputs (PlayerProfile: aggression, evasion, range, pressure), 1,419,264 decisions in 75 min (time cap).

| Boss | vs default bot | vs hard bot |
|---|---|---|
| v5 PPO policy | 100.0% (99.4 to 100.0), 26.7 s avg (seeds 26.46 / 27.06 / 26.54) | 100.0% (99.4 to 100.0), 27.8 s avg (27.78 / 28.28 / 27.20) |

Action mix: default bot idle 0.0 to 0.2%, attack 0.0 to 0.1%, dodge 12.9 to 13.6%, special 86.2 to 86.9%; hard bot dodge 6.8 to 7.3%, special 92.5 to 93.2% (per seed).

Reading: same strategy as v4. The profile inputs did not produce visible adaptation against these two bots; the special-attack imbalance (D-019) dominates. A fair test of adaptation needs the balance fixed first and opponents with clearly different styles.

## Inference time per decision

Method: stopwatch around tensor setup, `Execute` and readback in `BossAgent.QueryPolicy`, every decision, first 10 skipped.

| Date | Setup | n | mean | p50 | p95 | p99 |
|---|---|---|---|---|---|---|
| 2026-11-23 | v3, Sentis CPU, headless, 8 eval processes in parallel, seed 1 | 8,204 | 0.379 ms | 0.345 ms | 0.581 ms | 0.923 ms |
| 2026-11-23 | same, seed 2 | 8,445 | 0.377 ms | 0.345 ms | 0.585 ms | 0.871 ms |
| 2026-11-23 | same, seed 3 | 8,236 | 0.377 ms | 0.345 ms | 0.584 ms | 0.871 ms |
| 2026-11-23 | v3, Sentis GPUCompute, windowed 1080p, GPU shared with another workload | 19 | 1.90 ms | 1.45 ms | 3.58 ms | 3.58 ms |
| 2026-11-23 | PPO export (smoke model), Sentis CPU, headless, 5 episodes | 230 | 0.20 ms | 0.19 ms | 0.30 ms | 0.39 ms |
| 2026-11-23 | 18-input export (v5 smoke), Sentis CPU, headless, 5 episodes | 220 | 0.47 ms | 0.38 ms | 0.93 ms | 1.43 ms |

| 2026-11-24 | v4, Sentis CPU, headless, overnight eval (3 seeds x 2 bots) | 17,917 | 0.61 to 0.64 ms | 0.44 to 0.45 ms | 1.38 to 1.51 ms | 3.4 to 4.0 ms |
| 2026-11-24 | v5 (18 inputs), same | 17,960 | 0.27 to 0.28 ms | 0.26 ms | 0.37 to 0.38 ms | 0.46 to 0.51 ms |

The GPUCompute row is a small sample on a shared GPU; treat it as indicative only. The v4 and v5 rows are per-seed ranges from the overnight log; the two networks differ only in input size (14 vs 18), so the v4/v5 gap most likely comes from machine load at the time, not the model. Not investigated.

## Frame time

Not measured cleanly yet. The only attempt (2026-11-23, windowed 1080p, before the visual overhaul) ran while another workload held the GPU at 50 to 96% utilisation, so it is not reported. Command to measure: see README.

## Determinism

Seeded runs (`-seed N`, which seeds the bot, the FSM boss and the policy's sampling) give byte-identical episode CSVs:
- across repeated runs and across time scales 25 and 100 (2026-11-23)
- with every visual layer enabled in the 1080p recording run (seed 21: BOSS_WIN at 74.84 s, 80 HP left, both headless and recorded)
- after every code change since: first 20 episodes of v3 seed 1 hash `8b46d492f4f490addfa5e10552b99c6a`, last re-checked 2026-11-24 03:46

## Training throughput (custom PPO over TCP, CPU)

| Date | Setup | Throughput |
|---|---|---|
| 2026-11-23 | 2 game instances, smoke test | 1,024 decisions in about 6 s |
| 2026-11-23 | 12 instances, dry run | 768 decisions, start to shutdown 9 s |
| 2026-11-24 | 12 instances, v4 overnight, first 11.5 min | 239,616 decisions (about 20,800 per minute) |
| 2026-11-24 | 12 instances, v4 full run (75 min cap) | 1,327,104 decisions (about 17,700 per minute) |
| 2026-11-24 | 12 instances, v5 full run (75 min cap) | 1,419,264 decisions (about 18,900 per minute) |

## Builds

| Date | Build | Size |
|---|---|---|
| 2026-11-23 | WebGL (gzip, decompression fallback) | about 14 MB (data 5.9 MB, wasm 8.6 MB) |

## Original project numbers (from its README / logs, not re-measured)

| Claim | Source | Status |
|---|---|---|
| ML-Agents final mean reward: boss_final1 16.66, boss_v3 14.93, curriculum01 5.53 | `results/*/run_logs` | From training logs, not re-measured |
| RLlib speedup 1.52x / 2.17x / 2.62x at 2 / 4 / 8 workers, 52k steps | `eval/rllib_benchmark.csv` | Real, but for the simplified gym env, not the shipped policy |
| Python eval: RL boss 0/200 wins | `eval/results.csv` | Superseded: the Python sim does not match the Unity game |
