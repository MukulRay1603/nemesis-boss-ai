# Decision log

Each entry: what was decided, the options, the choice, and who chose. "Ray" = product/design owner. "Assistant" = implementation choice made during a session, open to Ray's veto.

| ID | Date | Decision | Options considered | Choice | By |
|---|---|---|---|---|---|
| D-001 | 2026-11-23 | How to measure win rate | Fix the Python sim; measure in the Unity build | Headless evaluation inside the real Unity build (`EvalRunner`), seeded and deterministic | Assistant |
| D-002 | 2026-11-23 | Baselines | FSM boss only; ablations in the same boss body | Both: FSM boss plus "always attack" and "uniform random" policies in the RL boss's body, so only the brain changes | Assistant |
| D-003 | 2026-11-23 | Recording method | Unity Recorder; OBS; built-in offscreen capture | Offscreen render to texture piped to ffmpeg at a fixed frame rate (no window, frame-perfect, same outcome as headless) | Assistant |
| D-004 | 2026-11-23 | Core AI changes | Change behaviour freely; propose only | Propose only; Ray decides anything that changes AI behaviour | Ray (standing rule) |
| D-005 | 2026-11-23 | First UI direction | Original debug panel; minimal; cards | Minimal hairline HUD | Ray |
| D-006 | 2026-11-23 | HUD design | Broadcast versus; glass cards; bold action game | Broadcast versus with a decision timeline | Ray |
| D-007 | 2026-11-23 | Upgrades to take on | Retrain; playable + web; fix AI bugs; results page + trailer | All four; retraining overnight after 04:00 | Ray |
| D-008 | 2026-11-23 | Training stack | ML-Agents in the Unity project; custom bridge | Custom PPO over TCP (`TrainingBridge` + `training/train_ppo.py`). The only cached ML-Agents (2.0.2) needs Barracuda, whose `.onnx` importer conflicts with Sentis; custom keeps the exact shipped game and the same ONNX interface | Assistant |
| D-009 | 2026-11-23 | Reward | Win/loss only; HP-based shaping | Per decision: damage dealt (fraction of player max HP) minus damage taken (fraction of boss max HP); terminal +1 win, -1 loss, -0.5 timeout | Assistant (open to Ray's revision) |
| D-010 | 2026-11-23 | Opponent during training | Fixed default bot; randomised bot | Randomised per fight around the scene bot: speed x0.85 to 1.4, gap x0.85 to 1.3, cooldown x0.7 to 1.15, retreat x0.9 to 1.3 | Assistant |
| D-011 | 2026-11-23 | Harder evaluation opponent | Default bot only; add a hard preset | Add "hard" preset (speed x1.3, cooldown x0.75, gap x1.2, retreat x1.2), because a random policy already beats the default bot 99.7% | Assistant |
| D-012 | 2026-11-23 | Bug fixes | Leave; fix | Fix: phase-3 special lands at low stamina; FSM baseline damage 8 -> 10. Time-input scaling resolved by retraining at /90 | Ray approved |
| D-013 | 2026-11-23 | WebGL module install | Skip web; install | Install (large download via Unity Hub) | Ray |
| D-014 | 2026-11-23 | Web graphics default | Use desktop Auto thresholds; fixed web default | Medium on desktop browsers, Low on phones (browsers do not report VRAM reliably) | Assistant |
| D-015 | 2026-11-23 | Adaptive boss | Not now; next night; tonight | 4 opponent-profile inputs (aggression, evasion, range, pressure). Trained tonight as v5 after a clean v4 | Ray ("tonight's run after 4am"), two-run plan by Assistant |
| D-016 | 2026-11-24 | Mobile layout | Same as desktop; compact | Compact: fewer details on the AI card, scaled versus card, FPS off, shorter page | Ray |
| D-017 | 2026-11-24 | Hosting | GitHub Pages; itch.io | Pending | Ray |
| D-018 | 2026-11-24 | Which model ships | v3; v4; v5 | Pending the overnight results | Ray |
| D-019 | 2026-11-24 | Special attack is dominant (found by v4) | Keep; raise its cost (stamina or cooldown); lower its damage; reward shaping for variety; co-trained RL hunter that learns to punish it (REDESIGN 2b) | Pending. Ray (2026-11-26): manual nerfs make the boss feel scripted; asked for research. Recommended: co-trained hunter | Ray |
