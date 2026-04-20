# Nemesis Boss AI

A reinforcement learning-driven boss combat AI for Unity. The boss learns adaptive attack strategies via PPO, executes them in real-time through Unity Sentis (ONNX inference), and delivers in-character dialogue through a live LLM pipeline.

---

## Overview

Nemesis combines three systems:

1. **RL Combat Brain** — A PPO policy (trained with ML-Agents, deployed via Unity Sentis) drives a 5-state combat FSM: Idle → Telegraph → Attack → Recovery → Dodge. The boss reads 14 observations per frame and outputs one of four actions.
2. **Phase Escalation** — As boss HP drops, speed and aggression scale up across three phases, culminating in an enraged state with visual feedback.
3. **LLM Taunt Layer** — A Python TCP bridge streams live game state to Groq (`llama-3.3-70b-versatile`), which generates in-character taunts filtered through NeMo Guardrails before being displayed in a Genshin-style dialogue panel.

---

## Architecture

```
Unity (20 Hz FixedUpdate)
├── BossAgent.cs      — Sentis ONNX inference + 5-state FSM
├── PlayerController.cs — Autonomous player AI (5-state FSM)
├── TauntDisplay.cs   — TCP bridge to Python + typewriter UI
└── ScriptedBoss.cs   — Rule-based FSM baseline (for evaluation)

TCP localhost:9999
└── llm_layer/state_bridge.py
    ├── Receives JSON game state from Unity every 6 s
    ├── Calls Groq API (llama-3.3-70b-versatile)
    ├── Applies NeMo Guardrails output filtering
    └── Returns taunt string to Unity
```

---

## Scene Layout

The combat arena is **20 × 20 units** with solid walls on all four sides.

| Object | Spawn Position | Notes |
|--------|---------------|-------|
| Boss | (0, 1, +6) | Capsule, Rigidbody, BossAgent |
| Player | (0, 1, −6) | Capsule, Rigidbody, PlayerController |
| Camera | (0, 1, −10) | Perspective, FOV 60, URP post-processing |

---

## Boss Combat Stats

| Parameter | Value |
|-----------|-------|
| HP | 300 |
| Attack damage | 10 |
| Attack range | 2.5 units |
| Move speed | 3.0 m/s |
| Dodge speed | 6.0 m/s |

### Phase System

| Phase | HP Threshold | Speed Multiplier | Recovery Time |
|-------|-------------|-----------------|---------------|
| 0 — Full strength | > 66 % | 1.0× | 0.85 s |
| 1 — Wounded | 33 – 66 % | 1.3× | 0.65 s |
| 2 — Enraged | < 33 % | 1.6× | 0.45 s |

Phase 2 triggers a scale-pulse visual effect. The special attack (action 3) is masked out unless stamina ≥ 20 or phase ≥ 2.

### State Machine Timings

| State | Duration | Behaviour |
|-------|----------|-----------|
| Idle | 0.35 s | Query policy, queue next action |
| Telegraph | 0.45 s | Slow lunge toward player (0.15× speed) |
| Attack | 0.30 s | Fast lunge (1.8×), deal damage once if in range |
| Recovery | 0.85 / 0.65 / 0.45 s | Slow retreat — **boss is vulnerable here** |
| Dodge | 0.50 s | Perpendicular sidestep (1.8× speed) |

---

## Observation Vector (14 floats)

| Index | Feature | Range |
|-------|---------|-------|
| 0 | Boss HP ratio | [0, 1] |
| 1 | Player HP ratio | [0, 1] |
| 2–3 | Boss X, Z (normalised) | [−1, 1] |
| 4–5 | Player X, Z (normalised) | [−1, 1] |
| 6 | Distance (normalised) | [0, 1] |
| 7 | Phase / 2 | 0 / 0.5 / 1 |
| 8 | Last action / 3 | [0, 1] |
| 9–10 | Player velocity X, Z | [−0.1, 0.1] |
| 11 | Stamina ratio | [0, 1] |
| 12 | Episode time / 90 s | [0, 1] |
| 13 | Speed multiplier / 3 | [0.33, 1] |

---

## Training

The production model (`results/boss_v3/`) was trained using ML-Agents PPO over **300,000 steps** (~31 minutes on CPU).

**Key hyperparameters** (`config/boss_ppo.yaml`):

| Parameter | Value |
|-----------|-------|
| Batch size | 1024 |
| Buffer size | 10 240 |
| Learning rate | 3 × 10⁻⁴ (linear decay) |
| Gamma | 0.99 |
| Lambda (GAE) | 0.95 |
| Clip epsilon | 0.2 |
| Network | 2 × 256 FC, normalised inputs |
| Max steps | 300 000 |

**Reward shaping:**

| Event | Reward |
|-------|--------|
| Move toward player | +0.05 |
| Hit (normal attack) | +0.40 |
| Hit (special attack) | +0.70 |
| Kill player | +1.00 |
| Idle (per step) | −0.005 |

**Curriculum** (`config/curriculum.yaml`) optionally progresses player speed from 1.0 → 2.0 → 3.0 m/s as reward thresholds are cleared.

**Final reward stats (1 322 episodes):**

| Metric | Value |
|--------|-------|
| Max | 22.71 |
| Min | −19.57 |
| Running average | 12.86 |
| Final episode | 16.18 |

Three model checkpoints are included: `boss_curriculum01`, `boss_final1`, and `boss_v3` (production).

---

## Evaluation

`eval/run_eval.py` runs 200 episodes each for the RL policy and the FSM baseline, logging outcome, duration, and final HP to `eval/results.csv`.

```bash
cd eval
python run_eval.py
```

`train_rllib.py` benchmarks distributed PPO scaling on Ray RLlib across 1, 2, 4, and 8 workers. Results are logged to `eval/rllib_benchmark.csv` and MLflow.

```bash
python train_rllib.py
```

**Benchmark results (50 000 steps each):**

| Workers | Wall time (s) | Speedup |
|---------|--------------|---------|
| 1 | 145.2 | 1.00× |
| 2 | 95.1 | 1.53× |
| 4 | 58.5 | 2.49× |
| 8 | 55.7 | 2.61× |

---

## LLM Taunt Layer

The bridge (`llm_layer/state_bridge.py`) is a standalone TCP server. Start it before entering Play Mode in Unity.

```bash
cd nemesis-boss-ai
python llm_layer/state_bridge.py
```

**Protocol** — Unity sends JSON every 6 seconds:

```json
{ "boss_hp": 0.8, "phase": 0, "last_move": "attack", "player_hp": 0.9 }
```

The bridge responds with a plain-text taunt (≤ 15 words). Post-generation guardrails strip modern slang and enforce a minimum length; if the output fails, one of five canned fallback taunts is returned.

**Configuration** (`.env` in project root):

```
GROQ_API_KEY=your_key_here
```

---

## Project Structure

```
nemesis-boss-ai/
├── config/
│   ├── boss_ppo.yaml          PPO hyperparameters
│   └── curriculum.yaml        Three-phase curriculum config
├── eval/
│   ├── run_eval.py            RL vs FSM win-rate evaluation
│   ├── results.csv            Latest evaluation output
│   └── rllib_benchmark.csv    Distributed training benchmark
├── llm_layer/
│   ├── state_bridge.py        TCP server + Groq integration + guardrails
│   └── guardrails/
│       ├── config.yml         NeMo Guardrails config
│       └── nemesis.co         Colang dialogue rules
├── results/
│   ├── boss_curriculum01/     Early curriculum checkpoint
│   ├── boss_final1/           Late-stage checkpoint
│   └── boss_v3/               Production model (ONNX + PyTorch)
├── Unity Project/
│   └── Assets/
│       ├── Models/BossAgent.onnx
│       ├── Scenes/CombatScene.unity
│       └── Scripts/
│           ├── BossAgent.cs
│           ├── PlayerController.cs
│           ├── ScriptedBoss.cs
│           ├── TauntDisplay.cs
│           └── CombatLabel.cs
├── train_rllib.py             Distributed PPO benchmark
└── test_bridge.py             TCP client smoke test
```

---

## Dependencies

**Unity** (`Unity Project/Packages/manifest.json`):

| Package | Version |
|---------|---------|
| Unity | 2022.3.62f3 |
| com.unity.sentis | 1.2.0-exp.2 |
| com.unity.render-pipelines.universal | 14.0.12 |
| com.unity.textmeshpro | 3.0.7 |
| com.unity.ml-agents | 2.3.0-exp.3 |

**Python** (install into a virtualenv):

```bash
pip install groq nemoguardrails ray[rllib] torch onnxruntime gymnasium python-dotenv
```

---

## Quick Start

1. **Clone and set up Python environment:**
   ```bash
   git clone <repo>
   cd nemesis-boss-ai
   python -m venv venv
   # Windows
   .\venv\Scripts\Activate.ps1
   # macOS / Linux
   source venv/bin/activate
   pip install groq nemoguardrails python-dotenv
   ```

2. **Add your Groq API key:**
   ```bash
   echo GROQ_API_KEY=your_key_here > .env
   ```

3. **Start the taunt bridge:**
   ```bash
   python llm_layer/state_bridge.py
   ```

4. **Open the Unity project** (`Unity Project/`) in Unity 2022.3, open `Assets/Scenes/CombatScene.unity`, and press Play.

---

## License

MIT — see `LICENSE`.
