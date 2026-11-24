using System.IO;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Lets an external PPO trainer (training/train_ppo.py) drive the boss inside the real game.
/// Activated with:  -nemesisTrain PORT  [-seed N] [-timeScale X] [-randomizePlayer 0|1]
///
/// At every boss decision point the game sends one message and blocks until the action arrives:
///   Unity -> Python  14 (or 18 with -obs 18) float obs, 4 float mask, float reward since the last decision,
///                    int done (1 = the previous decision's episode ended; obs is a fresh episode),
///                    int outcome of that episode (0 none, 1 boss win, 2 player win, 3 timeout)
///   Python -> Unity  int action
/// Reward (per decision): damage dealt to the player as a fraction of its max HP, minus damage
/// taken as a fraction of the boss's max HP; at episode end +1 win, -1 loss, -0.5 timeout.
/// The opponent bot's stats are randomised per episode (unless -randomizePlayer 0) so the policy
/// does not overfit to one opponent. Game logic is unchanged: this only replaces who picks the action.
/// </summary>
public class TrainingBridge : MonoBehaviour
{
    BinaryReader reader;
    BinaryWriter writer;
    BossAgent boss;
    PlayerController player;
    System.Random rng;
    bool randomize = true;

    float lastBossHP, lastPlayerHP;
    float pendingTerminal;
    int pendingDone, pendingOutcome;

    // Scene defaults, so randomisation is always around the shipped opponent.
    float baseSpeed, baseSafe, baseCooldown, baseRetreat;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string n) { int i = System.Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string port = Arg("-nemesisTrain");
        if (port == null) return;

        var b = FindObjectOfType<BossAgent>();
        var tb = new GameObject("TrainingBridge").AddComponent<TrainingBridge>();
        tb.boss = b;
        tb.player = b.player.GetComponent<PlayerController>();
        int seed = Arg("-seed") != null ? int.Parse(Arg("-seed")) : 1;
        tb.rng = new System.Random(seed);
        tb.randomize = Arg("-randomizePlayer") != "0";
        if (Arg("-obs") != null) BossAgent.TrainingObsSize = int.Parse(Arg("-obs"));   // 14 or 18
        UnityEngine.Random.InitState(seed);
        Time.timeScale = Mathf.Clamp(Arg("-timeScale") != null ? float.Parse(Arg("-timeScale"), System.Globalization.CultureInfo.InvariantCulture) : 100f, 1f, 100f);
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        Debug.unityLogger.filterLogType = LogType.Warning;
        foreach (var t in FindObjectsOfType<TauntDisplay>()) t.enabled = false;
        var fsm = b.GetComponent<ScriptedBoss>();
        if (fsm != null) fsm.enabled = false;
        b.enabled = true;

        var client = new TcpClient("127.0.0.1", int.Parse(port)) { NoDelay = true };
        var stream = client.GetStream();
        tb.reader = new BinaryReader(stream);
        tb.writer = new BinaryWriter(stream);
        BossAgent.ExternalPolicy = tb.Decide;
        EpisodeStats.Ended += tb.OnEnded;
        Debug.LogWarning($"[Train] connected to trainer on port {port}, seed {seed}, timeScale {Time.timeScale}");
    }

    void Start()
    {
        baseSpeed = player.moveSpeed;
        baseSafe = player.safeDistance;
        baseCooldown = player.attackCooldown;
        baseRetreat = player.retreatSpeed;
        lastBossHP = boss.maxHP;
        lastPlayerHP = player.maxHP;
        Randomize();
    }

    float U(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

    void Randomize()
    {
        if (!randomize) return;
        // Covers the default bot (x1.0) and the "hard" eval bot (speed x1.3, cooldown x0.75, gap x1.2).
        player.moveSpeed = baseSpeed * U(0.85f, 1.4f);
        player.safeDistance = baseSafe * U(0.85f, 1.3f);
        player.attackCooldown = baseCooldown * U(0.7f, 1.15f);
        player.retreatSpeed = baseRetreat * U(0.9f, 1.3f);
    }

    void OnEnded(EpisodeResult r)
    {
        // Damage dealt in the final moments before the episode ended.
        pendingTerminal += Shaped(r.bossHP, r.playerHP);
        pendingTerminal += r.outcome == "BOSS_WIN" ? 1f : r.outcome == "PLAYER_WIN" ? -1f : -0.5f;
        pendingDone = 1;
        pendingOutcome = r.outcome == "BOSS_WIN" ? 1 : r.outcome == "PLAYER_WIN" ? 2 : 3;
        lastBossHP = boss.maxHP;
        lastPlayerHP = player.maxHP;
        Randomize();
    }

    float Shaped(float bossHP, float playerHP)
    {
        float dealt = Mathf.Max(0f, lastPlayerHP - Mathf.Max(playerHP, 0f)) / player.maxHP;
        float taken = Mathf.Max(0f, lastBossHP - Mathf.Max(bossHP, 0f)) / boss.maxHP;
        lastPlayerHP = Mathf.Max(playerHP, 0f);
        lastBossHP = Mathf.Max(bossHP, 0f);
        return dealt - taken;
    }

    int Decide(float[] obs, float[] mask)
    {
        float reward = pendingTerminal;
        if (pendingDone == 0) reward += Shaped(boss.bossHP, player.playerHP);
        for (int i = 0; i < obs.Length; i++) writer.Write(obs[i]);
        for (int i = 0; i < 4; i++) writer.Write(mask[i]);
        writer.Write(reward);
        writer.Write(pendingDone);
        writer.Write(pendingOutcome);
        writer.Flush();
        pendingTerminal = 0f;
        pendingDone = 0;
        pendingOutcome = 0;
        int action = reader.ReadInt32();
        if (action == -1) { Application.Quit(); return 0; }   // trainer finished
        return action;
    }
}
