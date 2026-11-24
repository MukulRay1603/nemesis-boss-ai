using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// In-game settings (Esc), in the same minimal style as the HUD: a soft scrim instead of a panel,
/// segmented choices with a sliding underline, and pill switches.
///   Graphics  Auto / Low / Medium / High / Ultra
///   Camera    Third person / Broadcast
///   Display   AI overlay, FPS counter
/// Also shows a small FPS readout and a key hint that fades out after a few seconds.
/// Not created in -nemesisEval or -nemesisRecord runs.
/// </summary>
public class SettingsMenu : MonoBehaviour
{
    class Segmented
    {
        public readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
        public RectTransform underline;
        public int selected;
        public float x, w;
    }

    class Switch
    {
        public Image track;
        public RectTransform knob;
        public bool on;
        public float t;
    }

    CanvasGroup menu;
    bool open;
    TextMeshProUGUI fpsText, hint, autoNote;
    CanvasGroup hintGroup;
    float hintT;
    Segmented quality, camera, mode;
    Switch overlaySwitch, fpsSwitch;
    bool showFps = true;
    float fpsAccum; int fpsFrames;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool preview = System.Array.IndexOf(args, "-menuPreview") >= 0;   // open the menu, e.g. to record a still of it
        if (System.Array.IndexOf(args, "-nemesisEval") >= 0) return;
        if (System.Array.IndexOf(args, "-nemesisRecord") >= 0 && !preview) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        new GameObject("SettingsMenu").AddComponent<SettingsMenu>();
    }

    void Start()
    {
        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var canvas = UiKit.MakeCanvas(transform, "SettingsCanvas", 100);
        canvas.gameObject.AddComponent<GraphicRaycaster>();
        var rt = (RectTransform)canvas.transform;

        fpsText = UiKit.Label(rt, "", 12, new Vector2(-56, 40), new Vector2(400, 18), TextAlignmentOptions.Right, 3f,
                              UiKit.Faint, new Vector2(1f, 0f));
        hint = UiKit.Label(rt, "ESC  settings        TAB  overlay        C  camera", 12, new Vector2(0, 40),
                           new Vector2(800, 18), TextAlignmentOptions.Center, 4f, UiKit.Muted, new Vector2(0.5f, 0f));
        hintGroup = hint.gameObject.AddComponent<CanvasGroup>();

        // Menu: soft scrim + centred content
        var m = new GameObject("Menu", typeof(RectTransform), typeof(Image));
        m.transform.SetParent(rt, false);
        var mrt = (RectTransform)m.transform;
        mrt.anchorMin = Vector2.zero;
        mrt.anchorMax = Vector2.one;
        mrt.offsetMin = mrt.offsetMax = Vector2.zero;
        m.GetComponent<Image>().color = new Color(0.04f, 0.05f, 0.08f, 0.68f);
        menu = m.AddComponent<CanvasGroup>();

        const float W = 640f;
        var c = UiKit.Group(mrt, "Content", new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(W, 520));
        UiKit.Label(c, "SETTINGS", 26, Vector2.zero, new Vector2(W, 36), TextAlignmentOptions.Center, 20f);
        UiKit.Pill(c, new Vector2(W * 0.5f - 18, -50), new Vector2(36, 2), UiKit.Faint, "Rule");

        Section(c, "GRAPHICS", -96);
        quality = MakeSegmented(c, new[] { "Auto", "Low", "Medium", "High", "Ultra" }, -120, W, i =>
        {
            if (i == 0) QualityPresets.ApplyAuto();
            else QualityPresets.Apply((QualityPresets.Preset)(i - 1), false);
        });
        autoNote = UiKit.Label(c, "", 12, new Vector2(0, -164), new Vector2(W, 18), TextAlignmentOptions.Center, 1f, UiKit.Faint);

        Section(c, "CAMERA", -214);
        camera = MakeSegmented(c, new[] { "Third person", "Broadcast" }, -238, W, i =>
            ArenaCamera.SetMode(i == 0 ? ArenaCamera.Mode.ThirdPerson : ArenaCamera.Mode.Broadcast));

        Section(c, "DISPLAY", -300);
        overlaySwitch = MakeSwitch(c, "AI overlay", new Vector2(W * 0.5f - 250, -326), () =>
        {
            var o = FindObjectOfType<BossDebugOverlay>();
            if (o != null) o.SetVisible(!o.Visible);
        });
        fpsSwitch = MakeSwitch(c, "FPS counter", new Vector2(W * 0.5f + 40, -326), () => showFps = !showFps);

        Section(c, "MODE", -390);
        mode = MakeSegmented(c, new[] { "Fight", "Watch" }, -414, W, i => GameFlow.SetMode(i == 0));

        UiKit.Label(c, "ESC  to close", 11, new Vector2(0, -486), new Vector2(W, 16), TextAlignmentOptions.Center, 6f, UiKit.Faint);

        SetOpen(System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-menuPreview") >= 0, true);
        QualityPresets.Changed += Sync;
        GameFlow.ModeChanged += Sync;
        TouchControls.MenuPressed += ToggleFromTouch;
        ArenaCamera.ModeChanged += Sync;
        Sync();
    }

    void OnDestroy()
    {
        QualityPresets.Changed -= Sync;
        GameFlow.ModeChanged -= Sync;
        TouchControls.MenuPressed -= ToggleFromTouch;
        ArenaCamera.ModeChanged -= Sync;
    }

    void Section(RectTransform parent, string title, float y) =>
        UiKit.Label(parent, title, 11, new Vector2(0, y), new Vector2(640, 16), TextAlignmentOptions.Center, 10f, UiKit.Faint);

    Segmented MakeSegmented(RectTransform parent, string[] items, float y, float width, System.Action<int> onPick)
    {
        var seg = new Segmented();
        const float gap = 34f;
        var widths = new float[items.Length];
        float total = 0f;
        for (int i = 0; i < items.Length; i++)
        {
            var probe = UiKit.Label(parent, items[i], 18, Vector2.zero, new Vector2(300, 26));
            widths[i] = probe.GetPreferredValues(items[i]).x;
            Destroy(probe.gameObject);
            total += widths[i] + (i > 0 ? gap : 0);
        }
        float x = (width - total) * 0.5f;
        for (int i = 0; i < items.Length; i++)
        {
            int idx = i;
            var l = UiKit.Label(parent, items[i], 18, new Vector2(x, y), new Vector2(widths[i], 26), TextAlignmentOptions.Center, 1f);
            l.raycastTarget = true;
            var b = l.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() => { onPick(idx); Sync(); });
            seg.labels.Add(l);
            x += widths[i] + gap;
        }
        seg.underline = UiKit.Pill(parent, new Vector2(0, y - 30), new Vector2(10, 2), UiKit.Text, "Underline").rectTransform;
        seg.x = -1f;
        return seg;
    }

    Switch MakeSwitch(RectTransform parent, string label, Vector2 pos, System.Action onToggle)
    {
        var s = new Switch();
        s.track = UiKit.Pill(parent, pos + new Vector2(0, -4), new Vector2(36, 18), UiKit.Track, "SwitchTrack");
        s.track.raycastTarget = true;
        s.knob = UiKit.Pill(parent, pos + new Vector2(2, -6), new Vector2(14, 14), UiKit.Text, "Knob").rectTransform;
        var l = UiKit.Label(parent, label, 16, pos + new Vector2(50, 0), new Vector2(180, 24), TextAlignmentOptions.Left, 1f);
        l.raycastTarget = true;
        foreach (var go in new[] { s.track.gameObject, l.gameObject })
        {
            var b = go.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() => { onToggle(); Sync(); });
        }
        return s;
    }

    void Sync()
    {
        if (quality == null) return;
        quality.selected = QualityPresets.IsAuto ? 0 : (int)QualityPresets.Current + 1;
        camera.selected = ArenaCamera.CurrentMode == ArenaCamera.Mode.ThirdPerson ? 0 : 1;
        mode.selected = GameFlow.HumanMode ? 0 : 1;
        var rec = QualityPresets.Detect(out string why);
        autoNote.text = $"Running {QualityPresets.Current}.   Auto suggests {rec} for {why}.";
        var o = FindObjectOfType<BossDebugOverlay>();
        overlaySwitch.on = o != null && o.Visible;
        fpsSwitch.on = showFps;
    }

    void ToggleFromTouch() { SetOpen(!open); hintT = 0f; }

    void SetOpen(bool v, bool instant = false)
    {
        open = v;
        menu.blocksRaycasts = v;
        menu.interactable = v;
        if (instant) menu.alpha = v ? 1f : 0f;
        if (v) Sync();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        // Ignore the Esc that just closed the start screen (same frame).
        if (Input.GetKeyDown(KeyCode.Escape) && !GameFlow.StartScreenOpen && Time.frameCount != GameFlow.ClosedFrame)
        { SetOpen(!open); hintT = 0f; }
        if (!GameFlow.StartScreenOpen) PlayerController.InputBlocked = open;

        menu.alpha = UiKit.Ease(menu.alpha, open ? 1f : 0f, 12f);
        menu.gameObject.SetActive(menu.alpha > 0.01f || open);
        menu.transform.localScale = Vector3.one * (1f + 0.02f * (1f - menu.alpha));

        // Hint: visible for a few seconds after start or after closing the menu.
        hintT += dt;
        hintGroup.alpha = UiKit.Ease(hintGroup.alpha, !open && hintT < 6f && !TouchControls.Active ? 1f : 0f, 3f);

        foreach (var seg in new[] { quality, camera, mode })
        {
            var l = (RectTransform)seg.labels[seg.selected].transform;
            float tx = l.anchoredPosition.x, tw = l.sizeDelta.x;
            seg.x = seg.x < 0 ? tx : UiKit.Ease(seg.x, tx, 14f);
            seg.w = seg.w <= 0 ? tw : UiKit.Ease(seg.w, tw, 14f);
            seg.underline.anchoredPosition = new Vector2(seg.x, seg.underline.anchoredPosition.y);
            seg.underline.sizeDelta = new Vector2(seg.w, 2f);
            for (int i = 0; i < seg.labels.Count; i++)
                seg.labels[i].color = Color.Lerp(seg.labels[i].color, i == seg.selected ? UiKit.Text : UiKit.Faint,
                                                 1f - Mathf.Exp(-12f * dt));
        }
        foreach (var s in new[] { overlaySwitch, fpsSwitch })
        {
            s.t = UiKit.Ease(s.t, s.on ? 1f : 0f, 14f);
            var p = s.knob.anchoredPosition;
            s.knob.anchoredPosition = new Vector2(s.track.rectTransform.anchoredPosition.x + 2 + 18 * s.t, p.y);
            s.track.color = Color.Lerp(UiKit.Track, new Color(0.52f, 0.84f, 1f, 0.9f), s.t);
        }

        fpsAccum += dt;
        fpsFrames++;
        if (fpsAccum >= 0.5f)
        {
            float fps = fpsFrames / fpsAccum;
            fpsAccum = 0f;
            fpsFrames = 0;
            fpsText.text = $"{fps:0} FPS   ·   {1000f / Mathf.Max(fps, 1f):0.0} MS   ·   {QualityPresets.Current.ToString().ToUpperInvariant()}";
        }
        fpsText.gameObject.SetActive(showFps);
    }
}
