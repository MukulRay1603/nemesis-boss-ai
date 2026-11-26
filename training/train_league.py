"""
Nemesis: co-training the boss and the hunter in the real Unity game (self-play league).

Both sides are PPO agents. The hunter uses the human player's moveset and decides every 0.2 s;
the boss decides at each state-machine Idle, as in the shipped game. Each fight, per game instance,
the trainer picks who plays (a small league):

    current boss  vs current hunter     both learn                         (most fights)
    current boss  vs past hunter        boss learns, cannot forget old counters
    past boss     vs current hunter     hunter learns against older bosses
    current boss  vs scripted bot       keeps the boss honest against the shipped opponent

Snapshots of both policies join the pool every --snapshot-every updates. Nothing about the game's
rules changes: if the special attack stays dominant against a hunter that learns, that is evidence
it is a balance problem, not a training artefact.

Protocol: see TrainingBridge.cs (flag -hunter 1).

Usage (from the repo root, training venv):
    python training/train_league.py --envs 12 --max-hours 3 --run-id league_v6 --boss-init results/ppo_v5/agent.pt
    python training/train_league.py --envs 2 --total-rounds 2000 --run-id league_smoke     # quick check

Outputs in results/<run-id>/: BossAgent.onnx, HunterAgent.onnx, boss.pt, hunter.pt, log.csv, config.json,
checkpoints/ (gitignored).
"""

import argparse
import copy
import csv
import json
import os
import random
import socket
import struct
import subprocess
import time

import numpy as np
import torch
import torch.nn as nn

from train_ppo import RunningNorm, mlp

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "Unity Project", "Builds", "Windows", "Nemesis.exe")

BOSS_ACT, HUNTER_OBS, HUNTER_ACT = 4, 19, 14
HUNTER_TIMEOUT = -0.25   # documented in TrainingBridge.cs; kept here for the log only


# ── Model ───────────────────────────────────────────────────────────────────
class Agent(nn.Module):
    def __init__(self, n_obs, n_act):
        super().__init__()
        self.n_obs, self.n_act = n_obs, n_act
        self.norm = RunningNorm(n_obs)
        self.actor = mlp(n_obs, n_act)
        self.critic = mlp(n_obs, 1)
        nn.init.orthogonal_(self.actor[-1].weight, 0.01)
        nn.init.zeros_(self.actor[-1].bias)

    def logits(self, obs, mask):
        return self.actor(self.norm(obs)) + (mask - 1.0) * 1e8

    def value(self, obs):
        return self.critic(self.norm(obs)).squeeze(-1)


class OnnxPolicy(nn.Module):
    """Same graph contract as the ML-Agents export the game loads: masked softmax, then Multinomial."""

    def __init__(self, agent):
        super().__init__()
        self.norm, self.actor = agent.norm, agent.actor
        self.register_buffer("version", torch.tensor([3.0]))
        self.register_buffer("memory", torch.tensor([0.0]))
        self.register_buffer("act_shape", torch.tensor([[float(agent.n_act)]]))

    def forward(self, obs_0, action_masks):
        logits = self.actor(self.norm(obs_0))
        logits = logits * action_masks - (1.0 - action_masks) * 1e8
        probs = torch.softmax(logits, dim=-1)
        sampled = torch.multinomial(probs, 1)
        det = torch.argmax(probs, dim=-1, keepdim=True)
        return self.version, self.memory, sampled, self.act_shape, det


def export_onnx(agent, path):
    pol = OnnxPolicy(agent).eval()
    torch.onnx.export(pol, (torch.zeros(1, agent.n_obs), torch.ones(1, agent.n_act)), path, opset_version=15,
                      input_names=["obs_0", "action_masks"],
                      output_names=["version_number", "memory_size", "discrete_actions",
                                    "discrete_action_output_shape", "deterministic_discrete_actions"],
                      dynamic_axes={"obs_0": {0: "batch"}, "action_masks": {0: "batch"},
                                    "discrete_actions": {0: "batch"},
                                    "deterministic_discrete_actions": {0: "batch"}})


# ── Environments ────────────────────────────────────────────────────────────
class Conn:
    def __init__(self, sock, boss_obs):
        self.s = sock
        self.boss_body = struct.Struct(f"<{boss_obs}f{BOSS_ACT}ffii")
        self.hunter_body = struct.Struct(f"<{HUNTER_OBS}f{HUNTER_ACT}ffii")
        self.end_body = struct.Struct("<ffii")
        self.boss_obs = boss_obs

    def _read(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.s.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("Unity instance closed the connection")
            buf += chunk
        return buf

    def recv(self):
        kind = struct.unpack("<i", self._read(4))[0]
        if kind == 0:
            v = self.boss_body.unpack(self._read(self.boss_body.size))
            k = self.boss_obs
            return kind, np.array(v[:k], np.float32), np.array(v[k:k + BOSS_ACT], np.float32), v[k + BOSS_ACT]
        if kind == 1:
            v = self.hunter_body.unpack(self._read(self.hunter_body.size))
            k = HUNTER_OBS
            return kind, np.array(v[:k], np.float32), np.array(v[k:k + HUNTER_ACT], np.float32), v[k + HUNTER_ACT]
        if kind == 2:
            return (kind,) + self.end_body.unpack(self._read(self.end_body.size))
        raise ValueError(f"unknown message kind {kind}")

    def send(self, a):
        self.s.sendall(struct.pack("<i", int(a)))


def launch(n, exe, base_port, seed, time_scale, boss_obs, hunter_every, log_dir):
    procs, conns, servers = [], [], []
    for i in range(n):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind(("127.0.0.1", base_port + i))
        s.listen(1)
        servers.append(s)
        args = [exe, "-batchmode", "-nographics", "-nemesisTrain", str(base_port + i),
                "-seed", str(seed * 1000 + i), "-timeScale", str(time_scale), "-obs", str(boss_obs),
                "-hunter", "1", "-hunterEvery", str(hunter_every),
                "-logFile", os.path.join(log_dir, f"unity_{i}.log")]
        procs.append(subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    for s in servers:
        s.settimeout(120)
        c, _ = s.accept()
        c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        c.settimeout(120)
        conns.append(Conn(c, boss_obs))
        s.close()
    return procs, conns


# ── Rollout storage: one ordered stream per (env, side) for the learning policy ──
class Streams:
    """Transitions of the *current* policy of one side. A transition's reward arrives with the
    next message for that side (or with the episode end), so the newest entry of each stream is
    pending until then; it is carried into the next rollout."""

    def __init__(self, n_env):
        self.s = [[] for _ in range(n_env)]

    def add(self, e, obs, mask, act, logp, val):
        self.s[e].append({"obs": obs, "mask": mask, "act": act, "logp": logp, "val": val, "rew": 0.0,
                          "done": False, "ready": False})

    def reward(self, e, r, done=False):
        if self.s[e] and not self.s[e][-1]["ready"]:
            t = self.s[e][-1]
            t["rew"] += r
            t["done"] = t["done"] or done
            t["ready"] = True

    def cut(self, e):
        """The learning policy stopped controlling this side (a snapshot or the bot takes over)."""
        if self.s[e] and not self.s[e][-1]["ready"]:
            self.s[e].pop()

    def batch(self, gamma, lam):
        obs, mask, act, logp, adv, ret = [], [], [], [], [], []
        for e, st in enumerate(self.s):
            ready = [t for t in st if t["ready"]]
            pending = [t for t in st if not t["ready"]]
            if not ready:
                continue
            last = 0.0
            advs = [0.0] * len(ready)
            for i in reversed(range(len(ready))):
                t = ready[i]
                if t["done"]:
                    nv, last = 0.0, 0.0
                elif i + 1 < len(ready):
                    nv = ready[i + 1]["val"]
                elif pending:
                    nv = pending[0]["val"]
                else:
                    nv = t["val"]   # stream paused; bootstrap with own value
                delta = t["rew"] + gamma * nv - t["val"]
                last = delta + gamma * lam * (0.0 if t["done"] else 1.0) * last
                advs[i] = last
            for t, a in zip(ready, advs):
                obs.append(t["obs"]); mask.append(t["mask"]); act.append(t["act"]); logp.append(t["logp"])
                adv.append(a); ret.append(a + t["val"])
            self.s[e] = pending
        if not obs:
            return None
        return (torch.from_numpy(np.stack(obs)), torch.from_numpy(np.stack(mask)), torch.tensor(act),
                torch.tensor(logp, dtype=torch.float32), torch.tensor(adv, dtype=torch.float32),
                torch.tensor(ret, dtype=torch.float32))


def ppo_update(agent, opt, data, a):
    o, m, act, old_logp, adv, ret = data
    n = o.shape[0]
    pl = vl = en = 0.0
    for _ in range(a.epochs):
        idx = torch.randperm(n)
        for s in range(0, n, a.minibatch):
            mb = idx[s:s + a.minibatch]
            dist = torch.distributions.Categorical(logits=agent.logits(o[mb], m[mb]))
            logp = dist.log_prob(act[mb])
            ratio = torch.exp(logp - old_logp[mb])
            ad = adv[mb]
            ad = (ad - ad.mean()) / (ad.std() + 1e-8)
            p_loss = -torch.min(ratio * ad, torch.clamp(ratio, 1 - a.clip, 1 + a.clip) * ad).mean()
            v_loss = 0.5 * (agent.value(o[mb]) - ret[mb]).pow(2).mean()
            ent = dist.entropy().mean()
            loss = p_loss + a.vf * v_loss - a.ent * ent
            opt.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(agent.parameters(), 0.5)
            opt.step()
            pl, vl, en = p_loss.item(), v_loss.item(), ent.item()
    with torch.no_grad():
        agent.norm.update(o)
    return pl, vl, en, n


def frozen(agent):
    c = copy.deepcopy(agent)
    for p in c.parameters():
        p.requires_grad_(False)
    return c.eval()


# ── Training ────────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--envs", type=int, default=12)
    ap.add_argument("--total-rounds", type=int, default=10_000_000, help="one message per env per round")
    ap.add_argument("--max-hours", type=float, default=3.0)
    ap.add_argument("--rounds-per-update", type=int, default=512)
    ap.add_argument("--epochs", type=int, default=4)
    ap.add_argument("--minibatch", type=int, default=1024)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--gamma", type=float, default=0.99)
    ap.add_argument("--hunter-gamma", type=float, default=0.995, help="hunter decides ~5x more often")
    ap.add_argument("--lam", type=float, default=0.95)
    ap.add_argument("--clip", type=float, default=0.2)
    ap.add_argument("--ent", type=float, default=0.01)
    ap.add_argument("--vf", type=float, default=0.5)
    ap.add_argument("--time-scale", type=float, default=100)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--port", type=int, default=6300)
    ap.add_argument("--run-id", default="league_v6")
    ap.add_argument("--obs", type=int, default=18, choices=[14, 18], help="boss inputs (18 = with opponent profile)")
    ap.add_argument("--hunter-every", type=int, default=10, help="physics steps per hunter decision (10 = 0.2 s)")
    ap.add_argument("--boss-init", default=None, help="start the boss from a train_ppo.py agent.pt (same --obs)")
    ap.add_argument("--snapshot-every", type=int, default=10, help="updates")
    ap.add_argument("--pool-size", type=int, default=8)
    ap.add_argument("--p-scripted", type=float, default=0.15)
    ap.add_argument("--p-past-hunter", type=float, default=0.2)
    ap.add_argument("--p-past-boss", type=float, default=0.15)
    ap.add_argument("--checkpoint-every", type=int, default=20, help="updates")
    a = ap.parse_args()

    out = os.path.join(REPO, "results", a.run_id)
    os.makedirs(os.path.join(out, "checkpoints"), exist_ok=True)
    os.makedirs(os.path.join(out, "unity_logs"), exist_ok=True)
    with open(os.path.join(out, "config.json"), "w") as f:
        json.dump(vars(a), f, indent=2)

    random.seed(a.seed)
    torch.manual_seed(a.seed)
    np.random.seed(a.seed)
    torch.set_num_threads(2)

    boss = Agent(a.obs, BOSS_ACT)
    if a.boss_init:
        boss.load_state_dict(torch.load(a.boss_init))
    hunter = Agent(HUNTER_OBS, HUNTER_ACT)
    boss_opt = torch.optim.Adam(boss.parameters(), lr=a.lr, eps=1e-5)
    hunter_opt = torch.optim.Adam(hunter.parameters(), lr=a.lr, eps=1e-5)
    boss_pool, hunter_pool = [frozen(boss)], [frozen(hunter)]

    procs, conns = launch(a.envs, a.exe, a.port, a.seed, a.time_scale, a.obs, a.hunter_every,
                          os.path.join(out, "unity_logs"))
    n = a.envs
    bs, hs = Streams(n), Streams(n)
    # Per env: who controls each side this fight. None = the learning policy.
    boss_ctl = [None] * n
    hunter_ctl = ["current"] * n    # "current", a frozen Agent, or "scripted"

    def assign(e):
        r = random.random()
        if r < a.p_scripted:
            boss_ctl[e], hunter_ctl[e] = None, "scripted"
        elif r < a.p_scripted + a.p_past_hunter and len(hunter_pool) > 1:
            boss_ctl[e], hunter_ctl[e] = None, random.choice(hunter_pool[:-1])
        elif r < a.p_scripted + a.p_past_hunter + a.p_past_boss and len(boss_pool) > 1:
            boss_ctl[e], hunter_ctl[e] = random.choice(boss_pool[:-1]), "current"
        else:
            boss_ctl[e], hunter_ctl[e] = None, "current"

    log = open(os.path.join(out, "log.csv"), "w", newline="")
    w = csv.writer(log)
    w.writerow(["update", "rounds", "minutes", "fights", "boss_win_vs_current", "hunter_win_vs_current",
                "timeout_vs_current", "boss_win_vs_scripted", "boss_special_share", "boss_attack_share",
                "boss_dodge_share", "hunter_attack_share", "hunter_dash_share", "boss_samples", "hunter_samples",
                "boss_entropy", "hunter_entropy", "pool"])

    start = time.time()
    rounds, upd = 0, 0
    stats = {"cur": [], "scr": [], "boss_act": np.zeros(BOSS_ACT), "hunter_act": np.zeros(HUNTER_ACT)}
    while rounds < a.total_rounds:
        # One message per env, grouped by who must answer, then answered in batches.
        msgs = [c.recv() for c in conns]
        replies = [0] * n
        groups = {}
        for e, m in enumerate(msgs):
            kind = m[0]
            if kind == 2:
                _, boss_r, hunter_r, outcome, hunter_was_agent = m
                if boss_ctl[e] is None:
                    bs.reward(e, boss_r, done=True)
                if hunter_was_agent and hunter_ctl[e] == "current":
                    hs.reward(e, hunter_r, done=True)
                if hunter_ctl[e] == "scripted":
                    stats["scr"].append(outcome)
                elif boss_ctl[e] is None and hunter_ctl[e] == "current":
                    stats["cur"].append(outcome)
                old_boss, old_hunter = boss_ctl[e], hunter_ctl[e]
                assign(e)
                if old_boss is None and boss_ctl[e] is not None:
                    bs.cut(e)
                if old_hunter == "current" and hunter_ctl[e] != "current":
                    hs.cut(e)
                replies[e] = 0 if hunter_ctl[e] == "scripted" else 1
                continue
            _, obs, mask, rew = m
            if kind == 0:
                if boss_ctl[e] is None:
                    bs.reward(e, rew)
                key = ("boss", id(boss_ctl[e]) if boss_ctl[e] is not None else "cur")
                pol = boss if boss_ctl[e] is None else boss_ctl[e]
            else:
                if hunter_ctl[e] == "current":
                    hs.reward(e, rew)
                key = ("hunter", "cur" if hunter_ctl[e] == "current" else id(hunter_ctl[e]))
                pol = hunter if hunter_ctl[e] == "current" else hunter_ctl[e]
            groups.setdefault(key, (pol, []))[1].append((e, obs, mask))

        for (side, who), (pol, items) in groups.items():
            o = torch.from_numpy(np.stack([x[1] for x in items]))
            mk = torch.from_numpy(np.stack([x[2] for x in items]))
            with torch.no_grad():
                dist = torch.distributions.Categorical(logits=pol.logits(o, mk))
                act = dist.sample()
                logp = dist.log_prob(act)
                val = pol.value(o) if who == "cur" else None
            for j, (e, ob, ma) in enumerate(items):
                replies[e] = int(act[j])
                if who == "cur":
                    st = bs if side == "boss" else hs
                    st.add(e, ob, ma, int(act[j]), float(logp[j]), float(val[j]))
                    (stats["boss_act"] if side == "boss" else stats["hunter_act"])[int(act[j])] += 1

        for e, c in enumerate(conns):
            c.send(replies[e])
        rounds += 1

        if rounds % a.rounds_per_update == 0:
            upd += 1
            frac = 1.0 - min(1.0, rounds / a.total_rounds)
            for opt in (boss_opt, hunter_opt):
                for g in opt.param_groups:
                    g["lr"] = a.lr * max(frac, 0.1)
            bd = bs.batch(a.gamma, a.lam)
            hd = hs.batch(a.hunter_gamma, a.lam)
            b_res = ppo_update(boss, boss_opt, bd, a) if bd is not None else (0, 0, 0, 0)
            h_res = ppo_update(hunter, hunter_opt, hd, a) if hd is not None else (0, 0, 0, 0)

            if upd % a.snapshot_every == 0:
                boss_pool.append(frozen(boss))
                hunter_pool.append(frozen(hunter))
                # keep the oldest (diverse starting point) and the most recent ones
                while len(boss_pool) > a.pool_size:
                    boss_pool.pop(1)
                while len(hunter_pool) > a.pool_size:
                    hunter_pool.pop(1)

            cur, scr = stats["cur"], stats["scr"]
            rate = lambda xs, v: (sum(1 for x in xs if x == v) / len(xs)) if xs else float("nan")
            ba = stats["boss_act"] / max(1, stats["boss_act"].sum())
            ha = stats["hunter_act"] / max(1, stats["hunter_act"].sum())
            minutes = (time.time() - start) / 60
            row = [upd, rounds, f"{minutes:.1f}", len(cur) + len(scr),
                   f"{rate(cur, 1):.3f}", f"{rate(cur, 2):.3f}", f"{rate(cur, 3):.3f}", f"{rate(scr, 1):.3f}",
                   f"{ba[3]:.3f}", f"{ba[1]:.3f}", f"{ba[2]:.3f}",
                   f"{ha[9] + ha[10]:.3f}", f"{ha[11] + ha[12] + ha[13]:.3f}",
                   b_res[3], h_res[3], f"{b_res[2]:.3f}", f"{h_res[2]:.3f}", len(hunter_pool)]
            w.writerow(row)
            log.flush()
            print(f"upd {upd} rounds {rounds} {minutes:.1f} min | vs current hunter: boss {row[4]} hunter {row[5]} "
                  f"timeout {row[6]} | vs bot: boss {row[7]} | boss special {row[8]} | pool {len(hunter_pool)}",
                  flush=True)
            stats = {"cur": [], "scr": [], "boss_act": np.zeros(BOSS_ACT), "hunter_act": np.zeros(HUNTER_ACT)}

            if upd % a.checkpoint_every == 0:
                ck = os.path.join(out, "checkpoints")
                torch.save(boss.state_dict(), os.path.join(ck, f"boss_{rounds}.pt"))
                torch.save(hunter.state_dict(), os.path.join(ck, f"hunter_{rounds}.pt"))
                export_onnx(boss, os.path.join(ck, f"BossAgent_{rounds}.onnx"))
                export_onnx(hunter, os.path.join(ck, f"HunterAgent_{rounds}.onnx"))
            if minutes / 60 > a.max_hours:
                print("time limit reached", flush=True)
                break

    export_onnx(boss, os.path.join(out, "BossAgent.onnx"))
    export_onnx(hunter, os.path.join(out, "HunterAgent.onnx"))
    torch.save(boss.state_dict(), os.path.join(out, "boss.pt"))
    torch.save(hunter.state_dict(), os.path.join(out, "hunter.pt"))
    for c in conns:
        try:
            c.send(-1)
            c.s.close()
        except OSError:
            pass
    for p in procs:
        try:
            p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            p.kill()
    log.close()
    print(f"done: {rounds} rounds in {(time.time() - start) / 60:.1f} min -> {out}", flush=True)


if __name__ == "__main__":
    main()
