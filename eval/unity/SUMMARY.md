| Run | Episodes | Boss wins | Player wins | Timeouts | Boss win rate (95% CI) | Mean fight (s) | Boss hit rate |
|---|---|---|---|---|---|---|---|
| ablate_attack (seeds 1,2,3) | 600 | 496 | 0 | 104 | 82.7% (79.4% to 85.5%) | 76.5 | 23.7% |
| ablate_random (seeds 1,2,3) | 600 | 598 | 0 | 2 | 99.7% (98.8% to 99.9%) | 55.3 | 25.7% |
| fsm_dmg10 (seeds 1,2,3) | 600 | 600 | 0 | 0 | 100.0% (99.4% to 100.0%) | 15.3 | n/a |
| fsm (seeds 1,2,3) | 600 | 600 | 0 | 0 | 100.0% (99.4% to 100.0%) | 19.6 | n/a |
| rl (seeds 1,2,3) | 600 | 510 | 0 | 90 | 85.0% (81.9% to 87.6%) | 74.3 | 23.8% |

ablate_attack_s1: inference on CPU mean 0.400 ms, p50 0.370, p95 0.571, p99 0.873, max 5.319 (n=8098)
ablate_attack_s1: action mix idle/attack/dodge/special = 0.0% / 100.0% / 0.0% / 0.0%
ablate_attack_s2: inference on CPU mean 0.397 ms, p50 0.369, p95 0.561, p99 0.801, max 9.976 (n=7973)
ablate_attack_s2: action mix idle/attack/dodge/special = 0.0% / 100.0% / 0.0% / 0.0%
ablate_attack_s3: inference on CPU mean 0.396 ms, p50 0.370, p95 0.566, p99 0.796, max 3.601 (n=8131)
ablate_attack_s3: action mix idle/attack/dodge/special = 0.0% / 100.0% / 0.0% / 0.0%
ablate_random_s1: inference on CPU mean 0.385 ms, p50 0.363, p95 0.516, p99 0.712, max 9.026 (n=8940)
ablate_random_s1: action mix idle/attack/dodge/special = 25.4% / 25.2% / 24.7% / 24.7%
ablate_random_s2: inference on CPU mean 0.377 ms, p50 0.356, p95 0.510, p99 0.711, max 3.756 (n=8682)
ablate_random_s2: action mix idle/attack/dodge/special = 25.3% / 24.6% / 24.9% / 25.2%
ablate_random_s3: inference on CPU mean 0.376 ms, p50 0.355, p95 0.506, p99 0.711, max 11.221 (n=8661)
ablate_random_s3: action mix idle/attack/dodge/special = 25.2% / 24.9% / 24.7% / 25.2%
rl_s1: inference on CPU mean 0.379 ms, p50 0.345, p95 0.581, p99 0.923, max 5.740 (n=8204)
rl_s1: action mix idle/attack/dodge/special = 2.8% / 89.5% / 5.6% / 2.1%
rl_s2: inference on CPU mean 0.377 ms, p50 0.345, p95 0.585, p99 0.871, max 3.889 (n=8445)
rl_s2: action mix idle/attack/dodge/special = 2.8% / 88.8% / 6.2% / 2.2%
rl_s3: inference on CPU mean 0.377 ms, p50 0.345, p95 0.584, p99 0.871, max 3.215 (n=8236)
rl_s3: action mix idle/attack/dodge/special = 2.6% / 88.2% / 6.4% / 2.8%
