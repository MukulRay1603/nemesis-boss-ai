# Audit of the original project (state at commit 0bff669, April 2026)

Reviewed 2026-11-23, before any changes. Kept as written so the starting point is never lost.

## What the project was

Three systems in one Unity 2022.3 project:

1. **RL combat brain.** A PPO policy trained with ML-Agents (0.30 / package 2.3.0-exp.3), exported to ONNX and run at runtime with Unity Sentis 1.2. 14 observations, 4 discrete actions (idle, attack, dodge, special). Three training runs in `results/` (`boss_curriculum01`, `boss_final1`, `boss_v3`, the last one shipped).
2. **Phase escalation.** Below 66% / 33% HP the boss speeds up (x1.3, x1.6) and its recovery shortens (0.85 / 0.65 / 0.45 s). Added on 2026-04-02 together with a readable state machine: Idle, Telegraph, Attack, Recovery, Dodge.
3. **LLM taunt layer.** `llm_layer/state_bridge.py`: a TCP server that receives game state every 6 s, asks Groq (`llama-3.3-70b-versatile`) for a short taunt and filters it with a NeMo Guardrails config. Optional; the game runs without it.

Plus two side pieces:

- `train_rllib.py`: a Ray RLlib distributed PPO benchmark (1, 2, 4, 8 workers; 2.62x speedup at 8). It trains a simplified gym environment (100 HP, 300-step episodes), not the shipped policy.
- `eval/run_eval.py`: a Python re-implementation of the fight used to compute win rates.

## Where it was heading

Breadth: one project touching many tools (ML-Agents, Sentis, RLlib, MLflow, Groq, NeMo Guardrails), with each one shown working on its own. The commit history reads as a fast build sprint (2026-03-30 to 2026-04-02) followed by documentation and cleanup (2026-04-20 to 2026-04-22).

## What was good

- **End-to-end engine integration.** Training, ONNX export and Sentis inference inside Unity is the hard, transferable part, and it worked.
- **Game design instinct.** The telegraph / attack / recovery state machine makes the boss readable and punishable, which is how real boss fights are designed. The phase system adds escalation.
- **Reproducibility effort.** Training configs, checkpoints and run logs were kept in the repo.

## What was not working

1. **The shipped policy was trained in a different game.** Training used the earlier agent (preserved, commented out, in `a13f261`): one action per physics step, damage on every step in range, 100 HP each, 15 s episodes, no state machine. The shipped game queries the policy once per state-machine cycle, with 300 / 100 HP and 90 s episodes, and observation 12 is time / 90 instead of time / 15. Measured result (see RESULTS.md): the shipped policy performs about the same as "always attack".
2. **The Python evaluation did not match Unity.** Different constants (boss damage 12 vs 10, player damage 4 vs 10, 60 s vs 90 s episodes, player HP 250 vs 100, the FSM baseline got phase speed-ups). Its committed `eval/results.csv` said the RL boss won 0 of 200 (all timeouts); the real build gives about 85%.
3. **The RLlib benchmark measured a different environment.** The 2.62x speedup is real, but for a toy environment, not the shipped boss.
4. **Unequal baseline.** The scripted FSM boss had 8 damage against the RL boss's 10.
5. **Special attack bug.** In phase 3 the action mask allowed the special at any stamina, but the attack only dealt damage with stamina of 20 or more.
6. **README numbers did not match the scene** (for example player HP 250 vs 100 in the scene, 20 Hz vs the actual 50 Hz physics step).
7. **Camera and presentation.** The camera sat behind the player at chest height, so the player hid the boss. Player and phase-1 boss were both white.
8. **Fresh-clone gap.** The scene's post-processing profile was gitignored, so a clean clone had no post-processing.
9. **Secrets housekeeping.** `ROTATION_CHECKLIST.md` lists three API keys pending rotation. `.env` is gitignored and was never committed.

## Overall

Good instincts and real integration work, but the claims had outrun the evidence: the headline AI had never been measured in the actual game, and the one evaluation that existed was measuring something else. The restructure focuses on depth: one question, measured properly in the real build.
