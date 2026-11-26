using System.IO;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Lets an external PPO trainer drive the boss (and optionally the hunter) inside the real game.
///
/// Boss only (training/train_ppo.py):
///   -nemesisTrain PORT  [-seed N] [-timeScale X] [-randomizePlayer 0|1] [-obs 14|18]
///   At every boss decision point the game sends one message and blocks until the action arrives:
///     Unity -> Python  obs (14 or 18 floats), 4 float mask, float reward since the last decision,
///                      int done (1 = the previous decision's episode ended; obs is a fresh episode),
///                      int outcome of that episode (0 none, 1 boss win, 2 player win, 3 timeout)
///     Python -> Unity  int action
///
/// Co-training (training/train_league.py): add  -hunter 1 [-hunterEvery 10]
///   The hunter is also an agent. It uses the human player's moveset (PlayerController's human path:
///   faster movement, 0.8 s attack cooldown, dash with i-frames) and decides every hunterEvery physics
///   steps (10 = 0.2 s, about human reaction time). Every message starts with an int kind:
///     kind 0  boss decision     same body as above, but done is always 0 (episode ends come as kind 2)
///     kind 1  hunter decision   HunterObs floats, HunterActions float mask, float reward, int 0, int 0
///     kind 2  episode end       float boss reward, float hunter reward, int outcome, int hunterWasAgent
///   Reply: int action. For kind 2 the reply sets the next episode's hunter: 1 agent, 0 scripted bot.
///
/// Reward per decision: damage dealt as a fraction of the opponent's max HP minus damage taken as a
/// fraction of one's own max HP. Episode end: boss +1 win, -1 loss, -0.5 timeout; hunter +1 win,
/// -1 loss, -0.25 timeout. The scripted bot's stats are randomised per episode (unless -randomizePlayer 0).
/// Game logic is unchanged: this only replaces who picks the actions.
/// </summary>
[DefaultExecutionOrder(-50)]   // hunter actions are set before PlayerController.FixedUpdate runs
public class TrainingBridge : MonoBehaviour
{
    public const int HunterObs = 19;
    public const int HunterActions = 14;

    BinaryReader reader;
    BinaryWriter writer;
    BossAgent boss;
    PlayerController player;
    System.Random rng;
    bool randomize = true;

    // boss reward bookkeeping
    float lastBossHP, lastPlayerHP;
    float pendingTerminal;
    int pendingDone, pendingOutcome;

    // hunter (co-training)
    bool hunterMode;
    bool hunterAgentThisEpisode;
    int hunterEvery = 10, hunterStep;
    int hunterAction;
    float hLastBossHP, hLastPlayerHP;
    readonly float[] hObs = new float[HunterObs];
    readonly float[] hMask = new float[HunterActions];

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
        tb.hunterMode = Arg("-hunter") == "1";
        if (Arg("-hunterEvery") != null) tb.hunterEvery = Mathf.Max(1, int.Parse(Arg("-hunterEvery")));
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
        Debug.LogWarning($"[Train] connected to trainer on port {port}, seed {seed}, timeScale {Time.timeScale}, hunter {(tb.hunterMode ? "agent" : "scripted")}");
    }

    void Start()
    {
        baseSpeed = player.moveSpeed;
        baseSafe = player.safeDistance;
        baseCooldown = player.attackCooldown;
        baseRetreat = player.retreatSpeed;
        lastBossHP = hLastBossHP = boss.maxHP;
        lastPlayerHP = hLastPlayerHP = player.maxHP;
        hunterAgentThisEpisode = hunterMode;   // first episode: agent hunter
        player.agentControlled = hunterAgentThisEpisode;
        if (!hunterAgentThisEpisode) Randomize();
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

    void RestoreBaseStats()
    {
        player.moveSpeed = baseSpeed;
        player.safeDistance = baseSafe;
        player.attackCooldown = baseCooldown;
        player.retreatSpeed = baseRetreat;
    }

    void OnEnded(EpisodeResult r)
    {
        float bossTerminal = Shaped(ref lastBossHP, ref lastPlayerHP, r.bossHP, r.playerHP, true);
        bossTerminal += r.outcome == "BOSS_WIN" ? 1f : r.outcome == "PLAYER_WIN" ? -1f : -0.5f;
        int outcome = r.outcome == "BOSS_WIN" ? 1 : r.outcome == "PLAYER_WIN" ? 2 : 3;
        lastBossHP = hLastBossHP = boss.maxHP;
        lastPlayerHP = hLastPlayerHP = player.maxHP;

        if (!hunterMode)
        {
            pendingTerminal += bossTerminal;
            pendingDone = 1;
            pendingOutcome = outcome;
            Randomize();
            return;
        }

        float hunterTerminal = 0f;
        if (hunterAgentThisEpisode)
        {
            float hb = hLastBossHP, hp = hLastPlayerHP;
            hunterTerminal = Shaped(ref hb, ref hp, r.bossHP, r.playerHP, false);
            hunterTerminal += r.outcome == "PLAYER_WIN" ? 1f : r.outcome == "BOSS_WIN" ? -1f : -0.25f;
        }
        bossTerminal += pendingTerminal;
        pendingTerminal = 0f;

        writer.Write(2);
        writer.Write(bossTerminal);
        writer.Write(hunterTerminal);
        writer.Write(outcome);
        writer.Write(hunterAgentThisEpisode ? 1 : 0);
        writer.Flush();
        int next = reader.ReadInt32();
        if (next == -1) { Application.Quit(); return; }

        hunterAgentThisEpisode = next == 1;
        player.agentControlled = hunterAgentThisEpisode;
        if (hunterAgentThisEpisode) RestoreBaseStats(); else Randomize();
        hunterStep = 0;
        hunterAction = 0;
    }

    // Damage dealt minus damage taken, each as a fraction of the receiver's max HP, from the
    // point of view of the boss (forBoss) or the hunter. Updates the HP trackers.
    float Shaped(ref float lastB, ref float lastP, float bossHP, float playerHP, bool forBoss)
    {
        float toPlayer = Mathf.Max(0f, lastP - Mathf.Max(playerHP, 0f)) / player.maxHP;
        float toBoss = Mathf.Max(0f, lastB - Mathf.Max(bossHP, 0f)) / boss.maxHP;
        lastP = Mathf.Max(playerHP, 0f);
        lastB = Mathf.Max(bossHP, 0f);
        return forBoss ? toPlayer - toBoss : toBoss - toPlayer;
    }

    int Decide(float[] obs, float[] mask)
    {
        float reward = pendingTerminal;
        if (pendingDone == 0) reward += Shaped(ref lastBossHP, ref lastPlayerHP, boss.bossHP, player.playerHP, true);
        if (hunterMode) writer.Write(0);
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

    // ── Hunter agent ────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (!hunterMode || !hunterAgentThisEpisode || writer == null) return;

        if (hunterStep % hunterEvery == 0)
        {
            WriteHunterObs();
            float reward = Shaped(ref hLastBossHP, ref hLastPlayerHP, boss.bossHP, player.playerHP, false);
            writer.Write(1);
            for (int i = 0; i < HunterObs; i++) writer.Write(hObs[i]);
            for (int i = 0; i < HunterActions; i++) writer.Write(hMask[i]);
            writer.Write(reward);
            writer.Write(0);
            writer.Write(0);
            writer.Flush();
            int a = reader.ReadInt32();
            if (a == -1) { Application.Quit(); return; }
            hunterAction = Mathf.Clamp(a, 0, HunterActions - 1);
            if (hunterAction == 9 || hunterAction == 10) player.agentAttack = true;
            if (hunterAction >= 11) player.agentDash = true;
        }
        hunterStep++;
        player.agentMove = HunterMove(hunterAction);
    }

    // Actions are relative to the boss, recomputed every physics step while the action is held:
    // 0 hold, 1 toward, 2 away, 3 strafe left, 4 strafe right, 5 toward-left, 6 toward-right,
    // 7 away-left, 8 away-right, 9 attack while closing in, 10 attack in place,
    // 11 dash left, 12 dash right, 13 dash away.
    Vector3 HunterMove(int a)
    {
        Vector3 to = boss.transform.position - player.transform.position; to.y = 0f;
        to = to.sqrMagnitude > 1e-6f ? to.normalized : Vector3.forward;
        Vector3 left = new Vector3(-to.z, 0f, to.x);
        Vector3 m;
        switch (a)
        {
            case 1: case 9: m = to; break;
            case 2: case 13: m = -to; break;
            case 3: case 11: m = left; break;
            case 4: case 12: m = -left; break;
            case 5: m = to + left; break;
            case 6: m = to - left; break;
            case 7: m = -to + left; break;
            case 8: m = -to - left; break;
            default: m = Vector3.zero; break;
        }
        return m.sqrMagnitude > 1e-6f ? m.normalized : Vector3.zero;
    }

    void WriteHunterObs()
    {
        Vector3 pp = player.transform.position, bp = boss.transform.position;
        float lim = Mathf.Max(1f, player.arenaLimit);
        hObs[0] = Vector3.Distance(new Vector3(pp.x, 0f, pp.z), new Vector3(bp.x, 0f, bp.z)) / 10f;
        hObs[1] = pp.x / lim;
        hObs[2] = pp.z / lim;
        hObs[3] = bp.x / lim;
        hObs[4] = bp.z / lim;
        hObs[5] = Mathf.Max(player.playerHP, 0f) / player.maxHP;
        hObs[6] = Mathf.Max(boss.bossHP, 0f) / boss.maxHP;
        int s = (int)boss.currentBossState;
        for (int i = 0; i < 5; i++) hObs[7 + i] = i == s ? 1f : 0f;
        hObs[12] = Mathf.Clamp01(boss.StateTimeLeft / 1.5f);
        // The telegraph ring shows which attack is coming, so the hunter may know it too.
        bool winding = boss.currentBossState == BossAgent.BossState.Telegraph || boss.currentBossState == BossAgent.BossState.Attack;
        hObs[13] = winding && boss.LastAction == 3 ? 1f : 0f;
        hObs[14] = Mathf.Clamp01(boss.Stamina / 100f);
        hObs[15] = boss.currentPhase / 2f;
        hObs[16] = player.AttackReady ? 1f : 0f;
        hObs[17] = player.DashReady ? 1f : 0f;
        hObs[18] = boss.EpisodeTime / 90f;

        for (int i = 0; i < HunterActions; i++) hMask[i] = 1f;
        if (!player.AttackReady) hMask[9] = hMask[10] = 0f;
        if (!player.DashReady) hMask[11] = hMask[12] = hMask[13] = 0f;
    }
}
