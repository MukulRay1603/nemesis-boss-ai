using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal HUD showing what the RL boss is doing and why. No panels: soft-shadowed type and
/// hairline bars that ease toward their values.
///   top centre   boss name, HP bar with phase ticks and a lagging damage trail, phase name
///   top left     fight number, clock, score
///   bottom left  player HP
///   right        policy column: FSM state, stamina, the softmax over the 4 actions at the last
///                decision (read from the ONNX graph), and the action mix per phase this session
///   centre       brief titles on phase changes and when a fight ends
/// Not created in -nemesisEval runs, so it never affects measured timings. Tab toggles it.
/// </summary>
public class BossDebugOverlay : MonoBehaviour
{
    static readonly string[] ActionNames = { "Idle", "Attack", "Dodge", "Special" };
    static readonly string[] PhaseRoman = { "I", "II", "III" };
    static readonly string[] PhaseWord = { "Composed", "Wounded", "Enraged" };

    BossAgent boss;
    PlayerController player;
    GameObject root;

    // boss bar
    const float BossW = 760f, BarH = 6f;
    RectTransform bossFill, bossChip;
    Image bossFillImg;
    TextMeshProUGUI bossPhase, bossNumbers;
    float bossShown = 1f, bossChipShown = 1f, bossChipHold;

    // player bar
    const float PlayerW = 360f;
    RectTransform playerFill, playerChip;
    TextMeshProUGUI playerNumbers;
    float playerShown = 1f, playerChipShown = 1f, playerChipHold;

    // corner
    TextMeshProUGUI fightLabel, clockLabel, scoreLabel;

    // policy column
    Image stateDot;
    TextMeshProUGUI stateLabel, stateTime, footnote;
    UiKit.Bar stamina;
    readonly UiKit.Bar[] probBars = new UiKit.Bar[4];
    readonly TextMeshProUGUI[] probName = new TextMeshProUGUI[4];
    readonly TextMeshProUGUI[] probPct = new TextMeshProUGUI[4];
    readonly float[] probShown = new float[4];
    readonly RectTransform[,] mixSeg = new RectTransform[3, 4];
    readonly float[,] mixShown = new float[3, 4];
    const float MixW = 284f;

    // centre title
    TextMeshProUGUI titleBig, titleSmall;
    CanvasGroup titleGroup;
    float titleT = 99f;

    readonly int[,] phaseMix = new int[3, 4];
    int seenDecisions = -1, lastPhase;
    int fights = 1, bossWins, playerWins, timeouts;
    Color stateCol = Color.white;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nemesisEval") >= 0) return;
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-noOverlay") >= 0) return;
        var b = FindObjectOfType<BossAgent>();
        if (b == null) return;
        BossAgent.CaptureProbs = true;   // runs before BossAgent.Start(), where the model is loaded
        new GameObject("BossDebugOverlay").AddComponent<BossDebugOverlay>().boss = b;
    }

    public bool Visible => root != null && root.activeSelf;
    public void SetVisible(bool v) { if (root != null) root.SetActive(v); }

    void Start()
    {
        player = boss.player != null ? boss.player.GetComponent<PlayerController>() : null;
        EpisodeStats.Ended += OnEnded;
        HideWorldLabels();
        Build();
    }

    void OnDestroy() => EpisodeStats.Ended -= OnEnded;

    // The floating "NEMESIS"/"HUNTER" world labels repeat the HUD and clutter the frame.
    void HideWorldLabels()
    {
        foreach (var t in new[] { boss.transform, boss.player })
            if (t != null)
                foreach (Transform c in t)
                    if (c.name.StartsWith("Label_")) c.gameObject.SetActive(false);
        foreach (var l in FindObjectsOfType<CombatLabel>()) l.enabled = false;
    }

    void OnEnded(EpisodeResult r)
    {
        if (r.outcome == "BOSS_WIN") { bossWins++; ShowTitle("NEMESIS PREVAILS", "The hunter falls"); }
        else if (r.outcome == "PLAYER_WIN") { playerWins++; ShowTitle("NEMESIS FALLS", "The hunter prevails"); }
        else { timeouts++; ShowTitle("TIME", "No victor"); }
        fights++;
    }

    void ShowTitle(string big, string small)
    {
        if (titleBig == null) return;
        titleBig.text = big;
        titleSmall.text = small;
        titleT = 0f;
    }

    // ── Layout ────────────────────────────────────────────────────────────
    void Build()
    {
        var canvas = UiKit.MakeCanvas(transform, "HudCanvas", 50);
        root = canvas.gameObject;
        var rt = (RectTransform)root.transform;

        // Boss: top centre
        var b = UiKit.Group(rt, "Boss", new Vector2(0.5f, 1f), new Vector2(0, -42), new Vector2(BossW, 90));
        UiKit.Label(b, "NEMESIS", 21, Vector2.zero, new Vector2(BossW, 28), TextAlignmentOptions.Center, 22f);
        UiKit.Pill(b, new Vector2(0, -38), new Vector2(BossW, BarH), UiKit.Track, "Track");
        bossChip = UiKit.Pill(b, new Vector2(0, -38), new Vector2(BossW, BarH), new Color(1f, 0.85f, 0.7f, 0.55f), "Chip").rectTransform;
        bossFillImg = UiKit.Pill(b, new Vector2(0, -38), new Vector2(BossW, BarH), UiKit.Phase[0], "Fill");
        bossFill = bossFillImg.rectTransform;
        foreach (float f in new[] { 0.33f, 0.66f })   // phase thresholds
            UiKit.Pill(b, new Vector2(BossW * f - 1f, -35), new Vector2(2f, 11f), new Color(1, 1, 1, 0.55f), "Tick");
        bossPhase = UiKit.Label(b, "", 14, new Vector2(0, -54), new Vector2(BossW, 22), TextAlignmentOptions.Center, 7f);
        bossNumbers = UiKit.Label(b, "", 13, new Vector2(0, -54), new Vector2(BossW, 22), TextAlignmentOptions.Right, 2f, UiKit.Faint);

        // Fight / clock / score: top left
        var c = UiKit.Group(rt, "Fight", new Vector2(0f, 1f), new Vector2(56, -42), new Vector2(320, 100));
        fightLabel = UiKit.Label(c, "", 13, Vector2.zero, new Vector2(320, 20), TextAlignmentOptions.Left, 9f, UiKit.Muted);
        clockLabel = UiKit.Label(c, "", 30, new Vector2(0, -20), new Vector2(320, 40), TextAlignmentOptions.Left, 2f);
        scoreLabel = UiKit.Label(c, "", 13, new Vector2(0, -62), new Vector2(320, 20), TextAlignmentOptions.Left, 3f, UiKit.Muted);

        // Player: bottom left
        var p = UiKit.Group(rt, "Player", new Vector2(0f, 0f), new Vector2(56, 64), new Vector2(PlayerW, 40));
        UiKit.Label(p, "HUNTER", 14, Vector2.zero, new Vector2(200, 22), TextAlignmentOptions.Left, 12f);
        playerNumbers = UiKit.Label(p, "", 13, Vector2.zero, new Vector2(PlayerW, 22), TextAlignmentOptions.Right, 2f, UiKit.Muted);
        UiKit.Pill(p, new Vector2(0, -30), new Vector2(PlayerW, BarH), UiKit.Track, "Track");
        playerChip = UiKit.Pill(p, new Vector2(0, -30), new Vector2(PlayerW, BarH), new Color(1f, 0.6f, 0.55f, 0.6f), "Chip").rectTransform;
        playerFill = UiKit.Pill(p, new Vector2(0, -30), new Vector2(PlayerW, BarH), new Color(0.62f, 0.93f, 0.8f), "Fill").rectTransform;

        // Policy column: right
        const float W = 320f;
        var g = UiKit.Group(rt, "Policy", new Vector2(1f, 1f), new Vector2(-56, -150), new Vector2(W, 460));
        UiKit.Label(g, "POLICY", 13, Vector2.zero, new Vector2(W, 18), TextAlignmentOptions.Left, 12f, UiKit.Muted);
        UiKit.Label(g, "PPO  ·  ONNX  ·  Sentis", 11, Vector2.zero, new Vector2(W, 18), TextAlignmentOptions.Right, 2f, UiKit.Faint);

        stateDot = UiKit.Pill(g, new Vector2(0, -44), new Vector2(10, 10), Color.white, "Dot");
        stateLabel = UiKit.Label(g, "", 26, new Vector2(20, -30), new Vector2(220, 36), TextAlignmentOptions.Left, 1f);
        stateTime = UiKit.Label(g, "", 13, new Vector2(0, -40), new Vector2(W, 20), TextAlignmentOptions.Right, 1f, UiKit.Faint);

        UiKit.Label(g, "Stamina", 11, new Vector2(0, -76), new Vector2(W, 16), TextAlignmentOptions.Left, 3f, UiKit.Faint);
        stamina = new UiKit.Bar(g, new Vector2(70, -81), W - 70, 4f, new Color(1f, 0.86f, 0.58f));

        UiKit.Label(g, "LAST DECISION", 11, new Vector2(0, -116), new Vector2(W, 16), TextAlignmentOptions.Left, 9f, UiKit.Faint);
        for (int i = 0; i < 4; i++)
        {
            float y = -140 - i * 30;
            probName[i] = UiKit.Label(g, ActionNames[i], 15, new Vector2(0, y), new Vector2(90, 22), TextAlignmentOptions.Left, 1f);
            probBars[i] = new UiKit.Bar(g, new Vector2(96, y - 8), 168, 5f, UiKit.Action[i]);
            probPct[i] = UiKit.Label(g, "", 14, new Vector2(0, y), new Vector2(W, 22), TextAlignmentOptions.Right, 1f);
        }

        UiKit.Label(g, "ACTION MIX BY PHASE", 11, new Vector2(0, -272), new Vector2(W, 16), TextAlignmentOptions.Left, 9f, UiKit.Faint);
        for (int ph = 0; ph < 3; ph++)
        {
            float y = -298 - ph * 22;
            UiKit.Label(g, PhaseRoman[ph], 12, new Vector2(0, y + 6), new Vector2(30, 18), TextAlignmentOptions.Left, 2f, UiKit.Muted);
            var track = UiKit.Pill(g, new Vector2(36, y), new Vector2(MixW, 6f), UiKit.Track, "MixTrack");
            track.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            for (int a = 0; a < 4; a++)
            {
                var seg = new GameObject("Seg", typeof(RectTransform), typeof(Image));
                seg.transform.SetParent(track.transform, false);
                var srt = (RectTransform)seg.transform;
                srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0, 0.5f);
                srt.sizeDelta = new Vector2(0, 6f);
                var img = seg.GetComponent<Image>();
                img.color = UiKit.Action[a];
                img.raycastTarget = false;
                mixSeg[ph, a] = srt;
            }
        }
        // Legend
        float lx = 36f;
        for (int a = 0; a < 4; a++)
        {
            UiKit.Pill(g, new Vector2(lx, -370), new Vector2(7, 7), UiKit.Action[a], "LegendDot");
            var l = UiKit.Label(g, ActionNames[a], 11, new Vector2(lx + 12, -364), new Vector2(70, 18), TextAlignmentOptions.Left, 1f, UiKit.Faint);
            lx += 12 + l.GetPreferredValues(ActionNames[a]).x + 16;
        }
        footnote = UiKit.Label(g, "", 11, new Vector2(0, -400), new Vector2(W, 18), TextAlignmentOptions.Left, 1f, UiKit.Faint);

        // Centre title
        var t = UiKit.Group(rt, "Title", new Vector2(0.5f, 0.5f), new Vector2(0, 200), new Vector2(1000, 90));
        titleGroup = t.gameObject.AddComponent<CanvasGroup>();
        titleGroup.alpha = 0f;
        titleBig = UiKit.Label(t, "", 34, Vector2.zero, new Vector2(1000, 46), TextAlignmentOptions.Center, 26f);
        titleSmall = UiKit.Label(t, "", 15, new Vector2(0, -50), new Vector2(1000, 24), TextAlignmentOptions.Center, 8f, UiKit.Muted);
    }

    // ── Per frame ────────────────────────────────────────────────────────
    static void SetPill(RectTransform rt, float frac, float width, float h, float alpha)
    {
        float w = Mathf.Clamp01(frac) * width;
        rt.sizeDelta = new Vector2(Mathf.Max(w, h), h);
        var img = rt.GetComponent<Image>();
        var c = img.color;
        c.a = Mathf.Clamp01(w / h) * alpha;   // fade out instead of shrinking past the round caps
        img.color = c;
    }

    // Fill eases quickly; the "chip" behind it waits, then drains slowly, so hits read clearly.
    static void Drain(ref float shown, ref float chip, ref float hold, float target, float dt)
    {
        shown = Mathf.Lerp(shown, target, 1f - Mathf.Exp(-14f * dt));
        if (target < chip - 0.001f)
        {
            hold += dt;
            if (hold > 0.45f) chip = Mathf.Lerp(chip, target, 1f - Mathf.Exp(-4f * dt));
        }
        else { chip = target; hold = 0f; }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) root.SetActive(!root.activeSelf);
        if (boss == null || !boss.enabled || !root.activeSelf) return;
        float dt = Time.deltaTime;

        int phase = Mathf.Clamp(boss.currentPhase, 0, 2);
        if (boss.Decisions != seenDecisions)
        {
            if (seenDecisions >= 0) phaseMix[phase, Mathf.Clamp(boss.LastAction, 0, 3)]++;
            seenDecisions = boss.Decisions;
        }
        if (phase > lastPhase) ShowTitle("PHASE " + PhaseRoman[phase], PhaseWord[phase]);
        lastPhase = phase;

        // Boss bar
        float bhp = Mathf.Max(0, boss.bossHP);
        Drain(ref bossShown, ref bossChipShown, ref bossChipHold, bhp / boss.maxHP, dt);
        var pc = UiKit.Phase[phase];
        bossFillImg.color = new Color(pc.r, pc.g, pc.b, bossFillImg.color.a);
        SetPill(bossFill, bossShown, BossW, BarH, 1f);
        SetPill(bossChip, bossChipShown, BossW, BarH, 0.55f);
        bossPhase.text = $"PHASE {PhaseRoman[phase]}   ·   {PhaseWord[phase].ToUpperInvariant()}";
        bossPhase.color = Color.Lerp(bossPhase.color, pc, 1f - Mathf.Exp(-6f * dt));
        bossNumbers.text = $"{bhp:0}";

        // Player bar
        if (player != null)
        {
            float php = Mathf.Max(0, player.playerHP);
            Drain(ref playerShown, ref playerChipShown, ref playerChipHold, php / player.maxHP, dt);
            SetPill(playerFill, playerShown, PlayerW, BarH, 1f);
            SetPill(playerChip, playerChipShown, PlayerW, BarH, 0.6f);
            playerNumbers.text = $"{php:0}";
        }

        // Corner
        int secs = Mathf.FloorToInt(boss.EpisodeTime);
        fightLabel.text = $"FIGHT {fights:00}";
        clockLabel.text = $"{secs / 60}:{secs % 60:00}";
        scoreLabel.text = $"Nemesis {bossWins}   ·   Hunter {playerWins}" + (timeouts > 0 ? $"   ·   Draw {timeouts}" : "");

        // State
        var s = boss.currentBossState;
        Color target = s switch
        {
            BossAgent.BossState.Telegraph => new Color(1f, 0.8f, 0.45f),
            BossAgent.BossState.Attack => new Color(1f, 0.47f, 0.42f),
            BossAgent.BossState.Recovery => new Color(0.56f, 0.95f, 0.72f),
            BossAgent.BossState.Dodge => UiKit.Action[2],
            _ => UiKit.Muted,
        };
        stateCol = Color.Lerp(stateCol, target, 1f - Mathf.Exp(-12f * dt));
        stateDot.color = stateCol;
        stateLabel.text = s.ToString();
        stateTime.text = $"{Mathf.Max(0, boss.StateTimeLeft):0.00}s";
        stamina.Set(boss.Stamina / 100f, 8f);

        // Action probabilities at the last decision
        for (int i = 0; i < 4; i++)
        {
            bool chosen = i == boss.LastAction;
            bool masked = boss.LastMask[i] < 0.5f;
            probShown[i] = Mathf.Lerp(probShown[i], boss.LastProbs[i], 1f - Mathf.Exp(-10f * dt));
            probBars[i].BaseAlpha = chosen ? 1f : 0.45f;
            probBars[i].Set(boss.LastProbs[i], 10f);
            probName[i].color = chosen ? UiKit.Text : UiKit.Muted;
            probPct[i].color = chosen ? UiKit.Text : UiKit.Faint;
            probPct[i].text = masked ? "masked" : $"{probShown[i] * 100f:0}%";
        }

        // Action mix per phase, as stacked bars
        for (int ph = 0; ph < 3; ph++)
        {
            int n = 0;
            for (int a = 0; a < 4; a++) n += phaseMix[ph, a];
            float x = 0f;
            for (int a = 0; a < 4; a++)
            {
                float f = n == 0 ? 0f : (float)phaseMix[ph, a] / n;
                mixShown[ph, a] = Mathf.Lerp(mixShown[ph, a], f, 1f - Mathf.Exp(-6f * dt));
                float w = mixShown[ph, a] * MixW;
                mixSeg[ph, a].anchoredPosition = new Vector2(x, 0);
                mixSeg[ph, a].sizeDelta = new Vector2(w, 6f);
                x += w;
            }
        }

        footnote.text = $"Decision {boss.Decisions}   ·   {boss.LastInferenceMs:0.00} ms on " +
                        (boss.ActiveBackend == Unity.Sentis.BackendType.CPU ? "CPU" : "GPU");

        // Centre title: fade in, hold, fade out, with a slight settle in scale
        titleT += dt;
        float alpha = titleT < 0.35f ? titleT / 0.35f : titleT < 1.9f ? 1f : Mathf.Clamp01(1f - (titleT - 1.9f) / 0.6f);
        titleGroup.alpha = alpha;
        titleGroup.transform.localScale = Vector3.one * (1f + 0.04f * (1f - Mathf.Clamp01(titleT / 0.5f)));
    }
}
