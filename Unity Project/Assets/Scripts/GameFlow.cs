using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Start screen and game mode.
///   Watch  the scripted bot fights the AI boss (the evaluated setup)
///   Fight  you control the hunter (WASD, Space / click to attack, Shift to dash)
/// The fight keeps running behind the start screen. Switching mode restarts the current fight.
/// Not created in -nemesisEval, -nemesisTrain or -nemesisRecord runs.
/// </summary>
public class GameFlow : MonoBehaviour
{
    public static bool HumanMode { get; private set; }
    public static event System.Action ModeChanged;
    public static bool StartScreenOpen { get; private set; }
    public static int ClosedFrame { get; private set; } = -1;

    static GameFlow instance;
    BossAgent boss;
    PlayerController player;
    CanvasGroup screen;
    bool open = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool preview = System.Array.IndexOf(args, "-startScreen") >= 0;   // show it in a recording, for stills
        foreach (var f in new[] { "-nemesisEval", "-nemesisTrain", "-nemesisRecord" })
            if (System.Array.IndexOf(args, f) >= 0 && !(preview && f == "-nemesisRecord")) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        var b = FindObjectOfType<BossAgent>();
        if (b == null || b.player == null) return;
        instance = new GameObject("GameFlow").AddComponent<GameFlow>();
        instance.boss = b;
        instance.player = b.player.GetComponent<PlayerController>();
    }

    public static void SetMode(bool human)
    {
        if (instance == null) return;
        HumanMode = human;
        instance.player.humanControlled = human;
        instance.boss.ResetEpisode();   // fresh fight in the new mode
        ModeChanged?.Invoke();
    }

    void Start()
    {
        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var canvas = UiKit.MakeCanvas(transform, "StartCanvas", 90);
        canvas.gameObject.AddComponent<GraphicRaycaster>();
        var rt = (RectTransform)canvas.transform;

        var scrim = new GameObject("Scrim", typeof(RectTransform), typeof(Image));
        scrim.transform.SetParent(rt, false);
        var srt = (RectTransform)scrim.transform;
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.offsetMin = srt.offsetMax = Vector2.zero;
        scrim.GetComponent<Image>().color = new Color(0.03f, 0.04f, 0.07f, 0.7f);
        screen = scrim.AddComponent<CanvasGroup>();

        const float W = 620f;
        var card = UiKit.Card(srt, "StartCard", new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(W, 360),
                              new Color(0.06f, 0.07f, 0.10f, 0.92f));
        UiKit.Label(card, "NEMESIS", 56, new Vector2(0, -44), new Vector2(W, 66), TextAlignmentOptions.Center, 30f);
        UiKit.Label(card, "A boss whose every move is chosen by a PPO policy,", 15, new Vector2(0, -122), new Vector2(W, 22),
                    TextAlignmentOptions.Center, 1f, UiKit.Muted);
        UiKit.Label(card, "running live in Unity through Sentis.", 15, new Vector2(0, -144), new Vector2(W, 22),
                    TextAlignmentOptions.Center, 1f, UiKit.Muted);

        MakeButton(card, "FIGHT", "you vs the AI", new Vector2(W * 0.5f - 230, -196), () => Choose(true), true);
        MakeButton(card, "WATCH", "AI vs scripted bot", new Vector2(W * 0.5f + 10, -196), () => Choose(false), false);

        UiKit.Label(card, TouchControls.Active ? "TAP A MODE" : "ENTER  fight        ESC  watch", 11, new Vector2(0, -318), new Vector2(W, 16),
                    TextAlignmentOptions.Center, 6f, UiKit.Faint);
        StartScreenOpen = true;
        PlayerController.InputBlocked = true;
    }

    void MakeButton(RectTransform parent, string title, string sub, Vector2 pos, UnityEngine.Events.UnityAction onClick, bool primary)
    {
        var b = UiKit.Card(parent, title, new Vector2(0, 1), pos, new Vector2(220, 92));
        var bg = b.parent.GetChild(0).GetComponent<Image>();   // card fill
        if (primary) bg.color = new Color(0.55f, 0.85f, 1f, 0.22f);
        var hit = b.parent.GetComponent<Image>();
        hit.raycastTarget = true;
        var btn = b.parent.gameObject.AddComponent<Button>();
        btn.targetGraphic = bg;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
        cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        cb.fadeDuration = 0.12f;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);
        UiKit.Label(b, title, 26, new Vector2(0, -18), new Vector2(220, 32), TextAlignmentOptions.Center, 14f);
        UiKit.Label(b, sub, 13, new Vector2(0, -56), new Vector2(220, 20), TextAlignmentOptions.Center, 1f, UiKit.Muted);
    }

    void Choose(bool human)
    {
        open = false;
        StartScreenOpen = false;
        ClosedFrame = Time.frameCount;
        PlayerController.InputBlocked = false;
        SetMode(human);
    }

    void Update()
    {
        if (open)
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Choose(true);
            else if (Input.GetKeyDown(KeyCode.Escape)) Choose(false);
        }
        screen.alpha = UiKit.Ease(screen.alpha, open ? 1f : 0f, 10f);
        screen.blocksRaycasts = open;
        screen.gameObject.SetActive(open || screen.alpha > 0.01f);
    }
}
