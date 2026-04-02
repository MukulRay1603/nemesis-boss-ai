"""
Nemesis — Ray RLlib Distributed PPO Benchmark
==============================================
Trains a PPO agent on a gym-compatible BossEnv that mirrors
the exact obs/action space used in Unity (14 floats, 4 discrete actions).
Benchmarks wall-clock time for 1, 2, 4, 8 workers at 50k steps each.
Results saved to eval/rllib_benchmark.csv and logged to MLflow.

Usage:
    python train_rllib.py
"""

import time
import csv
import os
import numpy as np
import gymnasium as gym
from gymnasium import spaces
import ray
from ray.rllib.algorithms.ppo import PPOConfig
import mlflow

# ── Gym Environment ──────────────────────────────────────────────────────────

class BossEnv(gym.Env):
    """
    Simplified boss combat environment.
    Mirrors the exact 14-float observation space and 4-action discrete
    action space used in Unity ML-Agents training for Nemesis.

    Obs (14 floats, all normalized -1..1 or 0..1):
        0  boss_hp_norm
        1  player_hp_norm
        2  boss_pos_x
        3  boss_pos_z
        4  player_pos_x
        5  player_pos_z
        6  distance_norm
        7  boss_phase_norm
        8  last_action_norm
        9  player_vel_x
        10 player_vel_z
        11 boss_stamina_norm
        12 time_norm
        13 player_speed_multiplier_norm

    Actions (discrete, 4):
        0 = idle
        1 = attack / move toward player
        2 = dodge
        3 = special attack
    """

    metadata = {"render_modes": []}

    def __init__(self, env_config=None):
        super().__init__()
        self.observation_space = spaces.Box(
            low=-1.0, high=1.0, shape=(14,), dtype=np.float32
        )
        self.action_space = spaces.Discrete(4)

        # Episode params
        self.max_steps = 300          # ~15s at 20Hz
        self.arena_size = 10.0
        self.attack_range = 2.5
        self.attack_damage = 10.0
        self.move_speed = 3.0
        self.dodge_speed = 6.0
        self.max_hp = 100.0
        self.max_stamina = 100.0

        self._reset_state()

    def _reset_state(self):
        self.boss_pos = np.array([0.0, 6.0])      # x, z
        self.player_pos = np.array([0.0, -6.0])
        self.boss_hp = self.max_hp
        self.player_hp = self.max_hp
        self.boss_stamina = self.max_stamina
        self.player_vel = np.array([0.0, 0.0])
        self.step_count = 0
        self.last_action = 0

    def _get_obs(self):
        dist = np.linalg.norm(self.boss_pos - self.player_pos)
        phase = 0.0
        if self.boss_hp < self.max_hp * 0.66:
            phase = 1.0
        if self.boss_hp < self.max_hp * 0.33:
            phase = 2.0
        return np.array([
            self.boss_hp / self.max_hp,
            self.player_hp / self.max_hp,
            self.boss_pos[0] / self.arena_size,
            self.boss_pos[1] / self.arena_size,
            self.player_pos[0] / self.arena_size,
            self.player_pos[1] / self.arena_size,
            dist / (self.arena_size * 2.0),
            phase / 2.0,
            float(self.last_action) / 3.0,
            self.player_vel[0] / 10.0,
            self.player_vel[1] / 10.0,
            self.boss_stamina / self.max_stamina,
            float(self.step_count) / self.max_steps,
            1.0 / 3.0,   # player_speed_multiplier=1, normalized
        ], dtype=np.float32)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._reset_state()
        return self._get_obs(), {}

    def step(self, action):
        self.step_count += 1
        self.last_action = action
        reward = 0.0

        # Player moves randomly (simple autoplay)
        angle = self.np_random.uniform(0, 2 * np.pi)
        self.player_vel = np.array([np.cos(angle), np.sin(angle)]) * 2.0
        self.player_pos += self.player_vel * 0.05
        self.player_pos = np.clip(self.player_pos, -self.arena_size, self.arena_size)

        dir_to_player = self.player_pos - self.boss_pos
        dist = np.linalg.norm(dir_to_player)
        dir_norm = dir_to_player / (dist + 1e-8)

        if action == 0:   # idle
            reward -= 0.005

        elif action == 1:  # attack / move toward
            self.boss_pos += dir_norm * self.move_speed * 0.05
            if dist < self.attack_range:
                self.player_hp -= self.attack_damage
                reward += 0.4
                if self.player_hp <= 0:
                    reward += 1.0
            else:
                reward += 0.05

        elif action == 2:  # dodge
            perp = np.array([-dir_norm[1], dir_norm[0]])
            self.boss_pos += perp * self.dodge_speed * 0.05
            reward += 0.02

        elif action == 3:  # special
            if self.boss_stamina >= 20.0:
                self.boss_pos += dir_norm * self.move_speed * 2.0 * 0.05
                self.boss_stamina -= 20.0
                if dist < self.attack_range * 1.5:
                    self.player_hp -= self.attack_damage * 2.0
                    reward += 0.7
                    if self.player_hp <= 0:
                        reward += 1.0
            else:
                reward -= 0.05

        # Stamina regen
        self.boss_stamina = min(self.max_stamina, self.boss_stamina + 5.0 * 0.05)

        # Wall penalty
        self.boss_pos = np.clip(self.boss_pos, -self.arena_size, self.arena_size)

        # Termination
        terminated = bool(self.player_hp <= 0 or self.boss_hp <= 0)
        truncated = bool(self.step_count >= self.max_steps)

        return self._get_obs(), reward, terminated, truncated, {}


# ── Benchmark ────────────────────────────────────────────────────────────────

def run_benchmark(num_workers: int, total_steps: int = 50_000) -> dict:
    """Train PPO with num_workers rollout workers, return timing + reward."""
    print(f"\n{'='*60}")
    print(f"  Running: {num_workers} worker(s) | {total_steps:,} steps")
    print(f"{'='*60}")

    config = (
        PPOConfig()
        .environment(BossEnv)
        .rollouts(num_rollout_workers=num_workers)
        .framework("torch")
        .training(
            lr=3e-4,
            gamma=0.99,
            lambda_=0.95,
            clip_param=0.2,
            train_batch_size=4000,
            sgd_minibatch_size=128,
            num_sgd_iter=10,
        )
        .resources(num_gpus=0)   # CPU only — GPU reserved for Unity/Sentis
    )

    algo = config.build()
    steps_done = 0
    episode_rewards = []
    wall_start = time.time()

    while steps_done < total_steps:
        result = algo.train()
        steps_done = result["timesteps_total"]
        mean_reward = result.get("episode_reward_mean", float("nan"))
        episode_rewards.append(mean_reward)
        print(
            f"  steps={steps_done:>7,} | "
            f"reward_mean={mean_reward:>7.3f} | "
            f"elapsed={time.time()-wall_start:.1f}s"
        )

    wall_time = time.time() - wall_start
    final_reward = episode_rewards[-1] if episode_rewards else float("nan")
    algo.stop()

    return {
        "num_workers": num_workers,
        "total_steps": steps_done,
        "wall_time_sec": round(wall_time, 2),
        "final_reward_mean": round(final_reward, 4),
    }


def main():
    os.makedirs("eval", exist_ok=True)
    ray.init(ignore_reinit_error=True)

    worker_counts = [1, 2, 4, 8]
    results = []

    mlflow.set_experiment("nemesis_rllib_benchmark")

    for n in worker_counts:
        with mlflow.start_run(run_name=f"ppo_{n}workers"):
            result = run_benchmark(num_workers=n, total_steps=50_000)
            results.append(result)
            mlflow.log_params({"num_workers": n, "total_steps": 50_000})
            mlflow.log_metrics({
                "wall_time_sec": result["wall_time_sec"],
                "final_reward_mean": result["final_reward_mean"],
            })
            print(
                f"\n  ✓ {n} worker(s): "
                f"{result['wall_time_sec']:.1f}s | "
                f"reward={result['final_reward_mean']:.4f}"
            )

    # Compute speedups relative to 1 worker
    baseline = next(r for r in results if r["num_workers"] == 1)
    for r in results:
        r["speedup_vs_1worker"] = round(
            baseline["wall_time_sec"] / r["wall_time_sec"], 2
        )

    # Save CSV
    csv_path = "eval/rllib_benchmark.csv"
    fieldnames = ["num_workers", "total_steps", "wall_time_sec",
                  "final_reward_mean", "speedup_vs_1worker"]
    with open(csv_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(results)

    print(f"\n{'='*60}")
    print("  BENCHMARK COMPLETE")
    print(f"{'='*60}")
    print(f"  {'Workers':<10} {'Time(s)':<12} {'Speedup':<10} {'Reward'}")
    print(f"  {'-'*45}")
    for r in results:
        print(
            f"  {r['num_workers']:<10} "
            f"{r['wall_time_sec']:<12.1f} "
            f"{r['speedup_vs_1worker']:<10.2f}x "
            f"{r['final_reward_mean']:.4f}"
        )
    print(f"\n  Results saved to {csv_path}")
    print(f"  MLflow UI: run 'mlflow ui' in this directory\n")

    ray.shutdown()


if __name__ == "__main__":
    main()