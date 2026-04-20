# Nemesis Boss AI

A reinforcement learning-driven boss combat AI for Unity. The boss learns adaptive attack strategies via PPO, executes them in real-time through Unity Sentis (ONNX inference), and delivers in-character dialogue through a live LLM pipeline.

---

## Overview

Nemesis combines three systems:

1. **RL Combat Brain** — A PPO policy trained with ML-Agents and deployed via Unity Sentis drives a 5-state combat FSM: Idle → Telegraph → Attack → Recovery → Dodge. The boss reads 14 observations per frame and outputs one of four discrete actions.
2. **Phase Escalation** — As boss HP drops, speed and aggression scale up across three phases, culminating in an enraged state with a visual pulse effect.
3. **LLM Taunt Layer** — A Python TCP bridge streams live game state to Groq (`llama-3.3-70b-versatile`), which generates in-character taunts filtered through NeMo Guardrails before being displayed in a Genshin-style dialogue panel.

---

## Architecture

```
Unity (20 Hz FixedUpdate)
├── BossAgent.cs        — Sentis ONNX inference + 5-state FSM
├── PlayerController.cs — Autonomous player AI (5-state FSM)
├── TauntDisplay.cs     — TCP bridge to Python + typewriter UI
└── ScriptedBoss.cs     — Rule-based FSM baseline (used in evaluation)

TCP localhost:9999
└── llm_layer/state_bridge.py
    ├── Receives JSON game state from Unity every 6 s
    ├── Calls Groq API (llama-3.3-70b-versatile)
    ├── Applies NeMo Guardrails output filtering
    └── Returns taunt string to Unity
```

---

## Quick Start

### Requirements

| Tool | Version |
|------|---------|
| Unity | 2022.3.62f3 |
| Python | 3.10+ |
| Groq API key | [console.groq.com](https://console.groq.com) |

### 1 — Clone and set up Python

```bash
git clone <repo-url>
cd nemesis-boss-ai

python -m venv venv
# Windows
.\venv\Scripts\Activate.ps1
# macOS / Linux
source venv/bin/activate

pip install -r requirements.txt
```

### 2 — Configure secrets

```bash
cp .env.example .env
# Edit .env and set your Groq API key
```

### 3 — Open the Unity project

1. Open **Unity Hub** → Add project → select the `Unity Project/` folder.
2. Unity will auto-resolve all packages from `manifest.json` (Sentis, URP, TextMesh Pro). This may take a minute on first open.
3. If prompted to import **TextMesh Pro Essentials**, click **Import TMP Essentials**.
4. Open `Assets/Scenes/CombatScene.unity`.

### 4 — Start the taunt bridge

```bash
python llm_layer/state_bridge.py
```

Leave this running. Unity connects to it over TCP on port 9999.

### 5 — Press Play

The boss runs the RL policy via Sentis. Every 6 seconds, game state is sent to the bridge and a taunt appears in the dialogue panel.

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

### Combat FSM Timings

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

The production model (`results/boss_v3/`) was trained using ML-Agents PPO over **300,000 steps** (~31 minutes).

### Hyperparameters (`config/boss_ppo.yaml`)

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

### Reward Shaping

| Event | Reward |
|-------|--------|
| Move toward player | +0.05 |
| Hit (normal attack) | +0.40 |
| Hit (special attack) | +0.70 |
| Kill player | +1.00 |
| Idle (per step) | −0.005 |

### Curriculum (`config/curriculum.yaml`)

Optionally progresses player speed from 1.0 → 2.0 → 3.0 m/s as reward thresholds are reached.

### Training Results

| Run | Steps | Final Reward | Notes |
|-----|-------|-------------|-------|
| `boss_curriculum01` | 18 379 | 5.53 | Early stop — used to verify curriculum setup |
| `boss_final1` | 300 035 | 16.66 | Full run, flat reward schedule |
| `boss_v3` ✓ | 300 030 | 14.93 | **Production** — deployed in Unity |

All three ONNX models and their PyTorch checkpoints are included in `results/` for comparison.

### Re-running Training

```bash
# Install training dependencies
pip install "ray[rllib]" torch mlflow

# ML-Agents training (requires Unity Editor open with CombatScene)
mlagents-learn config/boss_ppo.yaml --run-id boss_v4

# Distributed PPO benchmark (standalone Python, no Unity needed)
python train_rllib.py
```

---

## Evaluation

`eval/run_eval.py` runs 200 episodes each for the RL policy and the FSM baseline, comparing win rates without needing Unity open.

```bash
python eval/run_eval.py
```

Results are written to `eval/results.csv`. The RLlib distributed training benchmark results are in `eval/rllib_benchmark.csv`.

### Distributed Training Benchmark (50 000 steps each)

| Workers | Wall time (s) | Speedup |
|---------|--------------|---------|
| 1 | 145.2 | 1.00× |
| 2 | 95.1 | 1.53× |
| 4 | 58.5 | 2.49× |
| 8 | 55.7 | 2.61× |

---

## LLM Taunt Layer

### Protocol

Unity sends JSON every 6 seconds:

```json
{ "boss_hp": 0.8, "phase": 0, "last_move": "attack", "player_hp": 0.9 }
```

The bridge returns a plain-text taunt (≤ 15 words). Post-generation guardrails strip modern slang and enforce a minimum length; outputs that fail fall back to one of five canned Nemesis phrases.

### Testing the Bridge

```bash
python test_bridge.py
```

Sends three test states and prints the generated taunts. Useful for verifying the Groq API key and guardrails pipeline without opening Unity.

---

## Project Structure

```
nemesis-boss-ai/
├── .env.example                   Required env vars (copy to .env)
├── requirements.txt               Python dependencies
├── config/
│   ├── boss_ppo.yaml              ML-Agents PPO hyperparameters
│   └── curriculum.yaml            Three-phase curriculum config
├── eval/
│   ├── run_eval.py                RL vs FSM win-rate evaluation
│   ├── results.csv                200-episode evaluation output
│   └── rllib_benchmark.csv        Distributed training benchmark
├── llm_layer/
│   ├── state_bridge.py            TCP server + Groq + guardrails
│   └── guardrails/
│       ├── config.yml             NeMo Guardrails config
│       └── nemesis.co             Colang dialogue rules
├── results/
│   ├── boss_curriculum01/         Early checkpoint (18k steps, reward 5.53)
│   ├── boss_final1/               Full run (300k steps, reward 16.66)
│   └── boss_v3/                   Production model (300k steps, reward 14.93)
│       ├── BossAgent.onnx         Deployed in Unity
│       ├── BossAgent/
│       │   └── BossAgent-300030.pt  PyTorch checkpoint (for retraining)
│       ├── configuration.yaml     Training config snapshot
│       └── run_logs/              Reward metrics and training status
├── train_rllib.py                 Distributed PPO benchmark
├── test_bridge.py                 TCP client smoke test
└── Unity Project/
    ├── Packages/
    │   └── manifest.json          Unity package dependencies (auto-resolved)
    ├── ProjectSettings/           Unity project configuration
    └── Assets/
        ├── Models/BossAgent.onnx  Runtime model (copy of boss_v3)
        ├── Scenes/CombatScene.unity
        └── Scripts/
            ├── BossAgent.cs
            ├── PlayerController.cs
            ├── ScriptedBoss.cs
            ├── TauntDisplay.cs
            └── CombatLabel.cs
```

---

## Unity Package Dependencies

Managed via `Unity Project/Packages/manifest.json` — Unity resolves these automatically on first open.

| Package | Version |
|---------|---------|
| com.unity.sentis | 1.2.0-exp.2 |
| com.unity.render-pipelines.universal | 14.0.12 |
| com.unity.textmeshpro | 3.0.7 |

> **Note:** ML-Agents (`com.unity.ml-agents 2.3.0-exp.3`) is used for training only and is not required to run the project. The trained policy is already exported to ONNX and loaded at runtime by Sentis.

---

## License

MIT — see `LICENSE`.
