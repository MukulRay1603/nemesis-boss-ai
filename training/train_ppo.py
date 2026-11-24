"""
Nemesis: PPO training inside the real Unity game.

Launches N headless copies of the Unity build (EvalRunner/TrainingBridge, flag -nemesisTrain),
each connected over TCP. Every time a boss is ready to pick its next move, that copy sends
(obs, action mask, reward since last decision, done, outcome) and waits for an action. All copies
are stepped together, so one "step" here is one decision in each environment.

Exports an ONNX model with the same interface as the ML-Agents model the game already loads
(inputs obs_0 [batch,14] and action_masks [batch,4], output discrete_actions [batch,1] sampled
from a softmax), so Unity Project/Assets/Models/BossAgent.onnx can be swapped without code changes.

Usage (from the repo root, with the training venv):
    python training/train_ppo.py --envs 12 --total-steps 3000000 --max-hours 3 --run-id ppo_v4
    python training/train_ppo.py --envs 2 --total-steps 4096 --run-id smoke     # quick check

Outputs in results/<run-id>/: BossAgent.onnx (final), checkpoints/, log.csv, config.json.
"""

import argparse
import csv
import json
import os
import socket
import struct
import subprocess
import sys
import time

import numpy as np
import torch
import torch.nn as nn

OBS, ACT = 14, 4
MSG_IN = struct.Struct("<14f4ffii")      # obs, mask, reward, done, outcome
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "Unity Project", "Builds", "Windows", "Nemesis.exe")


# ── Environments ────────────────────────────────────────────────────────────
class UnityEnvs:
    def __init__(self, n, exe, base_port, seed, time_scale, log_dir):
        self.n = n
        self.procs, self.conns = [], []
        servers = []
        for i in range(n):
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind(("127.0.0.1", base_port + i))
            s.listen(1)
            servers.append(s)
            args = [exe, "-batchmode", "-nographics", "-nemesisTrain", str(base_port + i),
                    "-seed", str(seed * 1000 + i), "-timeScale", str(time_scale),
                    "-logFile", os.path.join(log_dir, f"unity_{i}.log")]
            self.procs.append(subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
        for s in servers:
            s.settimeout(120)
            c, _ = s.accept()
            c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            c.settimeout(120)
            self.conns.append(c)
            s.close()

    def _recv(self, c):
        buf = b""
        while len(buf) < MSG_IN.size:
            chunk = c.recv(MSG_IN.size - len(buf))
            if not chunk:
                raise ConnectionError("Unity instance closed the connection")
            buf += chunk
        v = MSG_IN.unpack(buf)
        return np.array(v[:14], np.float32), np.array(v[14:18], np.float32), v[18], v[19], v[20]

    def first(self):
        out = [self._recv(c) for c in self.conns]
        return np.stack([o[0] for o in out]), np.stack([o[1] for o in out])

    def step(self, actions):
        for c, a in zip(self.conns, actions):
            c.sendall(struct.pack("<i", int(a)))
        out = [self._recv(c) for c in self.conns]
        obs = np.stack([o[0] for o in out])
        mask = np.stack([o[1] for o in out])
        rew = np.array([o[2] for o in out], np.float32)
        done = np.array([o[3] for o in out], np.float32)
        outcome = np.array([o[4] for o in out], np.int32)
        return obs, mask, rew, done, outcome

    def close(self):
        for c in self.conns:
            try:
                c.sendall(struct.pack("<i", -1))
                c.close()
            except OSError:
                pass
        for p in self.procs:
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill()


# ── Model ───────────────────────────────────────────────────────────────────
class RunningNorm(nn.Module):
    def __init__(self, n):
        super().__init__()
        self.register_buffer("mean", torch.zeros(n))
        self.register_buffer("var", torch.ones(n))
        self.register_buffer("count", torch.tensor(1e-4))

    def update(self, x):
        bm, bv, bc = x.mean(0), x.var(0, unbiased=False), x.shape[0]
        d = bm - self.mean
        tot = self.count + bc
        self.mean += d * bc / tot
        self.var = (self.var * self.count + bv * bc + d.pow(2) * self.count * bc / tot) / tot
        self.count = tot

    def forward(self, x):
        return torch.clamp((x - self.mean) / torch.sqrt(self.var + 1e-8), -5.0, 5.0)


def mlp(i, o, h=256):
    return nn.Sequential(nn.Linear(i, h), nn.SiLU(), nn.Linear(h, h), nn.SiLU(), nn.Linear(h, o))


class Agent(nn.Module):
    def __init__(self):
        super().__init__()
        self.norm = RunningNorm(OBS)
        self.actor = mlp(OBS, ACT)
        self.critic = mlp(OBS, 1)
        nn.init.orthogonal_(self.actor[-1].weight, 0.01)
        nn.init.zeros_(self.actor[-1].bias)

    def logits(self, obs, mask):
        return self.actor(self.norm(obs)) + (mask - 1.0) * 1e8

    def value(self, obs):
        return self.critic(self.norm(obs)).squeeze(-1)


class OnnxPolicy(nn.Module):
    """Same graph contract as the ML-Agents export: masked softmax, then Multinomial."""

    def __init__(self, agent):
        super().__init__()
        self.norm, self.actor = agent.norm, agent.actor
        self.register_buffer("version", torch.tensor([3.0]))
        self.register_buffer("memory", torch.tensor([0.0]))
        self.register_buffer("act_shape", torch.tensor([[float(ACT)]]))

    def forward(self, obs_0, action_masks):
        logits = self.actor(self.norm(obs_0))
        logits = logits * action_masks - (1.0 - action_masks) * 1e8
        probs = torch.softmax(logits, dim=-1)
        sampled = torch.multinomial(probs, 1)
        det = torch.argmax(probs, dim=-1, keepdim=True)
        return self.version, self.memory, sampled, self.act_shape, det


def export_onnx(agent, path):
    pol = OnnxPolicy(agent).eval()
    obs = torch.zeros(1, OBS)
    mask = torch.ones(1, ACT)
    torch.onnx.export(pol, (obs, mask), path, opset_version=15,
                      input_names=["obs_0", "action_masks"],
                      output_names=["version_number", "memory_size", "discrete_actions",
                                    "discrete_action_output_shape", "deterministic_discrete_actions"],
                      dynamic_axes={"obs_0": {0: "batch"}, "action_masks": {0: "batch"},
                                    "discrete_actions": {0: "batch"},
                                    "deterministic_discrete_actions": {0: "batch"}})


# ── Training ────────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--envs", type=int, default=12)
    ap.add_argument("--total-steps", type=int, default=3_000_000)
    ap.add_argument("--max-hours", type=float, default=3.0)
    ap.add_argument("--rollout", type=int, default=512, help="decisions per env per update")
    ap.add_argument("--epochs", type=int, default=4)
    ap.add_argument("--minibatch", type=int, default=1024)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--gamma", type=float, default=0.99)
    ap.add_argument("--lam", type=float, default=0.95)
    ap.add_argument("--clip", type=float, default=0.2)
    ap.add_argument("--ent", type=float, default=0.01)
    ap.add_argument("--vf", type=float, default=0.5)
    ap.add_argument("--time-scale", type=float, default=100)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--port", type=int, default=6100)
    ap.add_argument("--run-id", default="ppo_v4")
    ap.add_argument("--checkpoint-every", type=int, default=20, help="updates")
    a = ap.parse_args()

    out = os.path.join(REPO, "results", a.run_id)
    os.makedirs(os.path.join(out, "checkpoints"), exist_ok=True)
    os.makedirs(os.path.join(out, "unity_logs"), exist_ok=True)
    with open(os.path.join(out, "config.json"), "w") as f:
        json.dump(vars(a), f, indent=2)

    torch.manual_seed(a.seed)
    np.random.seed(a.seed)
    torch.set_num_threads(2)
    agent = Agent()
    opt = torch.optim.Adam(agent.parameters(), lr=a.lr, eps=1e-5)

    envs = UnityEnvs(a.envs, a.exe, a.port, a.seed, a.time_scale, os.path.join(out, "unity_logs"))
    n, T = a.envs, a.rollout
    obs, mask = envs.first()
    num_updates = max(1, a.total_steps // (n * T))
    start = time.time()
    log = open(os.path.join(out, "log.csv"), "w", newline="")
    w = csv.writer(log)
    w.writerow(["update", "steps", "minutes", "episodes", "boss_win_rate", "player_win_rate", "timeout_rate",
                "mean_reward_per_decision", "policy_loss", "value_loss", "entropy", "lr"])

    steps = 0
    for upd in range(1, num_updates + 1):
        lr = a.lr * (1.0 - (upd - 1) / num_updates)
        for g in opt.param_groups:
            g["lr"] = lr

        b_obs = np.zeros((T, n, OBS), np.float32)
        b_mask = np.zeros((T, n, ACT), np.float32)
        b_act = np.zeros((T, n), np.int64)
        b_logp = np.zeros((T, n), np.float32)
        b_val = np.zeros((T, n), np.float32)
        b_rew = np.zeros((T, n), np.float32)
        b_done = np.zeros((T, n), np.float32)
        outcomes = []

        for t in range(T):
            with torch.no_grad():
                o = torch.from_numpy(obs)
                m = torch.from_numpy(mask)
                dist = torch.distributions.Categorical(logits=agent.logits(o, m))
                act = dist.sample()
                b_logp[t] = dist.log_prob(act).numpy()
                b_val[t] = agent.value(o).numpy()
            b_obs[t], b_mask[t], b_act[t] = obs, mask, act.numpy()
            obs, mask, rew, done, outc = envs.step(b_act[t])
            # reward/done arriving now belong to the decision just taken
            b_rew[t], b_done[t] = rew, done
            outcomes += [x for x in outc if x > 0]
        steps += n * T

        with torch.no_grad():
            next_val = agent.value(torch.from_numpy(obs)).numpy()
        adv = np.zeros_like(b_rew)
        last = np.zeros(n, np.float32)
        for t in reversed(range(T)):
            nv = next_val if t == T - 1 else b_val[t + 1]
            nonterm = 1.0 - b_done[t]
            delta = b_rew[t] + a.gamma * nv * nonterm - b_val[t]
            last = delta + a.gamma * a.lam * nonterm * last
            adv[t] = last
        ret = adv + b_val

        f_obs = torch.from_numpy(b_obs.reshape(-1, OBS))
        f_mask = torch.from_numpy(b_mask.reshape(-1, ACT))
        f_act = torch.from_numpy(b_act.reshape(-1))
        f_logp = torch.from_numpy(b_logp.reshape(-1))
        f_adv = torch.from_numpy(adv.reshape(-1))
        f_ret = torch.from_numpy(ret.reshape(-1))
        N = f_obs.shape[0]
        pl = vl = en = 0.0
        for _ in range(a.epochs):
            idx = torch.randperm(N)
            for s in range(0, N, a.minibatch):
                mb = idx[s:s + a.minibatch]
                dist = torch.distributions.Categorical(logits=agent.logits(f_obs[mb], f_mask[mb]))
                logp = dist.log_prob(f_act[mb])
                ratio = torch.exp(logp - f_logp[mb])
                ad = f_adv[mb]
                ad = (ad - ad.mean()) / (ad.std() + 1e-8)
                p_loss = -torch.min(ratio * ad, torch.clamp(ratio, 1 - a.clip, 1 + a.clip) * ad).mean()
                v_loss = 0.5 * (agent.value(f_obs[mb]) - f_ret[mb]).pow(2).mean()
                ent = dist.entropy().mean()
                loss = p_loss + a.vf * v_loss - a.ent * ent
                opt.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(agent.parameters(), 0.5)
                opt.step()
                pl, vl, en = p_loss.item(), v_loss.item(), ent.item()

        # Update observation statistics after optimising, so ratios above used one consistent normaliser.
        with torch.no_grad():
            agent.norm.update(torch.from_numpy(b_obs.reshape(-1, OBS)))

        minutes = (time.time() - start) / 60
        k = len(outcomes)
        bw = sum(1 for x in outcomes if x == 1) / k if k else float("nan")
        pw = sum(1 for x in outcomes if x == 2) / k if k else float("nan")
        to = sum(1 for x in outcomes if x == 3) / k if k else float("nan")
        w.writerow([upd, steps, f"{minutes:.1f}", k, f"{bw:.3f}", f"{pw:.3f}", f"{to:.3f}",
                    f"{b_rew.mean():.4f}", f"{pl:.4f}", f"{vl:.4f}", f"{en:.3f}", f"{lr:.2e}"])
        log.flush()
        print(f"upd {upd}/{num_updates} steps {steps} {minutes:.1f} min | episodes {k} "
              f"boss win {bw:.2f} player win {pw:.2f} timeout {to:.2f} | ent {en:.2f}", flush=True)

        if upd % a.checkpoint_every == 0:
            export_onnx(agent, os.path.join(out, "checkpoints", f"BossAgent_{steps}.onnx"))
            torch.save(agent.state_dict(), os.path.join(out, "checkpoints", f"agent_{steps}.pt"))
        if minutes / 60 > a.max_hours:
            print("time limit reached", flush=True)
            break

    export_onnx(agent, os.path.join(out, "BossAgent.onnx"))
    torch.save(agent.state_dict(), os.path.join(out, "agent.pt"))
    envs.close()
    log.close()
    print(f"done: {steps} decisions in {(time.time() - start) / 60:.1f} min -> {out}", flush=True)


if __name__ == "__main__":
    main()
