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
