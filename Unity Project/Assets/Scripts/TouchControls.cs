using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// On-screen controls for phones and tablets (also with -touch on desktop, for testing):
///   left   a floating joystick (touch anywhere in the left third, drag to move)
///   right  ATTACK and DASH buttons
///   top    a MENU button, since there is no Esc key
/// Feeds PlayerController's virtual inputs, so touch and keyboard go through the same code.
/// Visible only in Fight mode while no menu is open.
/// </summary>
public class TouchControls : MonoBehaviour
{
    public static bool Active { get; private set; }
    public static event System.Action MenuPressed;

    CanvasGroup group, menuGroup;
    Stick stickRef;
    bool placed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        foreach (var f in new[] { "-nemesisEval", "-nemesisTrain", "-nemesisRecord" })
            if (System.Array.IndexOf(args, f) >= 0) return;
        bool forced = System.Array.IndexOf(args, "-touch") >= 0;
        if (!forced && !(Application.isMobilePlatform && Input.touchSupported)) return;
        Active = true;
        new GameObject("TouchControls").AddComponent<TouchControls>();
    }

    void Start()
    {
        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        var canvas = UiKit.MakeCanvas(transform, "TouchCanvas", 80);
        canvas.gameObject.AddComponent<GraphicRaycaster>();
        var rt = (RectTransform)canvas.transform;

        // Controls group (fight mode only)
        var g = new GameObject("Controls", typeof(RectTransform));
        g.transform.SetParent(rt, false);
        var grt = (RectTransform)g.transform;
        grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one; grt.offsetMin = grt.offsetMax = Vector2.zero;
        group = g.AddComponent<CanvasGroup>();

        // Joystick: invisible hit area over the left third, visible base + knob that follow the thumb
        var area = new GameObject("StickArea", typeof(RectTransform), typeof(Image));
        area.transform.SetParent(grt, false);
        var art = (RectTransform)area.transform;
        art.anchorMin = new Vector2(0, 0); art.anchorMax = new Vector2(0.4f, 0.7f);
        art.offsetMin = art.offsetMax = Vector2.zero;
        area.GetComponent<Image>().color = new Color(0, 0, 0, 0);
        var stick = area.AddComponent<Stick>();
        stick.baseImg = Circle(art, new Vector2(240, 240), new Color(1, 1, 1, 0.08f), "Base");
        stick.knob = Circle(art, new Vector2(104, 104), new Color(1, 1, 1, 0.55f), "Knob");
        stick.home = new Vector2(220, 230);
        stickRef = stick;

        Button(grt, "ATTACK", new Vector2(-210, 220), 190, new Color(0.98f, 0.97f, 0.95f, 0.22f),
               () => PlayerController.VirtualAttack = true);
        Button(grt, "DASH", new Vector2(-420, 150), 130, new Color(0.52f, 0.84f, 1f, 0.22f),
               () => PlayerController.VirtualDash = true);

        // Menu button, always available (top right)
        var m = Button(rt, "MENU", new Vector2(-70, -70), 84, new Color(1, 1, 1, 0.12f), () => MenuPressed?.Invoke(),
                       new Vector2(1, 1));
        menuGroup = m.gameObject.AddComponent<CanvasGroup>();
    }

    static Image Circle(RectTransform parent, Vector2 size, Color c, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.sprite = UiKit.PillSprite;
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    static RectTransform Button(RectTransform parent, string label, Vector2 pos, float d, Color c,
                                System.Action onDown, Vector2? anchor = null)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor ?? new Vector2(1, 0);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(d, d);
        var img = go.GetComponent<Image>();
        img.sprite = UiKit.PillSprite;
        img.color = c;
        var press = go.AddComponent<PressButton>();
        press.onDown = onDown;
        press.img = img;
        var t = UiKit.Label(rt, label, d > 120 ? 20 : 15, Vector2.zero, new Vector2(d, d), TextAlignmentOptions.Center,
                            d > 120 ? 8f : 5f, UiKit.Text, new Vector2(0, 1));
        t.rectTransform.anchoredPosition = Vector2.zero;
        return rt;
    }

    void Update()
    {
        if (!placed) { stickRef.Reset(); placed = true; }   // after the canvas has its final size
        bool show = GameFlow.HumanMode && !GameFlow.StartScreenOpen && !PlayerController.InputBlocked;
        group.alpha = UiKit.Ease(group.alpha, show ? 1f : 0f, 10f);
        group.blocksRaycasts = show;
        menuGroup.alpha = UiKit.Ease(menuGroup.alpha, GameFlow.StartScreenOpen ? 0f : 1f, 10f);
        menuGroup.blocksRaycasts = !GameFlow.StartScreenOpen;
        if (!show) PlayerController.VirtualMove = Vector2.zero;
    }

    /// Fires on touch-down (not on release), so attacks feel immediate.
    class PressButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public System.Action onDown;
        public Image img;
        Color baseCol;
        void Start() => baseCol = img.color;
        public void OnPointerDown(PointerEventData e) { onDown?.Invoke(); img.color = baseCol * new Color(1, 1, 1, 2.2f); }
        public void OnPointerUp(PointerEventData e) => img.color = baseCol;
    }

    /// Floating joystick: appears where the thumb lands, returns a vector in [-1, 1].
    class Stick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public Image baseImg, knob;
        public Vector2 home;
        const float Radius = 100f;
        Vector2 origin;

        public void Reset()
        {
            var area = (RectTransform)transform;
            Vector2 local = home - area.rect.size * 0.5f;   // area pivot is centre
            baseImg.rectTransform.anchoredPosition = local;
            knob.rectTransform.anchoredPosition = local;
            PlayerController.VirtualMove = Vector2.zero;
        }

        Vector2 ToLocal(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, e.position, e.pressEventCamera, out var p);
            return p;
        }

        public void OnPointerDown(PointerEventData e)
        {
            origin = ToLocal(e);
            baseImg.rectTransform.anchoredPosition = origin;
            knob.rectTransform.anchoredPosition = origin;
        }

        public void OnDrag(PointerEventData e)
        {
            Vector2 d = Vector2.ClampMagnitude(ToLocal(e) - origin, Radius);
            knob.rectTransform.anchoredPosition = origin + d;
            PlayerController.VirtualMove = d / Radius;
        }

        public void OnPointerUp(PointerEventData e) => Reset();
    }
}
