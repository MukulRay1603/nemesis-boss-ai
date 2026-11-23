using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Readable HUD showing what the RL boss is doing and why:
///   - boss / player HP bars, phase, fight counter
///   - current FSM state and time left in it
///   - the policy's softmax over the 4 actions for the latest decision (read from the ONNX graph),
///     with the sampled action highlighted and masked actions marked
///   - the live action mix per phase, so you can see the policy shift as the boss gets hurt
/// Built entirely in code. Not created in -nemesisEval runs, so it never affects measured timings.
/// Toggle with Tab.
/// </summary>
public class BossDebugOverlay : MonoBehaviour
{
    static readonly string[] ActionNames = { "IDLE", "ATTACK", "DODGE", "SPECIAL" };
    static readonly string[] PhaseNames = { "PHASE 1", "PHASE 2  WOUNDED", "PHASE 3  ENRAGED" };
    static readonly Color[] PhaseColors =
        { new Color(0.92f, 0.92f, 0.92f), new Color(1f, 0.55f, 0.1f), new Color(0.9f, 0.15f, 0.15f) };
    static readonly Color Panel = new Color(0.05f, 0.06f, 0.09f, 0.78f);
    static readonly Color Track = new Color(1f, 1f, 1f, 0.10f);
    static readonly Color Accent = new Color(0.35f, 0.8f, 1f);
    static readonly Color Dim = new Color(0.75f, 0.78f, 0.85f);

    BossAgent boss;
    PlayerController player;
    Canvas canvas;
    GameObject root;

    RectTransform bossFill, playerFill, stamFill;
    TextMeshProUGUI bossLabel, playerLabel, fightLabel, stateLabel, decisionLabel, mixLabel, inputsLabel;
    readonly RectTransform[] probFill = new RectTransform[4];
    readonly Image[] probImg = new Image[4];
    readonly TextMeshProUGUI[] probText = new TextMeshProUGUI[4];

    readonly int[,] phaseMix = new int[3, 4];
    int seenDecisions = -1;
    int fights = 1, bossWins, playerWins, timeouts;

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

    void Start()
    {
        player = boss.player != null ? boss.player.GetComponent<PlayerController>() : null;
        EpisodeStats.Ended += OnEnded;
        Build();
    }

    void OnDestroy() => EpisodeStats.Ended -= OnEnded;

    void OnEnded(EpisodeResult r)
    {
        if (r.outcome == "BOSS_WIN") bossWins++;
        else if (r.outcome == "PLAYER_WIN") playerWins++;
        else timeouts++;
        fights++;
    }

    // ── Layout ────────────────────────────────────────────────────────────
    void Build()
    {
        root = new GameObject("OverlayCanvas");
        root.transform.SetParent(transform);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        // Boss bar, top centre
        var bossBar = Box(root.transform, new Vector2(0.5f, 1f), new Vector2(0, -28), new Vector2(980, 76), Panel);
        bossLabel = Text(bossBar, "NEMESIS", 24, new Vector2(16, -6), new Vector2(948, 30), TextAlignmentOptions.Left);
        bossFill = Bar(bossBar, new Vector2(16, -42), new Vector2(948, 20), PhaseColors[0]);

        // Fight counter, top left
        var fightBox = Box(root.transform, new Vector2(0f, 1f), new Vector2(28, -28), new Vector2(380, 76), Panel);
        fightLabel = Text(fightBox, "", 22, new Vector2(16, -8), new Vector2(350, 64), TextAlignmentOptions.TopLeft);

        // Player bar, bottom left
        var pBox = Box(root.transform, new Vector2(0f, 0f), new Vector2(28, 28), new Vector2(520, 70), Panel);
        playerLabel = Text(pBox, "PLAYER (scripted bot)", 20, new Vector2(16, -6), new Vector2(490, 28), TextAlignmentOptions.Left);
        playerFill = Bar(pBox, new Vector2(16, -38), new Vector2(488, 18), new Color(0.3f, 0.85f, 0.5f));

        // Policy panel, right
        var brain = Box(root.transform, new Vector2(1f, 1f), new Vector2(-28, -124), new Vector2(470, 590), Panel);
        Text(brain, "BOSS POLICY  (PPO, ONNX via Sentis)", 20, new Vector2(18, -12), new Vector2(440, 28),
             TextAlignmentOptions.Left).color = Accent;
        stateLabel = Text(brain, "", 30, new Vector2(18, -46), new Vector2(440, 40), TextAlignmentOptions.Left);
        decisionLabel = Text(brain, "", 17, new Vector2(18, -88), new Vector2(440, 26), TextAlignmentOptions.Left);
        decisionLabel.color = Dim;

        Text(brain, "Action probabilities, last decision", 18, new Vector2(18, -126), new Vector2(440, 26),
             TextAlignmentOptions.Left).color = Dim;
        for (int i = 0; i < 4; i++)
        {
            float y = -160 - i * 44;
            Text(brain, ActionNames[i], 20, new Vector2(18, y), new Vector2(110, 30), TextAlignmentOptions.Left);
            probFill[i] = Bar(brain, new Vector2(130, y - 5), new Vector2(220, 22), Accent);
            probImg[i] = probFill[i].GetComponent<Image>();
            probText[i] = Text(brain, "", 19, new Vector2(358, y), new Vector2(100, 30), TextAlignmentOptions.Right);
        }

        inputsLabel = Text(brain, "", 18, new Vector2(18, -340), new Vector2(440, 56), TextAlignmentOptions.TopLeft);
        Text(brain, "Stamina", 18, new Vector2(18, -400), new Vector2(110, 26), TextAlignmentOptions.Left).color = Dim;
        stamFill = Bar(brain, new Vector2(130, -404), new Vector2(320, 16), new Color(0.95f, 0.8f, 0.25f));

        Text(brain, "Action mix by phase (this session)", 18, new Vector2(18, -440), new Vector2(440, 26),
             TextAlignmentOptions.Left).color = Dim;
        mixLabel = Text(brain, "", 18, new Vector2(18, -470), new Vector2(440, 160), TextAlignmentOptions.TopLeft);
        mixLabel.fontStyle = FontStyles.Normal;
    }

    RectTransform Box(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color c)
    {
        var go = new GameObject("Box", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        go.GetComponent<Image>().color = c;
        return rt;
    }

    // Bars and text are positioned from the parent's top-left corner.
    RectTransform Bar(RectTransform parent, Vector2 pos, Vector2 size, Color c)
    {
        var track = Box(parent, new Vector2(0, 1), pos, size, Track);
        var fill = Box(track, new Vector2(0, 1), Vector2.zero, size, c);
        return fill;
    }

    TextMeshProUGUI Text(RectTransform parent, string s, float size, Vector2 pos, Vector2 box, TextAlignmentOptions a)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = s;
        t.fontSize = size;
        t.alignment = a;
        t.color = Color.white;
        t.fontStyle = FontStyles.Bold;
        t.enableWordWrapping = false;
        return t;
    }

    static void SetFill(RectTransform fill, float frac)
    {
        var track = (RectTransform)fill.parent;
        fill.sizeDelta = new Vector2(track.sizeDelta.x * Mathf.Clamp01(frac), track.sizeDelta.y);
    }

    // ── Per-frame update ─────────────────────────────────────────────────
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) root.SetActive(!root.activeSelf);
        if (boss == null || !boss.enabled || !root.activeSelf) return;

        int phase = Mathf.Clamp(boss.currentPhase, 0, 2);
        if (boss.Decisions != seenDecisions)
        {
            if (seenDecisions >= 0) phaseMix[phase, Mathf.Clamp(boss.LastAction, 0, 3)]++;
            seenDecisions = boss.Decisions;
        }

        float bhp = Mathf.Max(0, boss.bossHP);
        bossLabel.text = $"NEMESIS   <size=18><color=#{ColorUtility.ToHtmlStringRGB(PhaseColors[phase])}>" +
                         $"{PhaseNames[phase]}</color>   {bhp:0} / {boss.maxHP:0}</size>";
        SetFill(bossFill, bhp / boss.maxHP);
        bossFill.GetComponent<Image>().color = PhaseColors[phase];

        if (player != null)
        {
            float php = Mathf.Max(0, player.playerHP);
            playerLabel.text = $"PLAYER (scripted bot)   <size=16>{php:0} / {player.maxHP:0}</size>";
            SetFill(playerFill, php / player.maxHP);
        }

        fightLabel.text = $"FIGHT {fights}   <size=18>{boss.EpisodeTime:0.0}s / 90s</size>\n" +
                          $"<size=18><color=#BFC7D9>Boss {bossWins}   Player {playerWins}   Timeout {timeouts}</color></size>";

        stateLabel.text = $"{boss.currentBossState.ToString().ToUpperInvariant()}" +
                          $"  <size=20><color=#BFC7D9>{Mathf.Max(0, boss.StateTimeLeft):0.00}s</color></size>";
        stateLabel.color = boss.currentBossState switch
        {
            BossAgent.BossState.Telegraph => new Color(1f, 0.8f, 0.2f),
            BossAgent.BossState.Attack => new Color(1f, 0.3f, 0.25f),
            BossAgent.BossState.Recovery => new Color(0.4f, 1f, 0.55f),
            BossAgent.BossState.Dodge => Accent,
            _ => Color.white,
        };
        decisionLabel.text = $"Decision #{boss.Decisions}   inference {boss.LastInferenceMs:0.00} ms ({boss.ActiveBackend})";

        for (int i = 0; i < 4; i++)
        {
            float p = boss.LastProbs[i];
            bool chosen = i == boss.LastAction;
            bool masked = boss.LastMask[i] < 0.5f;
            SetFill(probFill[i], p);
            probImg[i].color = chosen ? new Color(1f, 0.82f, 0.3f) : Accent * new Color(1, 1, 1, 0.7f);
            probText[i].text = masked ? "<size=15>masked</size>" : $"{p * 100f:0}%{(chosen ? " <" : "")}";
            probText[i].color = chosen ? new Color(1f, 0.82f, 0.3f) : Dim;
        }

        float dist = Vector3.Distance(boss.transform.position, boss.player.position);
        inputsLabel.text = $"<color=#BFC7D9>Distance to player</color>  {dist:0.0} m\n" +
                           $"<color=#BFC7D9>Speed</color>  x{(phase == 0 ? 1f : phase == 1 ? 1.3f : 1.6f):0.0}" +
                           $"   <color=#BFC7D9>Recovery</color>  {boss.recoveryDuration:0.00}s";
        SetFill(stamFill, boss.Stamina / 100f);

        var sb = new System.Text.StringBuilder("<mspace=11>      idle  atk   dodge spec   n</mspace>\n");
        for (int ph = 0; ph < 3; ph++)
        {
            int n = 0;
            for (int a = 0; a < 4; a++) n += phaseMix[ph, a];
            sb.Append($"<mspace=11>P{ph + 1}  ");
            for (int a = 0; a < 4; a++)
                sb.Append(n == 0 ? "  -   " : $"{100f * phaseMix[ph, a] / n,4:0}% ");
            sb.Append($"{n,4}</mspace>\n");
        }
        mixLabel.text = sb.ToString();
    }
}
