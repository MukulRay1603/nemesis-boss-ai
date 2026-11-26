# Redesign plan (proposed 2026-11-24, awaiting Ray's go-ahead)

Goal: a project that impresses game AI teams on two fronts at once: a distinctive look with a "wow" moment, and real depth that shows how game AI is built, tested and tuned. Cut anything that is decoration without purpose.

## 1. Art direction: build on Ray's original white look

Start from the legacy build's look (captured first, see gallery) rather than the generic dark neon of v2 to v4.
- High-key white arena and white boss, soft daylight, strong readable shadows.
- One danger colour only (red) for wind-ups, attack ranges and the special. Everything else neutral, so threat is instantly readable.
- Player in near-black, so the two silhouettes never blend.
- Characters built from simple sculpted shapes (head, torso, cape or armour plates) instead of bare capsules, no external assets.
- Motion with weight: anticipation on wind-ups, hit-stop on impact, a short slow-motion beat on phase changes and the final blow. Visual only; the simulation runs in fixed steps, so results do not change.
- HUD as editorial type on the white scene: dark ink text, no boxes.

## 2. Depth features (each is a testing or tuning tool, not decoration)

| Feature | What it shows | Effort |
|---|---|---|
| AI Lab split-screen | Four arenas side by side (v3, v4, v5, random) fighting live with running win counters: the evaluation made visible | 1 to 2 days |
| Replay and decision scrubber | Replay any seeded fight and scrub a timeline; each decision shows the policy's probabilities and the opponent profile. A debugging tool studios use daily | 1 to 2 days |
| Position and hit heatmaps | Where the boss lands hits, where it gets punished, per model | half a day |
| Difficulty dial (needs Ray's OK, training) | One policy, a designer-facing slider; win rate and fight length follow it on a chart | 2 to 3 days with overnight training |
| Experience metrics | Fight length, close calls, telegraph-before-every-hit, beyond win rate | half a day |
| Behaviour regression check | A test that fails if a new model's win rate or fight length leaves its band | half a day |

## 2b. AI direction after v4/v5 (researched 2026-11-26, awaiting Ray)

Ray's point: nerfing the special and retraining only swaps one trick for another, and the boss feels scripted again. Agreed. The root cause is not the move list but the opponent: both models trained against one scripted bot that never punishes the special, and any optimiser exploits a fixed opponent. What studios do instead:

| Option | Precedent | What it gives Nemesis |
|---|---|---|
| A. Co-trained hunter (self-play league) | AlphaStar league; Ubisoft For Honor bots trained against past versions of themselves; NCSOFT Blade & Soul (62% vs pros); Naruto Mobile "Shukai" heterogeneous league | The hunter is also an RL agent (it drives the existing virtual inputs). It learns to bait and sidestep the special, so the boss has to mix up. Snapshots of both sides form a league. No balance change: if a learning hunter cannot counter the special, that proves it is a balance bug. Demo: an arms-race timeline, generation by generation |
| B. Persona-conditioned boss | EA SEED MultiGAIL (one agent that switches and blends personas); quality-diversity / MAP-Elites play-style work | One network with a style input (aggressive, patient, trickster) and a behaviour map showing each style lands in a different region (gap kept, special share, dodge timing) |
| C. Learns from real players | Sony AI BeTAIL (imitation from human racing) | Opt-in recordings of web-build fights, behaviour-cloned into "ghost hunters" that join the league, so the boss trains against human habits |
| D. Designer intent in the reward | GT Sophy: racing etiquette encoded in the reward | Fairness terms (telegraph before every hit, a small variety bonus) as a dial, measured, not hard rules |

Proposed order: A first (it is the headline and answers D-019 without a manual nerf), then B with the behaviour map in the AI Lab, then C once the web build is hosted. D only as a tuning dial.

Cost: A is about 1 to 2 days of code (hunter action channel in `TrainingBridge`, two policies and an opponent pool in `train_ppo.py`) plus overnight CPU runs, only when Ray is not gaming.

## 3. Showcase

- 90 s video: legacy look to final look, the AI card explained, AI Lab, difficulty dial, live link.
- Write-up page with three charts; README leads with the result and a playable link.
- Web build hosted (GitHub Pages or itch.io, Ray to choose).

## Order

1. Capture the legacy build (needs a free GPU lock).
2. Overnight results in; Ray picks the model.
3. Art direction pass, then AI Lab and replay scrubber.
4. Difficulty dial and experience metrics (with Ray's OK on the training objective).
5. Showcase assets.
