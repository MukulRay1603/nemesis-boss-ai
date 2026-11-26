# Version history

Release versions (1.0, 2.0, ...) are separate from policy names (v3, v4, v5: the trained models). What each version of Nemesis was, what changed, and what was learned. The original build is kept unchanged in git: tag `v1.0-legacy` points at commit 0bff669. Details per change are in [BUILD_NOTES.md](BUILD_NOTES.md), numbers in [RESULTS.md](RESULTS.md), decisions in [DECISIONS.md](DECISIONS.md).

| Version | Dates | Git | Look | Boss AI | Evidence |
|---|---|---|---|---|---|
| 1.0 legacy | 2026-03-30 to 2026-04-22 | `v1.0-legacy` (0bff669) | White arena, white boss, chest-height camera | PPO (ML-Agents), 14 inputs, 4 actions, trained in an earlier version of the game (v3 policy) | Python re-implementation reported 0/200 boss wins; never measured in the real game |
| 2.0 measured | 2026-11-23 | 73c12f6 to b775261 | Third-person camera, effects, broadcast HUD | Same v3 policy, bugs fixed (phase-3 special, FSM damage) | First real measurement: 85.0% vs default bot, no better than "always attack" (82.7%) |
| 2.1 playable | 2026-11-23 to 24 | 1a762e8 to 7eef102 | Web build, touch controls, compact phone layout | v3 policy; player can fight it | WebGL build about 14 MB |
| 2.2 retrained (policies v4, v5) | 2026-11-24 | 3eba2d2, 8428390, 22229b6 | unchanged | Custom PPO in the shipped game; v5 adds 4 opponent-profile inputs | 100% vs both bots in about 27 s, but by special-attack spam (86 to 94% of decisions) |
| 3.0 co-trained hunter | planned | | Art direction from REDESIGN.md | Boss and hunter both learn (self-play league) | to be measured |

## What each step taught

- **1.0 to 2.0: measure in the real build.** The legacy README and Python evaluation described a different game from the one that shipped. A seeded, headless evaluation inside the actual Unity build was the first trustworthy number, and it showed the policy was no better than a one-line baseline.
- **2.0: baselines in the same body.** Comparing the RL boss to "always attack" and "random" in the same body isolates the brain. Comparing it to the FSM boss alone hid the problem.
- **2.2, policy v4: an optimiser finds the exploit.** Retraining in the shipped game gave a perfect win rate through one move. RL works as an automated playtester: it showed the special attack is dominant.
- **2.2, policy v5: adaptation needs varied opponents.** Profile inputs changed nothing, because the bots it faced did not differ in ways that matter.
- **3.0 (planned): the opponent must learn too.** Hand-nerfing the special would only swap one trick for another. A hunter that learns to punish the boss forces variety without scripting it (REDESIGN.md section 2b).

## Authorship

1.0 is Ray's original work. From 2.0 on, the code was written with an AI coding assistant, directed and reviewed by Ray (see BUILD_NOTES.md, "Authorship").

## Media

| Version | Media |
|---|---|
| 1.0 legacy | `docs/gallery/legacy.gif` (pending: captured from the original `Nemesis-ai.exe`, unmodified) |
| 2.0 to 2.2 | stills in [gallery/](gallery/README.md); clips in the gitignored `media/` folder |
