using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small shared toolkit for the minimal HUD: one palette, soft-shadowed type, rounded hairline bars.
/// No dark panels: legibility comes from a soft text shadow and generous spacing.
/// </summary>
public static class UiKit
{
    // Palette
    public static readonly Color Text = new Color(0.97f, 0.97f, 0.98f, 1f);
    public static readonly Color Muted = new Color(0.97f, 0.97f, 0.98f, 0.62f);
    public static readonly Color Faint = new Color(0.97f, 0.97f, 0.98f, 0.38f);
    public static readonly Color Track = new Color(1f, 1f, 1f, 0.16f);

    // One colour per action, used everywhere the actions appear (rows, mix bars, legend).
    public static readonly Color[] Action =
    {
        new Color(0.62f, 0.65f, 0.72f),   // idle: cool grey
        new Color(0.98f, 0.97f, 0.95f),   // attack: warm white
        new Color(0.52f, 0.84f, 1.00f),   // dodge: sky
        new Color(0.80f, 0.62f, 1.00f),   // special: lilac
    };

    // Boss phase tints, softened
    public static readonly Color[] Phase =
    {
        new Color(0.96f, 0.96f, 0.97f),
        new Color(1.00f, 0.70f, 0.38f),
        new Color(1.00f, 0.42f, 0.38f),
    };

    static Sprite pill;
    static Material shadowMat;

    /// Fully rounded 9-sliced sprite, so bars of any length get smooth round caps.
    public static Sprite PillSprite
    {
        get
        {
            if (pill != null) return pill;
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float a = Mathf.Clamp01(r - d);        // 1px anti-aliased edge
                    px[y * n + x] = new Color(1, 1, 1, a);
                }
            tex.SetPixels32(px);
            tex.Apply();
            pill = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                                 SpriteMeshType.FullRect, new Vector4(15, 15, 15, 15));
            return pill;
        }
    }

    static Sprite card;

    /// Rounded rectangle (radius 14 px at reference resolution), 9-sliced so cards of any size keep their corners.
    public static Sprite CardSprite
    {
        get
        {
            if (card != null) return card;
            const int n = 64, r = 14;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, n - r), cy = Mathf.Clamp(y + 0.5f, r, n - r);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    px[y * n + x] = new Color(1, 1, 1, Mathf.Clamp01(r - d));
                }
            tex.SetPixels32(px);
            tex.Apply();
            card = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                                 SpriteMeshType.FullRect, new Vector4(r + 1, r + 1, r + 1, r + 1));
            return card;
        }
    }

    public static readonly Color CardFill = new Color(0.06f, 0.07f, 0.10f, 0.58f);
    public static readonly Color CardEdge = new Color(1f, 1f, 1f, 0.09f);

    /// A glassy card: translucent fill with a faint 1 px edge. Returns the content RectTransform
    /// (pivot top-left), so children can be positioned from the card's top-left like a Group.
    public static RectTransform Card(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color? fillColor = null)
    {
        var edge = new GameObject(name, typeof(RectTransform), typeof(Image));
        edge.transform.SetParent(parent, false);
        var ert = (RectTransform)edge.transform;
        ert.anchorMin = ert.anchorMax = ert.pivot = anchor;
        ert.anchoredPosition = pos;
        ert.sizeDelta = size;
        var ei = edge.GetComponent<Image>();
        ei.sprite = CardSprite; ei.type = Image.Type.Sliced; ei.color = CardEdge; ei.raycastTarget = false;

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(edge.transform, false);
        var frt = (RectTransform)fill.transform;
        frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
        frt.offsetMin = new Vector2(1, 1); frt.offsetMax = new Vector2(-1, -1);
        var fi = fill.GetComponent<Image>();
        fi.sprite = CardSprite; fi.type = Image.Type.Sliced; fi.color = fillColor ?? CardFill; fi.raycastTarget = false;

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(edge.transform, false);
        var crt = (RectTransform)content.transform;
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0, 1);
        crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = size;
        return crt;
    }

    /// Soft, low-contrast shadow under the text (TMP underlay), shared by all HUD text.
    public static Material ShadowMaterial
    {
        get
        {
            if (shadowMat != null) return shadowMat;
            var src = Resources.Load<Material>("Fonts & Materials/LiberationSans SDF - Drop Shadow");
            if (src == null) return null;
            shadowMat = new Material(src) { name = "HUD Soft Shadow" };
            shadowMat.SetFloat("_OutlineWidth", 0f);
            shadowMat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.42f));
            shadowMat.SetFloat("_UnderlayOffsetX", 0.2f);
            shadowMat.SetFloat("_UnderlayOffsetY", -0.35f);
            shadowMat.SetFloat("_UnderlaySoftness", 0.6f);
            shadowMat.SetFloat("_UnderlayDilate", 0.15f);
            return shadowMat;
        }
    }

    public static Canvas MakeCanvas(Transform parent, string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var c = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = order;
        var s = go.AddComponent<CanvasScaler>();
        s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        // Phones and tablets: a smaller reference makes every HUD element about 1.3x larger on screen.
        s.referenceResolution = Application.isMobilePlatform ? new Vector2(1500, 844) : new Vector2(1920, 1080);
        s.matchWidthOrHeight = 1f;
        return c;
    }

    public static RectTransform Group(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    /// Text positioned from the parent's top-left (pivot top-left), unless an anchor is given.
    public static TextMeshProUGUI Label(RectTransform parent, string s, float size, Vector2 pos, Vector2 box,
                                        TextAlignmentOptions align = TextAlignmentOptions.Left,
                                        float spacing = 0f, Color? color = null, Vector2? anchor = null)
    {
        var a = anchor ?? new Vector2(0, 1);
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = a;
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (ShadowMaterial != null) t.fontSharedMaterial = ShadowMaterial;
        t.text = s;
        t.fontSize = size;
        t.alignment = align;
        t.characterSpacing = spacing;
        t.fontStyle = FontStyles.Normal;
        t.color = color ?? Text;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        return t;
    }

    /// A rounded image (pill). Pivot top-left, positioned from the parent's top-left.
    public static Image Pill(RectTransform parent, Vector2 pos, Vector2 size, Color c, string name = "Pill")
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.sprite = PillSprite;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 32f / Mathf.Max(1f, size.y);   // keep caps fully round at any height
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    /// Smoothly animated bar: a track plus a fill whose width eases toward the target.
    public class Bar
    {
        public readonly RectTransform Fill;
        public readonly Image FillImage;
        readonly float width, height;
        float shown = -1f;

        public Bar(RectTransform parent, Vector2 pos, float width, float height, Color fill, Color? track = null)
        {
            this.width = width;
            this.height = height;
            Pill(parent, pos, new Vector2(width, height), track ?? Track, "Track");
            FillImage = Pill(parent, pos, new Vector2(width, height), fill, "Fill");
            Fill = FillImage.rectTransform;
        }

        public void Set(float frac, float speed = 10f)
        {
            frac = Mathf.Clamp01(frac);
            shown = shown < 0f ? frac : Mathf.Lerp(shown, frac, 1f - Mathf.Exp(-speed * Time.deltaTime));
            // Below the cap diameter a pill can't shrink further; fade it out instead.
            float w = shown * width;
            Fill.sizeDelta = new Vector2(Mathf.Max(w, height), height);
            var c = FillImage.color;
            c.a = Mathf.Clamp01(w / height) * BaseAlpha;
            FillImage.color = c;
        }

        public float BaseAlpha = 1f;
        public float Shown => Mathf.Max(0f, shown);
    }

    public static float Ease(float current, float target, float speed) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-speed * Time.deltaTime));
}
