"""
Nemesis — Win Rate Evaluation
==============================
Pits the trained PPO policy (ONNX) against a scripted auto-player
across 200 episodes, then pits a scripted FSM boss against the same
auto-player for 200 episodes.

The auto-player mirrors the Unity PlayerController logic:
  - Maintains safe distance (5 units)
  - Attacks during boss recovery window (after boss attacks)
  - Dodges when boss is too close
  - Deals 5 damage per hit, cooldown 1.8s

Results saved to eval/results.csv

Usage:
    python eval/run_eval.py
"""

import csv
import os
import numpy as np
import onnxruntime as ort

# ── Config ────────────────────────────────────────────────────────────────────
ONNX_PATH      = "results/boss_v3/BossAgent.onnx"
NUM_EPISODES   = 200
STEP_DT        = 0.05        # 20Hz — matches Unity FixedUpdate
MAX_STEPS      = int(60 / STEP_DT)   # 90 seconds

ARENA          = 10.0
BOSS_MAX_HP    = 300.0
PLAYER_MAX_HP  = 250.0
BOSS_DAMAGE    = 12.0
PLAYER_DAMAGE  = 4.0
ATTACK_RANGE   = 2.4
MOVE_SPEED     = 3.0
SAFE_DIST      = 5.0
PLAYER_SPEED   = 2.8

# Boss state machine timing (mirrors BossAgent.cs)
IDLE_DUR       = 0.35
TELEGRAPH_DUR  = 0.45
ATTACK_DUR     = 0.30
RECOVERY_DUR   = 0.85   # phase 0
DODGE_DUR      = 0.50


# ── Boss state machine ────────────────────────────────────────────────────────

class BossStateMachine:
    """
    Mirrors the BossAgent.cs state machine exactly.
    Wraps either RL policy or FSM action selection.
    """
    IDLE, TELEGRAPH, ATTACK, RECOVERY, DODGE = 0, 1, 2, 3, 4

    def __init__(self):
        self.state           = self.IDLE
        self.state_timer     = IDLE_DUR
        self.pending_action  = 0
        self.has_hit         = False
        self.recovery_dur    = RECOVERY_DUR

    def get_state_name(self):
        return ["IDLE","TELEGRAPH","ATTACK","RECOVERY","DODGE"][self.state]

    @property
    def is_vulnerable(self):
        return self.state == self.RECOVERY

    @property
    def is_attacking(self):
        return self.state in (self.TELEGRAPH, self.ATTACK)

    def set_state(self, s):
        self.state = s
        if   s == self.IDLE:      self.state_timer = IDLE_DUR
        elif s == self.TELEGRAPH: self.state_timer = TELEGRAPH_DUR
        elif s == self.ATTACK:
            self.state_timer = ATTACK_DUR
            self.has_hit     = False
        elif s == self.RECOVERY:  self.state_timer = self.recovery_dur
        elif s == self.DODGE:     self.state_timer = DODGE_DUR

    def tick(self, action):
        """Advance state timer, return next state."""
        self.state_timer -= STEP_DT
        if self.state_timer <= 0:
            if   self.state == self.IDLE:
                self.pending_action = action
                if   action == 2: self.set_state(self.DODGE)
                elif action in (1, 3): self.set_state(self.TELEGRAPH)
                else: self.set_state(self.IDLE)
            elif self.state == self.TELEGRAPH: self.set_state(self.ATTACK)
            elif self.state == self.ATTACK:    self.set_state(self.RECOVERY)
            elif self.state == self.RECOVERY:  self.set_state(self.IDLE)
            elif self.state == self.DODGE:     self.set_state(self.IDLE)

    def update_phase_timings(self, phase):
        if phase == 1:
            self.recovery_dur = 0.65
        elif phase == 2:
            self.recovery_dur = 0.45


# ── Combat environment ────────────────────────────────────────────────────────

class CombatEnv:
    def __init__(self, use_state_machine=True):
        self.use_sm = use_state_machine

    def reset(self):
        self.boss_pos    = np.array([0.0,  6.0])
        self.player_pos  = np.array([0.0, -6.0])
        self.boss_hp     = BOSS_MAX_HP
        self.player_hp   = PLAYER_MAX_HP
        self.boss_stam   = 100.0
        self.step        = 0
        self.phase       = 0
        self.last_action = 0

        # Player state
        self.p_attack_cd  = 0.0
        self.p_retreat_t  = 0.0
        self.p_vel        = np.array([0.0, 0.0])
        self.p_state      = "approach"  # approach/strafe/commit/retreat/dodge
        self.p_strafe_t   = 0.0
        self.p_strafe_sgn = 1.0
        self.p_dodge_cd   = 0.0
        self.p_retreat_dir = np.array([0.0, -1.0])

        # Boss state machine
        self.sm = BossStateMachine() if self.use_sm else None
        return self._obs()

    def _update_phase(self):
        old = self.phase
        if   self.boss_hp > BOSS_MAX_HP * 0.66: self.phase = 0
        elif self.boss_hp > BOSS_MAX_HP * 0.33: self.phase = 1
        else:                                    self.phase = 2
        if self.sm and self.phase != old:
            self.sm.update_phase_timings(self.phase)

    def _phase_speed(self):
        return {0: 1.0, 1: 1.3, 2: 1.6}[self.phase]

    def _obs(self):
        dist = np.linalg.norm(self.boss_pos - self.player_pos)
        return np.array([
            self.boss_hp   / BOSS_MAX_HP,
            self.player_hp / PLAYER_MAX_HP,
            self.boss_pos[0]   / ARENA,
            self.boss_pos[1]   / ARENA,
            self.player_pos[0] / ARENA,
            self.player_pos[1] / ARENA,
            dist / (ARENA * 2.0),
            self.phase / 2.0,
            self.last_action / 3.0,
            self.p_vel[0] / 10.0,
            self.p_vel[1] / 10.0,
            self.boss_stam / 100.0,
            self.step / MAX_STEPS,
            1.0 / 3.0,
        ], dtype=np.float32)

    # ── Boss movement (shared) ─────────────────────────────────────────────
    def _move_boss_rl(self, action):
        """Execute boss movement using RL action via state machine."""
        spd = MOVE_SPEED * self._phase_speed()
        dir_tp = self.player_pos - self.boss_pos
        dist   = np.linalg.norm(dir_tp)
        dn     = dir_tp / (dist + 1e-8)
        perp   = np.array([-dn[1], dn[0]])

        sm = self.sm
        sm.tick(action)

        if   sm.state == BossStateMachine.TELEGRAPH:
            self.boss_pos += dn * spd * 0.15 * STEP_DT
        elif sm.state == BossStateMachine.ATTACK:
            lunge = 2.5 if sm.pending_action == 3 else 1.8
            self.boss_pos += dn * spd * lunge * STEP_DT
            if dist < ATTACK_RANGE and not sm.has_hit:
                sm.has_hit = True
                dmg = BOSS_DAMAGE * (2 if sm.pending_action == 3 else 1)
                if sm.pending_action == 3 and self.boss_stam >= 20:
                    self.boss_stam  -= 20
                    self.player_hp  -= dmg
                elif sm.pending_action == 1:
                    self.player_hp  -= dmg
        elif sm.state == BossStateMachine.RECOVERY:
            self.boss_pos -= dn * spd * 0.2 * STEP_DT
        elif sm.state == BossStateMachine.DODGE:
            self.boss_pos += perp * spd * 1.8 * STEP_DT

        self.boss_stam  = min(100.0, self.boss_stam + 4 * STEP_DT)
        self.boss_pos   = np.clip(self.boss_pos, -9.0, 9.0)

    def _move_boss_fsm(self):
        """Scripted FSM boss — direct attack, no telegraph/recovery."""
        spd    = MOVE_SPEED * self._phase_speed()
        dir_tp = self.player_pos - self.boss_pos
        dist   = np.linalg.norm(dir_tp)
        dn     = dir_tp / (dist + 1e-8)
        perp   = np.array([-dn[1], dn[0]])

        if dist < 1.5 and np.random.rand() < 0.35:
            self.boss_pos += perp * spd * STEP_DT
        elif dist < ATTACK_RANGE:
            # attack every ~1.2s (no telegraph, no recovery = simpler)
            if self.step % int(1.2 / STEP_DT) == 0:
                self.player_hp -= BOSS_DAMAGE
            self.boss_pos += dn * spd * 0.3 * STEP_DT
        else:
            self.boss_pos += dn * spd * STEP_DT

        self.boss_pos = np.clip(self.boss_pos, -9.0, 9.0)

    # ── Auto-player (mirrors Unity PlayerController state machine) ─────────
    def _move_player(self, boss_vulnerable, boss_attacking):
        self.p_attack_cd -= STEP_DT
        self.p_retreat_t -= STEP_DT
        self.p_strafe_t  -= STEP_DT
        self.p_dodge_cd  -= STEP_DT

        dir_tb = self.boss_pos - self.player_pos
        dist   = np.linalg.norm(dir_tb)
        dn     = dir_tb / (dist + 1e-8)
        perp   = np.array([-dn[1], dn[0]]) * self.p_strafe_sgn

        # Wall safety
        near_wall = (np.abs(self.player_pos[0]) > ARENA - 1.5 or
                     np.abs(self.player_pos[1]) > ARENA - 1.5)
        if near_wall:
            to_center  = -self.player_pos / (np.linalg.norm(self.player_pos) + 1e-8)
            self.p_vel = to_center * PLAYER_SPEED * 2.0
            self.player_pos += self.p_vel * STEP_DT
            self.p_state = "approach"
            self.player_pos = np.clip(self.player_pos, -8.0, 8.0)
            return

        # Emergency dodge when boss attacks close
        if boss_attacking and dist < 3.0 and self.p_dodge_cd <= 0:
            self.p_strafe_sgn  = np.random.choice([-1, 1])
            perp_fresh = np.array([-dn[1], dn[0]]) * self.p_strafe_sgn
            self.p_vel = (perp_fresh - dn * 0.5)
            self.p_vel /= np.linalg.norm(self.p_vel) + 1e-8
            self.p_vel *= PLAYER_SPEED * 2.0
            self.p_dodge_cd = 2.0
            self.p_state = "dodge"

        if self.p_state == "approach":
            if dist > SAFE_DIST + 1.0:
                self.p_vel = dn * PLAYER_SPEED * 0.8
            elif dist < SAFE_DIST - 0.5:
                self.p_vel = -dn * PLAYER_SPEED
            else:
                self.p_strafe_sgn = np.random.choice([-1, 1])
                self.p_strafe_t   = np.random.uniform(0.6, 1.4)
                self.p_state      = "strafe"
                self.p_vel        = np.zeros(2)
            if boss_vulnerable and self.p_attack_cd <= 0:
                self.p_state = "commit"

        elif self.p_state == "strafe":
            self.p_vel = perp * PLAYER_SPEED * 0.9
            if dist < SAFE_DIST - 1.0:
                self.p_vel -= dn * PLAYER_SPEED * 0.5
            elif dist > SAFE_DIST + 2.0:
                self.p_vel += dn * PLAYER_SPEED * 0.5
            if boss_vulnerable and self.p_attack_cd <= 0:
                self.p_state = "commit"
            elif self.p_strafe_t <= 0:
                self.p_strafe_sgn = -self.p_strafe_sgn
                self.p_strafe_t   = np.random.uniform(0.5, 1.2)
                self.p_state      = "approach"
            if boss_attacking and dist < 3.5:
                self.p_retreat_dir = -dn
                self.p_retreat_t   = 0.6
                self.p_state       = "retreat"

        elif self.p_state == "commit":
            self.p_vel = dn * PLAYER_SPEED * 2.2
            if dist <= ATTACK_RANGE and self.p_attack_cd <= 0:
                self.boss_hp     -= PLAYER_DAMAGE
                self.p_attack_cd  = 1.8
                self.p_retreat_dir = (-dn + np.random.uniform(-0.3, 0.3, 2))
                norm = np.linalg.norm(self.p_retreat_dir)
                self.p_retreat_dir /= norm + 1e-8
                self.p_retreat_t = 1.2
                self.p_state     = "retreat"
            elif not boss_vulnerable and dist > ATTACK_RANGE:
                self.p_state = "approach"

        elif self.p_state == "retreat":
            self.p_vel = self.p_retreat_dir * PLAYER_SPEED * 1.4
            if self.p_retreat_t <= 0:
                self.p_state = "approach"
            if dist < 1.8 and self.p_dodge_cd <= 0:
                perp_fresh = np.array([-dn[1], dn[0]]) * np.random.choice([-1,1])
                self.p_vel = (perp_fresh - dn * 0.3)
                self.p_vel /= np.linalg.norm(self.p_vel) + 1e-8
                self.p_vel *= PLAYER_SPEED * 2.0
                self.p_dodge_cd = 2.0
                self.p_state = "dodge"

        elif self.p_state == "dodge":
            # Short burst then return
            if self.p_dodge_cd < 2.0 - 0.4:
                self.p_state = "approach"

        self.player_pos += self.p_vel * STEP_DT
        self.player_pos  = np.clip(self.player_pos, -8.0, 8.0)

    # ── Episode runners ────────────────────────────────────────────────────
    def run_episode_rl(self, sess, obs_name, mask_name, out_name):
        obs = self.reset()
        while self.step < MAX_STEPS and self.boss_hp > 0 and self.player_hp > 0:
            self.step += 1
            self._update_phase()

            masks = np.ones((1, 4), dtype=np.float32)
            if self.phase < 2 and self.boss_stam < 20:
                masks[0, 3] = 0.0

            action = int(sess.run(
                [out_name],
                {obs_name: obs.reshape(1, -1), mask_name: masks}
            )[0][0])
            self.last_action = action

            boss_vul = self.sm.is_vulnerable
            boss_atk = self.sm.is_attacking
            self._move_boss_rl(action)
            self._move_player(boss_vul, boss_atk)
            obs = self._obs()

        return self._result("rl_policy")

    def run_episode_fsm(self):
        self.reset()
        while self.step < MAX_STEPS and self.boss_hp > 0 and self.player_hp > 0:
            self.step += 1
            self._update_phase()
            self._move_boss_fsm()
            # FSM boss has no state machine — player never gets recovery window
            self._move_player(boss_vulnerable=False, boss_attacking=False)

        return self._result("fsm_baseline")

    def _result(self, mode):
        if   self.player_hp <= 0: outcome = "boss_win"
        elif self.boss_hp   <= 0: outcome = "player_win"
        else:                     outcome = "timeout"
        return {
            "mode":        mode,
            "outcome":     outcome,
            "steps":       self.step,
            "duration_s":  round(self.step * STEP_DT, 1),
            "boss_hp":     round(max(self.boss_hp, 0), 1),
            "player_hp":   round(max(self.player_hp, 0), 1),
            "final_phase": self.phase,
        }


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    os.makedirs("eval", exist_ok=True)

    sess      = ort.InferenceSession(ONNX_PATH)
    obs_name  = sess.get_inputs()[0].name
    mask_name = sess.get_inputs()[1].name
    out_name  = sess.get_outputs()[0].name

    rl_results  = []
    fsm_results = []

    env = CombatEnv(use_state_machine=True)

    print(f"\n{'='*55}")
    print(f"  RL Policy Evaluation — {NUM_EPISODES} episodes")
    print(f"{'='*55}")
    for ep in range(NUM_EPISODES):
        r = env.run_episode_rl(sess, obs_name, mask_name, out_name)
        r["episode"] = ep + 1
        rl_results.append(r)
        if (ep + 1) % 50 == 0:
            wins = sum(1 for x in rl_results if x["outcome"] == "boss_win")
            print(f"  ep={ep+1:>3} | boss={wins} "
                  f"player={ep+1-wins} "
                  f"({wins/(ep+1)*100:.1f}% boss win rate)")

    env_fsm = CombatEnv(use_state_machine=False)
    print(f"\n{'='*55}")
    print(f"  FSM Baseline Evaluation — {NUM_EPISODES} episodes")
    print(f"{'='*55}")
    for ep in range(NUM_EPISODES):
        r = env_fsm.run_episode_fsm()
        r["episode"] = ep + 1
        fsm_results.append(r)
        if (ep + 1) % 50 == 0:
            wins = sum(1 for x in fsm_results if x["outcome"] == "boss_win")
            print(f"  ep={ep+1:>3} | boss={wins} "
                  f"player={ep+1-wins} "
                  f"({wins/(ep+1)*100:.1f}% boss win rate)")

    # Save CSV
    all_results = rl_results + fsm_results
    fieldnames  = ["episode", "mode", "outcome", "steps", "duration_s",
                   "boss_hp", "player_hp", "final_phase"]
    with open("eval/results.csv", "w", newline="") as f:
        csv.DictWriter(f, fieldnames=fieldnames).writeheader()
        csv.DictWriter(f, fieldnames=fieldnames).writerows(all_results)

    # Summary stats
    def stats(results):
        wins    = sum(1 for r in results if r["outcome"] == "boss_win")
        p_wins  = sum(1 for r in results if r["outcome"] == "player_win")
        timeouts= sum(1 for r in results if r["outcome"] == "timeout")
        wr      = wins / len(results) * 100
        avg_dur = np.mean([r["duration_s"] for r in results])
        avg_ph  = np.mean([r["final_phase"] for r in results])
        return wins, p_wins, timeouts, wr, avg_dur, avg_ph

    rl_w, rl_pw, rl_to, rl_wr, rl_dur, rl_ph   = stats(rl_results)
    fsm_w, fsm_pw, fsm_to, fsm_wr, fsm_dur, _  = stats(fsm_results)

    print(f"\n{'='*55}")
    print(f"  EVALUATION COMPLETE")
    print(f"{'='*55}")
    print(f"  RL Policy:")
    print(f"    Boss wins   : {rl_w}  ({rl_wr:.1f}%)")
    print(f"    Player wins : {rl_pw}")
    print(f"    Timeouts    : {rl_to}")
    print(f"    Avg duration: {rl_dur:.1f}s")
    print(f"    Avg phase   : {rl_ph:.2f}")
    print(f"\n  FSM Baseline:")
    print(f"    Boss wins   : {fsm_w}  ({fsm_wr:.1f}%)")
    print(f"    Player wins : {fsm_pw}")
    print(f"    Timeouts    : {fsm_to}")
    print(f"    Avg duration: {fsm_dur:.1f}s")
    print(f"\n  ── Resume bullet numbers ──────────────────────")
    print(f"  RL boss win rate  : {rl_wr:.1f}%  → [W]%")
    print(f"  FSM boss win rate : {fsm_wr:.1f}%  → [F]%")
    print(f"{'='*55}\n")


if __name__ == "__main__":
    main()