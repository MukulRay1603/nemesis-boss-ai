# Build notes

Chronological log. Newest entry at the bottom. Each entry: what changed, why, how it was verified, commits, open items. Decisions are in [DECISIONS.md](DECISIONS.md) (D-0NN), numbers in [RESULTS.md](RESULTS.md).

---

## Phase 0: original build (2026-03-30 to 2026-04-22), by Ray

Reconstructed from git history.

| Date | Commit | Change |
|---|---|---|
| 2026-03-30 | 3a9d99d, 8ce3b42 | Repo created. Environment: Python 3.10.8, ML-Agents 0.30.0, PyTorch 2.0.1+cu117 |
| 2026-03-30 | 31157e4 | Observation space, dense reward shaping, PPO config |
| 2026-03-30 | 141be35, f133ec1 | First 300k-step PPO run, ONNX export |
| 2026-03-31 | 017880d | PlayerController with Rigidbody, curriculum config, player velocity observations |
| 2026-04-01 | 8cf7887 | `boss_final1` 300k-step run |
| 2026-04-01 | 3d9f613 | Sentis runtime inference working |
| 2026-04-02 | a13f261 | Unity project added; old ML-Agents training agent kept commented out in `BossAgent.cs` |
| 2026-04-02 | 70bbd4b | RLlib distributed PPO benchmark (1 to 8 workers) |
| 2026-04-02 | 4e9b788, 6e70698 | Boss state machine (telegraph / attack / recovery / dodge), player reaction AI, phase system |
| 2026-04-20 to 22 | e651ad3, 0641573, d957480, 0bff669 | README rewrite, repo cleanup, gitignore hardening |

The state at 0bff669 is reviewed in [AUDIT.md](AUDIT.md).

---

## 2026-11-23, session 1: audit, evaluation, presentation (AI-assisted)

**Audit.** Project opens and compiles in Unity 2022.3.62f3 with no fixes needed. Findings in AUDIT.md. Git author for this repo set locally to Mukul Rayana (noreply address), no attribution trailers.

**Evaluation harness** (73c12f6, D-001, D-002, D-003)
- `EvalRunner.cs`: `-nemesisEval N` runs seeded headless episodes in the real build and writes per-episode CSV plus summary JSON; `-nemesisFrames S` for frame time; `-nemesisRecord S` for offscreen 1080p capture piped to ffmpeg.
- `BossAgent.cs` instrumentation: per-decision timing, seeded policy sampling (`PolicySeed`), softmax readout, ablation policies (`-policy random|attack`).
- `EpisodeStats.cs` (episode results), `Editor/NemesisBuild.cs` (command-line builds).
- `eval/run_unity_eval.ps1`, `eval/summarize_unity_eval.py`.
- Verified: repeated runs and different time scales give byte-identical CSVs.
- First results: v3 85.0%, always attack 82.7%, random 99.7%, FSM 100% (default bot). The shipped policy is no better than always attacking.

**Presentation** (dac002d, 99083b7)
- `ArenaCamera.cs` third-person lock-on and broadcast cameras with hit shake; `VisualFX.cs` lighting, post-processing, procedural floor, phase-coloured trim, telegraph rings, trails, sparks, damage numbers; `FighterAnimator.cs` energy blades and procedural sword animation driven by the boss state machine.
- `QualityPresets.cs` Low / Medium / High / Ultra / Auto; `SettingsMenu.cs`.
- HUD redesigned to a minimal style (`UiKit.cs`), D-005.
- Recording: HUD composited over the post-processed scene with a two-pass (over black / over white) shader, because URP writes opaque alpha.
- Verified: seed 21 recorded fight matches the headless result exactly.

**Bug fixes, training, HUD, play mode** (b775261, D-006 to D-012)
- Fixes: phase-3 special now lands; FSM baseline damage 10.
- `TrainingBridge.cs` + `training/train_ppo.py`: PPO trained inside the shipped game over TCP, ONNX export with the same interface. Smoke and 12-instance dry runs pass; Sentis loads the export.
- Hard opponent preset; baseline matrix re-run on the fixed build (`eval/unity/`), pre-fix data kept in `eval/unity_pre_fix/`.
- Broadcast versus HUD with Boss AI card, decision timeline, per-phase mix.
- `GameFlow.cs` start screen (Fight / Watch); human control in `PlayerController` (WASD, attack, dash with i-frames). Verified bot path unchanged (same hash).
- Scheduled one-time task `NemesisOvernightTraining` for 2026-11-24 04:00.

**WebGL** (1a762e8, 504f32e, D-013, D-014)
- WebGL Build Support installed with Ray's OK. Sentis falls back to CPU without compute shaders; taunt bridge skipped in browsers; web Auto preset defaults to Medium.
- Custom page template (`Assets/WebGLTemplates/Nemesis`): full-width game, Watch vs Fight, how the AI works, results.
- `TouchControls.cs`: floating joystick, Attack, Dash, Menu. Verified in the browser pane with real touch events (fight start, movement, hit).

**Adaptive boss** (3eba2d2, D-015)
- `PlayerProfile.cs`: aggression, evasion, range, pressure; ~10 s moving averages, reset per fight, no randomness.
- `BossAgent` takes its input size from the model (14 or 18); `train_ppo.py --obs 14|18`.
- Verified: 18-input export trains, loads and runs in the game; v3 hash unchanged.
- Overnight script now trains and evaluates v4 (14 inputs) then v5 (18 inputs, only if before 06:15).

## 2026-11-24, session 1 continued (AI-assisted)

**Compact phone layout and fixes** (dfb4ca3, D-016)
- Phones: compact AI card, scaled versus card so Menu has room, FPS off by default, shorter page; v5 "How it reads you" row on desktop; camera framing for Fight mode and narrow screens; hover lift on start buttons.
- Windows and WebGL rebuilt before 04:00; v3 hash unchanged.
- Incident: a ~30 s browser-pane GPU test at about 03:40 ran while `F:\Genshin Edits\Assets\Scripts\gpu.lock` was held by "SPARK local model bench medium" (taken 03:32). The lock check output was misread; stopped once noticed. Rule going forward: check the lock folder's existence explicitly before any GPU work.

**Results tooling** (7eef102)
- `eval/update_results.py` regenerates the results tables in README and the web page between `RESULTS` markers. Verified idempotent.

**Documentation** (this commit)
- `docs/` added: this log, decisions, results, audit.

**Overnight job status at 04:11:** v4 training running (239,616 decisions in 11.5 min, in-training boss win rate 1.00, policy entropy 0.03). Expected: v4 eval ~05:30, v5 training to ~06:45, v5 eval ~07:00.

### Open items
1. Read `results/overnight_*.log`; run `python eval/update_results.py`; rewrite the README "Reading this honestly" paragraph to match.
2. Ray picks the shipped model (D-018); rebuild WebGL; re-record the clip (check gpu.lock first).
3. Hosting choice (D-017); results page charts and trailer.
4. Training throughput: asynchronous env stepping and an env-count benchmark (8 / 12 / 16 / 20).
5. Candidate next directions for Ray: difficulty dial, experience metrics (fight length, close calls), opt-in playtest stats from the web build, CI regression check on AI behaviour.
6. Frame time per preset, measured on an idle GPU.
7. Keys listed in `ROTATION_CHECKLIST.md` still to be rotated (Ray).

### Authorship
Phase 0 is Ray's original work. Everything from 2026-11-23 on was written with an AI coding assistant, directed and reviewed by Ray: the files named in the entries above, plus edits to `BossAgent.cs`, `PlayerController.cs`, `ScriptedBoss.cs`, `TauntDisplay.cs` and the scene (FSM damage value). Whether to state this publicly is Ray's decision.
