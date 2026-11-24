using System;

/// <summary>
/// Per-episode numbers that both bosses report when an episode ends.
/// EvalRunner listens to <see cref="EpisodeStats.Ended"/> and writes them to CSV.
/// </summary>
public struct EpisodeResult
{
    public string boss;        // "RL" or "FSM"
    public string outcome;     // BOSS_WIN, PLAYER_WIN, TIMEOUT
    public float duration;     // seconds of simulated time
    public float bossHP;
    public float playerHP;
    public int finalPhase;
    public int bossSwings;     // attacks started (RL: Telegraph entered, FSM: hit attempts)
    public int bossHits;       // swings that dealt damage
    public int[] actionCounts; // RL only: how often the policy picked each action (0..3)
}

public static class EpisodeStats
{
    public static event Action<EpisodeResult> Ended;

    public static void Report(EpisodeResult r) => Ended?.Invoke(r);
}

/// <summary>
/// Notification-only combat events for visual effects (hit sparks, camera shake, damage numbers).
/// Nothing here feeds back into game logic.
/// </summary>
public static class CombatEvents
{
    /// victim position, damage, true if the boss landed it, true if it was the special attack
    public static event Action<UnityEngine.Vector3, float, bool, bool> Hit;

    public static void RaiseHit(UnityEngine.Vector3 victimPos, float dmg, bool byBoss, bool special) =>
        Hit?.Invoke(victimPos, dmg, byBoss, special);
}
