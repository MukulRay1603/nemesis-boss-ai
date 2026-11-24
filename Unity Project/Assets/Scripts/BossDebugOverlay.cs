using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Broadcast-style HUD ("versus" layout) that also shows what the RL boss is thinking.
///   top card          NEMESIS vs HUNTER health bars facing off, clock and fight number in the middle
///   bottom-right card the boss's state, its chosen next move with probability (softmax read from the
///                     ONNX graph), the alternatives, a scrolling 30 s timeline of every decision with
///                     phase markers, and the action mix per phase this session
///   centre            short titles on phase changes and when a fight ends
///   bottom-left card  controls, in play mode
/// Not created in -nemesisEval runs, so it never affects measured timings. Tab toggles it.
/// </summary>
public class BossDebugOverlay : MonoBehaviour
{
    static readonly string[] ActionNames = { "IDLE", "ATTACK", "DODGE", "SPECIAL" };
    static readonly string[] ActionShort = { "Idle", "Attack", "Dodge", "Special" };
    static readonly string[] PhaseRoman = { "I", "II", "III" };
    static readonly string[] PhaseWord = { "Composed", "Wounded", "Enraged" };
    const float Window = 30f;   // seconds shown on the timeline

    BossAgent boss;
    PlayerController player;
    GameObject root;

    // versus card
    const float BarW = 520f, BarH = 14f, TopW = 1320f;
    RectTransform bossFill, bossChip, playerFill, playerChip;
    Image bossFillImg;
    TextMeshProUGUI bossPhase, bossHP, playerSub, playerHP, clock, fightNo, score;
    float bossShown = 1f, bossChipShown = 1f, bossHold, playerShown = 1f, playerChipShown = 1f, playerHold;

    // AI card
    const float CardW = 460f, Inner = 412f;
    Image stateDot, moveBar;
    TextMeshProUGUI stateLabel, stateTime, moveName, movePct, footnote;
    RectTransform moveFill;
    readonly TextMeshProUGUI[] chipText = new TextMeshProUGUI[3];
    readonly Image[] chipDot = new Image[3];
    float moveShown;
    Color stateCol = Color.white;

    // timeline
    RectTransform timeline;
    readonly List<(float t, int a)> decisions = new List<(float, int)>();
    readonly List<(float t, string label)> markers = new List<(float, string)>();
    readonly List<Image> tickPool = new List<Image>();
    readonly List<(Image line, TextMeshProUGUI label)> markerPool = new List<(Image, TextMeshProUGUI)>();

    // mix
    readonly RectTransform[,] mixSeg = new RectTransform[3, 4];
    readonly float[,] mixShown = new float[3, 4];
    const float MixW = 92f;
    readonly int[,] phaseMix = new int[3, 4];

    // Compact layout (phones/tablets): fewer details, smaller card.
    bool compact;
    readonly System.Collections.Generic.List<GameObject> detail = new System.Collections.Generic.List<GameObject>();
    readonly System.Collections.Generic.List<RectTransform> lowerBlock = new System.Collections.Generic.List<RectTransform>();
    RectTransform cardRoot;

    // opponent profile row (models with 18 inputs only)
    GameObject profileRow;
    readonly UiKit.Bar[] profileBars = new UiKit.Bar[4];

    // controls card (play mode)
    CanvasGroup controls;

    // centre title
    TextMeshProUGUI titleBig, titleSmall;
    CanvasGroup titleGroup;
    float titleT = 99f;

    int seenDecisions = -1, lastPhase;
    int fights = 1, bossWins, playerWins, timeouts;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nemesisEval") >= 0) return;
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-nemesisTrain") >= 0) return;
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

    void HideWorldLabels()
    {
        foreach (var t in new[] { boss.transform, boss.player })
            if (t != null)
                foreach (Transform c in t)
                    if (c.name.StartsWith("Label_")) c.gameObject.SetActive(false);
        foreach (var l in FindObjectsOfType<CombatLabel>()) l.enabled = false;
    }

    bool Human => player != null && player.humanControlled;

    void OnEnded(EpisodeResult r)
    {
        string you = Human ? "You" : "The hunter";
        if (r.outcome == "BOSS_WIN") { bossWins++; ShowTitle("NEMESIS PREVAILS", Human ? "You fall" : "The hunter falls"); }
        else if (r.outcome == "PLAYER_WIN") { playerWins++; ShowTitle("NEMESIS FALLS", $"{you} prevail" + (Human ? "" : "s")); }
        else { timeouts++; ShowTitle("TIME", "No victor"); }
        fights++;
        markers.Add((Time.time, $"FIGHT {fights:00}"));
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
        compact = TouchControls.Active || Application.isMobilePlatform;
        var canvas = UiKit.MakeCanvas(transform, "HudCanvas", 50);
        root = canvas.gameObject;
        var rt = (RectTransform)root.transform;

        // ── Versus card, top centre
        var top = UiKit.Card(rt, "Versus", new Vector2(0.5f, 1f), new Vector2(0, -26), new Vector2(TopW, 112));
        // Phones: scale the versus card down so the top-right MENU button has its own space.
        if (compact) top.parent.localScale = Vector3.one * 0.86f;
        float lx = 32f, rx = TopW - 32f - BarW;
        UiKit.Label(top, "NEMESIS", 24, new Vector2(lx, -16), new Vector2(BarW, 30), TextAlignmentOptions.Left, 12f);
        detail.Add(UiKit.Label(top, "BOSS · PPO POLICY", 11, new Vector2(lx, -22), new Vector2(BarW, 20), TextAlignmentOptions.Right, 6f, UiKit.Faint).gameObject);
        UiKit.Pill(top, new Vector2(lx, -54), new Vector2(BarW, BarH), UiKit.Track, "Track");
        bossChip = UiKit.Pill(top, new Vector2(lx, -54), new Vector2(BarW, BarH), new Color(1f, 0.9f, 0.75f, 0.5f), "Chip").rectTransform;
        bossFillImg = UiKit.Pill(top, new Vector2(lx, -54), new Vector2(BarW, BarH), UiKit.Phase[0], "Fill");
        bossFill = bossFillImg.rectTransform;
        foreach (float f in new[] { 0.33f, 0.66f })
            UiKit.Pill(top, new Vector2(lx + BarW * f - 1f, -50), new Vector2(2f, 22f), new Color(1, 1, 1, 0.6f), "Tick");
        bossPhase = UiKit.Label(top, "", 13, new Vector2(lx, -78), new Vector2(BarW, 20), TextAlignmentOptions.Left, 5f);
        bossHP = UiKit.Label(top, "", 13, new Vector2(lx, -78), new Vector2(BarW, 20), TextAlignmentOptions.Right, 2f, UiKit.Muted);

        UiKit.Label(top, "HUNTER", 24, new Vector2(rx, -16), new Vector2(BarW, 30), TextAlignmentOptions.Right, 12f);
        playerSub = UiKit.Label(top, "", 11, new Vector2(rx, -22), new Vector2(BarW, 20), TextAlignmentOptions.Left, 6f, UiKit.Faint);
        UiKit.Pill(top, new Vector2(rx, -54), new Vector2(BarW, BarH), UiKit.Track, "Track");
        playerChip = UiKit.Pill(top, new Vector2(rx, -54), new Vector2(BarW, BarH), new Color(1f, 0.65f, 0.6f, 0.55f), "Chip").rectTransform;
        playerFill = UiKit.Pill(top, new Vector2(rx, -54), new Vector2(BarW, BarH), new Color(0.55f, 0.85f, 1f), "Fill").rectTransform;
        playerHP = UiKit.Label(top, "", 13, new Vector2(rx, -78), new Vector2(BarW, 20), TextAlignmentOptions.Left, 2f, UiKit.Muted);

        float cx = TopW * 0.5f - 100f;
        fightNo = UiKit.Label(top, "", 11, new Vector2(cx, -16), new Vector2(200, 18), TextAlignmentOptions.Center, 8f, UiKit.Muted);
        clock = UiKit.Label(top, "", 38, new Vector2(cx, -32), new Vector2(200, 46), TextAlignmentOptions.Center, 3f);
        score = UiKit.Label(top, "", 12, new Vector2(cx, -80), new Vector2(200, 18), TextAlignmentOptions.Center, 4f, UiKit.Muted);

        // ── Boss AI card, bottom right
        var card = UiKit.Card(rt, "BossAI", new Vector2(1f, 0f), new Vector2(-40, 40), new Vector2(CardW, 352));
        if (TouchControls.Active)
        {
            // The right thumb needs the bottom-right corner: move the card up under the versus card, smaller.
            var cardRoot = (RectTransform)card.parent;
            cardRoot.anchorMin = cardRoot.anchorMax = cardRoot.pivot = new Vector2(1f, 1f);
            cardRoot.anchoredPosition = new Vector2(-40, -156);
            cardRoot.localScale = Vector3.one * 0.8f;
        }
        float px = 24f;
        UiKit.Label(card, "BOSS AI", 12, new Vector2(px, -20), new Vector2(Inner, 18), TextAlignmentOptions.Left, 10f, UiKit.Muted);
        detail.Add(UiKit.Label(card, "PPO  ·  ONNX  ·  Sentis", 11, new Vector2(px, -20), new Vector2(Inner, 18), TextAlignmentOptions.Right, 2f, UiKit.Faint).gameObject);
        stateDot = UiKit.Pill(card, new Vector2(px, -52), new Vector2(10, 10), Color.white, "Dot");
        stateLabel = UiKit.Label(card, "", 16, new Vector2(px + 18, -45), new Vector2(240, 24), TextAlignmentOptions.Left, 6f);
        stateTime = UiKit.Label(card, "", 13, new Vector2(px, -47), new Vector2(Inner, 20), TextAlignmentOptions.Right, 1f, UiKit.Faint);

        UiKit.Label(card, "NEXT MOVE", 11, new Vector2(px, -84), new Vector2(Inner, 16), TextAlignmentOptions.Left, 8f, UiKit.Faint);
        moveName = UiKit.Label(card, "", 40, new Vector2(px, -100), new Vector2(Inner, 48), TextAlignmentOptions.Left, 4f);
        movePct = UiKit.Label(card, "", 40, new Vector2(px, -100), new Vector2(Inner, 48), TextAlignmentOptions.Right, 1f);
        UiKit.Pill(card, new Vector2(px, -154), new Vector2(Inner, 10f), UiKit.Track, "Track");
        moveBar = UiKit.Pill(card, new Vector2(px, -154), new Vector2(Inner, 10f), UiKit.Action[1], "Fill");
        moveFill = moveBar.rectTransform;
        for (int i = 0; i < 3; i++)
        {
            float x = px + i * 138f;
            chipDot[i] = UiKit.Pill(card, new Vector2(x, -183), new Vector2(8, 8), Color.white, "ChipDot");
            chipText[i] = UiKit.Label(card, "", 14, new Vector2(x + 14, -176), new Vector2(120, 22), TextAlignmentOptions.Left, 1f, UiKit.Muted);
            detail.Add(chipDot[i].gameObject); detail.Add(chipText[i].gameObject);
        }

        lowerBlock.Add(UiKit.Pill(card, new Vector2(px, -212), new Vector2(Inner, 1f), UiKit.Track, "Divider").rectTransform);
        lowerBlock.Add(UiKit.Label(card, "LAST 30 SECONDS", 11, new Vector2(px, -226), new Vector2(Inner, 16), TextAlignmentOptions.Left, 8f, UiKit.Faint).rectTransform);
        footnote = UiKit.Label(card, "", 11, new Vector2(px, -226), new Vector2(Inner, 16), TextAlignmentOptions.Right, 1f, UiKit.Faint);
        detail.Add(footnote.gameObject);
        var tl = UiKit.Pill(card, new Vector2(px, -258), new Vector2(Inner, 28f), new Color(1, 1, 1, 0.06f), "Timeline");
        timeline = tl.rectTransform;
        lowerBlock.Add(timeline);
        detail.Add(UiKit.Label(card, "now", 10, new Vector2(px, -288), new Vector2(Inner, 14), TextAlignmentOptions.Right, 2f, UiKit.Faint).gameObject);
        detail.Add(UiKit.Label(card, "-30 s", 10, new Vector2(px, -288), new Vector2(Inner, 14), TextAlignmentOptions.Left, 2f, UiKit.Faint).gameObject);

        // per-phase mix, one compact row
        detail.Add(UiKit.Label(card, "MIX", 11, new Vector2(px, -312), new Vector2(40, 16), TextAlignmentOptions.Left, 6f, UiKit.Faint).gameObject);
        for (int ph = 0; ph < 3; ph++)
        {
            float x = px + 48f + ph * 122f;
            detail.Add(UiKit.Label(card, PhaseRoman[ph], 11, new Vector2(x, -312), new Vector2(24, 16), TextAlignmentOptions.Right, 1f, UiKit.Muted).gameObject);
            var track = UiKit.Pill(card, new Vector2(x + 30, -317), new Vector2(MixW, 6f), UiKit.Track, "MixTrack");
            detail.Add(track.gameObject);
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

        // Opponent profile: what an 18-input (adaptive) model reads about whoever it is fighting.
        profileRow = new GameObject("Profile", typeof(RectTransform));
        profileRow.transform.SetParent(card, false);
        var prt = (RectTransform)profileRow.transform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0, 1);
        prt.anchoredPosition = Vector2.zero;
        prt.sizeDelta = new Vector2(CardW, 60);
        UiKit.Label(prt, "HOW IT READS YOU", 11, new Vector2(px, -344), new Vector2(Inner, 16), TextAlignmentOptions.Left, 8f, UiKit.Faint);
        string[] pn = { "Aggression", "Evasion", "Range", "Pressure" };
        for (int i = 0; i < 4; i++)
        {
            float x = px + i * 104f;
            UiKit.Label(prt, pn[i], 11, new Vector2(x, -364), new Vector2(96, 16), TextAlignmentOptions.Left, 1f, UiKit.Muted);
            profileBars[i] = new UiKit.Bar(prt, new Vector2(x, -384), 92f, 4f, new Color(1f, 0.82f, 0.5f));
        }
        profileRow.SetActive(false);

        cardRoot = (RectTransform)card.parent;
        if (compact)
        {
            foreach (var go in detail) go.SetActive(false);
            foreach (var r in lowerBlock) r.anchoredPosition += new Vector2(0, 40f);   // close the gap left by the chips
            cardRoot.sizeDelta = new Vector2(CardW, 256);
        }

        // ── Controls card, bottom left (play mode only)
        var ctl = UiKit.Card(rt, "Controls", new Vector2(0f, 0f), new Vector2(40, 40), new Vector2(560, 52));
        UiKit.Label(ctl, "WASD  move        SPACE / CLICK  attack        SHIFT  dodge", 13, new Vector2(24, -16),
                    new Vector2(520, 20), TextAlignmentOptions.Left, 3f, UiKit.Muted);
        controls = ctl.parent.gameObject.AddComponent<CanvasGroup>();

        // ── Centre title
        var t = UiKit.Group(rt, "Title", new Vector2(0.5f, 0.5f), new Vector2(0, 150), new Vector2(1000, 90));
        titleGroup = t.gameObject.AddComponent<CanvasGroup>();
        titleGroup.alpha = 0f;
        titleBig = UiKit.Label(t, "", 40, Vector2.zero, new Vector2(1000, 52), TextAlignmentOptions.Center, 26f);
        titleSmall = UiKit.Label(t, "", 16, new Vector2(0, -56), new Vector2(1000, 24), TextAlignmentOptions.Center, 8f, UiKit.Muted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    static void SetBar(RectTransform rt, float x0, float frac, float width, float h, float alpha, bool fromRight)
    {
        float w = Mathf.Clamp01(frac) * width;
        float drawn = Mathf.Max(w, h);
        rt.sizeDelta = new Vector2(drawn, h);
        rt.anchoredPosition = new Vector2(fromRight ? x0 + width - drawn : x0, rt.anchoredPosition.y);
        var img = rt.GetComponent<Image>();
        var c = img.color;
        c.a = Mathf.Clamp01(w / h) * alpha;
        img.color = c;
    }

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

    Image Tick(int i)
    {
        while (tickPool.Count <= i)
        {
            var img = UiKit.Pill(timeline, Vector2.zero, new Vector2(6, 18), Color.white, "Tick");
            tickPool.Add(img);
        }
        return tickPool[i];
    }

    (Image, TextMeshProUGUI) Marker(int i)
    {
        while (markerPool.Count <= i)
        {
            var line = UiKit.Pill(timeline, Vector2.zero, new Vector2(1.5f, 34), new Color(1, 1, 1, 0.55f), "Marker");
            var lab = UiKit.Label(timeline, "", 9, Vector2.zero, new Vector2(80, 12), TextAlignmentOptions.Left, 2f, UiKit.Muted);
            markerPool.Add((line, lab));
        }
        return markerPool[i];
    }

    // ── Per frame ────────────────────────────────────────────────────────
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) root.SetActive(!root.activeSelf);
        if (boss == null || !boss.enabled || !root.activeSelf) return;
        float dt = Time.deltaTime, now = Time.time;

        int phase = Mathf.Clamp(boss.currentPhase, 0, 2);
        if (boss.Decisions != seenDecisions)
        {
            if (seenDecisions >= 0)
            {
                int a = Mathf.Clamp(boss.LastAction, 0, 3);
                phaseMix[phase, a]++;
                decisions.Add((now, a));
            }
            seenDecisions = boss.Decisions;
        }
        if (phase > lastPhase)
        {
            ShowTitle("PHASE " + PhaseRoman[phase], PhaseWord[phase]);
            markers.Add((now, "PHASE " + PhaseRoman[phase]));
        }
        lastPhase = phase;

        // Versus card
        float lx = 32f, rx = TopW - 32f - BarW;
        float bhp = Mathf.Max(0, boss.bossHP);
        Drain(ref bossShown, ref bossChipShown, ref bossHold, bhp / boss.maxHP, dt);
        var pc = UiKit.Phase[phase];
        bossFillImg.color = new Color(pc.r, pc.g, pc.b, bossFillImg.color.a);
        SetBar(bossFill, lx, bossShown, BarW, BarH, 1f, false);
        SetBar(bossChip, lx, bossChipShown, BarW, BarH, 0.5f, false);
        bossPhase.text = $"PHASE {PhaseRoman[phase]}  ·  {PhaseWord[phase].ToUpperInvariant()}";
        bossPhase.color = Color.Lerp(bossPhase.color, pc, 1f - Mathf.Exp(-6f * dt));
        bossHP.text = $"{bhp:0} / {boss.maxHP:0}";
        if (player != null)
        {
            float php = Mathf.Max(0, player.playerHP);
            Drain(ref playerShown, ref playerChipShown, ref playerHold, php / player.maxHP, dt);
            SetBar(playerFill, rx, playerShown, BarW, BarH, 1f, true);
            SetBar(playerChip, rx, playerChipShown, BarW, BarH, 0.55f, true);
            playerHP.text = $"{php:0} / {player.maxHP:0}";
            playerSub.text = Human ? "YOU" : "SCRIPTED BOT";
        }
        int secs = Mathf.FloorToInt(boss.EpisodeTime);
        clock.text = $"{secs / 60}:{secs % 60:00}";
        fightNo.text = $"FIGHT {fights:00}";
        score.text = $"{bossWins}   ·   {playerWins}";

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
        stateLabel.text = s.ToString().ToUpperInvariant();
        stateTime.text = $"{Mathf.Max(0, boss.StateTimeLeft):0.00} s";

        // Next move
        int chosen = Mathf.Clamp(boss.LastAction, 0, 3);
        float p = boss.LastProbs[chosen];
        moveShown = Mathf.Lerp(moveShown, p, 1f - Mathf.Exp(-10f * dt));
        moveName.text = ActionNames[chosen];
        moveName.color = Color.Lerp(moveName.color, UiKit.Action[chosen], 1f - Mathf.Exp(-12f * dt));
        movePct.text = $"{moveShown * 100f:0}%";
        moveBar.color = new Color(UiKit.Action[chosen].r, UiKit.Action[chosen].g, UiKit.Action[chosen].b, moveBar.color.a);
        SetBar(moveFill, 24f, moveShown, Inner, 10f, 1f, false);
        int k = 0;
        for (int a = 0; a < 4; a++)
        {
            if (a == chosen) continue;
            chipDot[k].color = UiKit.Action[a];
            chipText[k].text = boss.LastMask[a] < 0.5f ? $"{ActionShort[a]}  masked" : $"{ActionShort[a]}  {boss.LastProbs[a] * 100f:0}%";
            k++;
        }
        footnote.text = $"decision {boss.Decisions}  ·  {boss.LastInferenceMs:0.00} ms";

        // Timeline: one tick per decision in the last 30 s, newest on the right
        decisions.RemoveAll(d => now - d.t > Window + 1f);
        markers.RemoveAll(m => now - m.t > Window + 1f);
        int i = 0;
        foreach (var d in decisions)
        {
            float x = (1f - (now - d.t) / Window) * Inner;
            if (x < 0) continue;
            var img = Tick(i++);
            img.gameObject.SetActive(true);
            img.rectTransform.anchoredPosition = new Vector2(Mathf.Clamp(x - 3f, 0f, Inner - 6f), -5f);
            float age = Mathf.Clamp01((now - d.t) / Window);
            var c = UiKit.Action[d.a];
            img.color = new Color(c.r, c.g, c.b, Mathf.Lerp(1f, 0.45f, age));
        }
        for (; i < tickPool.Count; i++) tickPool[i].gameObject.SetActive(false);
        int m = 0;
        foreach (var mk in markers)
        {
            float x = (1f - (now - mk.t) / Window) * Inner;
            if (x < 0) continue;
            var (line, lab) = Marker(m++);
            line.gameObject.SetActive(true);
            lab.gameObject.SetActive(true);
            line.rectTransform.anchoredPosition = new Vector2(x, 3f);
            lab.rectTransform.anchoredPosition = new Vector2(Mathf.Min(x + 4f, Inner - 60f), 15f);
            lab.text = mk.label;
        }
        for (; m < markerPool.Count; m++) { markerPool[m].line.gameObject.SetActive(false); markerPool[m].label.gameObject.SetActive(false); }

        // Mix per phase
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

        controls.alpha = Mathf.Lerp(controls.alpha, Human && !TouchControls.Active ? 1f : 0f, 1f - Mathf.Exp(-6f * dt));

        bool showProfile = boss.UsesProfile && !compact;
        if (profileRow.activeSelf != showProfile)
        {
            profileRow.SetActive(showProfile);
            cardRoot.sizeDelta = new Vector2(CardW, showProfile ? 404 : 352);
        }
        if (showProfile)
        {
            var pr = boss.Profile;
            profileBars[0].Set(pr.Aggression, 6f);
            profileBars[1].Set(pr.Evasion, 6f);
            profileBars[2].Set(pr.Range, 6f);
            profileBars[3].Set(pr.Pressure, 6f);
        }

        titleT += dt;
        float alpha = titleT < 0.35f ? titleT / 0.35f : titleT < 1.9f ? 1f : Mathf.Clamp01(1f - (titleT - 1.9f) / 0.6f);
        titleGroup.alpha = alpha;
        titleGroup.transform.localScale = Vector3.one * (1f + 0.04f * (1f - Mathf.Clamp01(titleT / 0.5f)));
    }
}
