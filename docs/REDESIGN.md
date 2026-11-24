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
