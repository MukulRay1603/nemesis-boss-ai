using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// In-game settings panel (Esc): graphics preset (Auto / Low / Medium / High / Ultra),
/// camera mode, debug overlay and FPS counter. Also draws a small FPS readout and a key hint.
/// Built in code; not created in -nemesisEval or -nemesisRecord runs.
/// </summary>
public class SettingsMenu : MonoBehaviour
{
    static readonly Color PanelCol = new Color(0.05f, 0.06f, 0.09f, 0.92f);
    static readonly Color BtnCol = new Color(1f, 1f, 1f, 0.08f);
    static readonly Color BtnOn = new Color(0.35f, 0.8f, 1f, 0.85f);

    GameObject panel;
    TextMeshProUGUI fpsText, autoText;
    Image[] qualityBtns, cameraBtns;
    Image overlayBtn, fpsBtn;
    bool showFps = true;
    float fpsAccum; int fpsFrames; float fpsShown;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-nemesisEval") >= 0 || System.Array.IndexOf(args, "-nemesisRecord") >= 0) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        new GameObject("SettingsMenu").AddComponent<SettingsMenu>();
    }

    void Start()
    {
        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var root = new GameObject("SettingsCanvas");
        root.transform.SetParent(transform);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;
        root.AddComponent<GraphicRaycaster>();
        var rootRt = (RectTransform)root.transform;

        // Always-on corner readouts
        fpsText = Label(rootRt, "", 18, new Vector2(1f, 0f), new Vector2(-28, 70), new Vector2(300, 26), TextAlignmentOptions.Right);
        var hint = Label(rootRt, "Esc  settings     Tab  AI overlay     C  camera", 16,
                         new Vector2(1f, 0f), new Vector2(-28, 34), new Vector2(600, 26), TextAlignmentOptions.Right);
        hint.color = new Color(1, 1, 1, 0.55f);

        // Panel
        panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(root.transform, false);
        var prt = (RectTransform)panel.transform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(720, 520);
        panel.GetComponent<Image>().color = PanelCol;

        Label(prt, "SETTINGS", 34, new Vector2(0.5f, 1f), new Vector2(0, -28), new Vector2(660, 44), TextAlignmentOptions.Center);

        Label(prt, "Graphics preset", 20, new Vector2(0.5f, 1f), new Vector2(0, -96), new Vector2(660, 28), TextAlignmentOptions.Left)
            .color = new Color(0.75f, 0.78f, 0.85f);
        string[] q = { "AUTO", "LOW", "MEDIUM", "HIGH", "ULTRA" };
        qualityBtns = new Image[q.Length];
        for (int i = 0; i < q.Length; i++)
        {
            int idx = i;
            qualityBtns[i] = Button(prt, q[i], new Vector2(-264 + i * 132, -150), new Vector2(122, 48), () =>
            {
                if (idx == 0) QualityPresets.ApplyAuto();
                else QualityPresets.Apply((QualityPresets.Preset)(idx - 1), false);
                Refresh();
            });
        }
        autoText = Label(prt, "", 16, new Vector2(0.5f, 1f), new Vector2(0, -190), new Vector2(660, 50), TextAlignmentOptions.TopLeft);
        autoText.color = new Color(0.75f, 0.78f, 0.85f);
        autoText.enableWordWrapping = true;

        Label(prt, "Camera", 20, new Vector2(0.5f, 1f), new Vector2(0, -258), new Vector2(660, 28), TextAlignmentOptions.Left)
            .color = new Color(0.75f, 0.78f, 0.85f);
        cameraBtns = new[]
        {
            Button(prt, "THIRD PERSON", new Vector2(-170, -312), new Vector2(320, 48), () => { ArenaCamera.SetMode(ArenaCamera.Mode.ThirdPerson); Refresh(); }),
            Button(prt, "BROADCAST", new Vector2(170, -312), new Vector2(320, 48), () => { ArenaCamera.SetMode(ArenaCamera.Mode.Broadcast); Refresh(); }),
        };

        Label(prt, "Display", 20, new Vector2(0.5f, 1f), new Vector2(0, -366), new Vector2(660, 28), TextAlignmentOptions.Left)
            .color = new Color(0.75f, 0.78f, 0.85f);
        overlayBtn = Button(prt, "AI OVERLAY", new Vector2(-170, -420), new Vector2(320, 48), () =>
        {
            var o = FindObjectOfType<BossDebugOverlay>();
            if (o != null) o.SetVisible(!o.Visible);
            Refresh();
        });
        fpsBtn = Button(prt, "FPS COUNTER", new Vector2(170, -420), new Vector2(320, 48), () => { showFps = !showFps; Refresh(); });

        Label(prt, "Esc to close", 15, new Vector2(0.5f, 0f), new Vector2(0, 30), new Vector2(660, 24), TextAlignmentOptions.Center)
            .color = new Color(1, 1, 1, 0.5f);

        panel.SetActive(false);
        QualityPresets.Changed += Refresh;
        ArenaCamera.ModeChanged += Refresh;
        Refresh();
    }

    void OnDestroy()
    {
        QualityPresets.Changed -= Refresh;
        ArenaCamera.ModeChanged -= Refresh;
    }

    void Refresh()
    {
        if (qualityBtns == null) return;
        for (int i = 0; i < qualityBtns.Length; i++)
        {
            bool on = i == 0 ? QualityPresets.IsAuto : !QualityPresets.IsAuto && (int)QualityPresets.Current == i - 1;
            qualityBtns[i].color = on ? BtnOn : BtnCol;
        }
        var rec = QualityPresets.Detect(out string why);
        autoText.text = $"Active: {QualityPresets.Current}{(QualityPresets.IsAuto ? " (auto)" : "")}.   " +
                        $"Auto recommends {rec} for this machine ({why}).";
        cameraBtns[0].color = ArenaCamera.CurrentMode == ArenaCamera.Mode.ThirdPerson ? BtnOn : BtnCol;
        cameraBtns[1].color = ArenaCamera.CurrentMode == ArenaCamera.Mode.Broadcast ? BtnOn : BtnCol;
        var o = FindObjectOfType<BossDebugOverlay>();
        overlayBtn.color = o != null && o.Visible ? BtnOn : BtnCol;
        fpsBtn.color = showFps ? BtnOn : BtnCol;
        fpsText.gameObject.SetActive(showFps);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            panel.SetActive(!panel.activeSelf);
            Refresh();
        }

        fpsAccum += Time.unscaledDeltaTime;
        fpsFrames++;
        if (fpsAccum >= 0.5f)
        {
            fpsShown = fpsFrames / fpsAccum;
            fpsAccum = 0f;
            fpsFrames = 0;
            if (showFps)
                fpsText.text = $"{fpsShown:0} FPS   {1000f / Mathf.Max(fpsShown, 1f):0.0} ms   {QualityPresets.Current}";
        }
    }

    // ── UI helpers ───────────────────────────────────────────────────────
    static TextMeshProUGUI Label(RectTransform parent, string s, float size, Vector2 anchor, Vector2 pos, Vector2 box,
                                 TextAlignmentOptions align)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = s;
        t.fontSize = size;
        t.alignment = align;
        t.fontStyle = FontStyles.Bold;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        return t;
    }

    static Image Button(RectTransform parent, string text, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.color = BtnCol;
        var b = go.GetComponent<Button>();
        var cb = b.colors;
        cb.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
        b.colors = cb;
        b.onClick.AddListener(onClick);
        var l = Label(rt, text, 18, new Vector2(0.5f, 0.5f), Vector2.zero, size, TextAlignmentOptions.Center);
        l.raycastTarget = false;
        return img;
    }
}
