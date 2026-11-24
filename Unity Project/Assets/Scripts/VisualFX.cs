using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Presentation layer, built entirely at runtime. Visual only: no colliders, no gameplay state,
/// and it never calls UnityEngine.Random (the scripted player and FSM boss use it, so touching it
/// would change fight outcomes). Skipped in -nemesisEval runs.
///
///   - post-processing (ACES tonemapping, bloom, vignette, chromatic aberration on hits)
///   - moody lighting, procedural stone floor, arena trim that glows in the boss's phase colour
///   - boss/player emissive bodies and visors so facing is readable, boss point light
///   - telegraph rings showing the boss's attack range and wind-up progress
///   - motion trails on lunges/dodges, hit sparks, damage numbers, phase-change shockwaves
/// Scales with QualityPresets.FxTier.
/// </summary>
public class VisualFX : MonoBehaviour
{
    static readonly Color[] PhaseCol =
        { new Color(0.25f, 0.75f, 1f), new Color(1f, 0.5f, 0.1f), new Color(1f, 0.12f, 0.1f) };
    static readonly Color[] BossBody =
        { new Color(0.85f, 0.86f, 0.9f), new Color(1f, 0.45f, 0f), new Color(0.6f, 0f, 0f) };

    BossAgent boss;
    PlayerController player;
    Transform bossT, playerT;
    Material addTemplate, litTemplate;
    Texture2D dot;

    Volume volume;
    Bloom bloom;
    Vignette vignette;
    ChromaticAberration chroma;

    Renderer bossR, playerR;
    Material bossOrig, bossMat, playerOrig, playerMat, bossVisorMat, playerVisorMat;
    Light bossLight;
    LineRenderer ringOuter, ringInner;
    TrailRenderer bossTrail, playerTrail;
    readonly List<Material> trimMats = new List<Material>();
    readonly List<LineRenderer> floorRings = new List<LineRenderer>();
    readonly List<GameObject> pillars = new List<GameObject>();
    readonly List<Light> pillarLights = new List<Light>();
    ParticleSystem sparks;

    float bossFlash, playerFlash, hurtPulse, phasePulse, chromaPulse;
    int lastPhase;
    Vector3 lastPlayerPos;
    bool ready;
    readonly System.Random rng = new System.Random(12345);   // visual jitter only

    class Ring { public LineRenderer lr; public Vector3 c; public float t, dur, r0, r1, w; public Color col; }
    readonly List<Ring> rings = new List<Ring>();

    class PopupItem { public TextMeshPro tmp; public Vector3 p; public float t; public Color col; }
    readonly List<PopupItem> popups = new List<PopupItem>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-nemesisEval") >= 0) return;
        if (System.Array.IndexOf(args, "-noFx") >= 0) return;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        var b = FindObjectOfType<BossAgent>();
        if (b == null || b.player == null) return;
        var fx = new GameObject("VisualFX").AddComponent<VisualFX>();
        fx.boss = b;
    }

    System.Collections.IEnumerator Start()
    {
        bossT = boss.transform;
        playerT = boss.player;
        player = playerT.GetComponent<PlayerController>();
        addTemplate = Resources.Load<Material>("NemesisFX/FX_Additive");
        litTemplate = Resources.Load<Material>("NemesisFX/FX_EmissiveLit");
        if (addTemplate == null || litTemplate == null)
        {
            Debug.LogWarning("[VisualFX] FX materials missing (run Nemesis > Create FX Materials). Effects disabled.");
            yield break;
        }
        dot = MakeDot(64);

        SetupPost();
        SetupLighting();
        SetupArena();

        yield return null;   // let BossAgent / PlayerController create their material instances first
        SetupFighters();
        SetupRingsAndTrails();
        SetupSparks();

        CombatEvents.Hit += OnHit;
        EpisodeStats.Ended += OnEnded;
        QualityPresets.Changed += ApplyQuality;
        ApplyQuality();
        lastPlayerPos = playerT.position;
        ready = true;
    }

    void OnDestroy()
    {
        CombatEvents.Hit -= OnHit;
        EpisodeStats.Ended -= OnEnded;
        QualityPresets.Changed -= ApplyQuality;
    }

    // ── Setup ────────────────────────────────────────────────────────────
    void SetupPost()
    {
        // The scene's own volume points at a profile that is gitignored, so replace it with a known one.
        foreach (var v in FindObjectsOfType<Volume>()) v.enabled = false;
        var go = new GameObject("NemesisPost");
        go.transform.SetParent(transform);
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10;
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        volume.sharedProfile = profile;

        var tone = profile.Add<Tonemapping>(true);
        tone.mode.value = TonemappingMode.ACES;
        bloom = profile.Add<Bloom>(true);
        bloom.threshold.value = 0.95f;
        bloom.intensity.value = 0.9f;
        bloom.scatter.value = 0.7f;
        bloom.tint.value = Color.white;
        bloom.clamp.value = 65472f;
        bloom.highQualityFiltering.value = false;
        bloom.dirtIntensity.value = 0f;
        bloom.dirtTexture.value = null;
        vignette = profile.Add<Vignette>(true);
        vignette.color.value = Color.black;
        vignette.center.value = new Vector2(0.5f, 0.5f);
        vignette.intensity.value = 0.32f;
        vignette.smoothness.value = 0.45f;
        vignette.rounded.value = false;
        chroma = profile.Add<ChromaticAberration>(true);
        chroma.intensity.value = 0f;
        var col = profile.Add<ColorAdjustments>(true);
        col.postExposure.value = 0.25f;
        col.contrast.value = 14f;
        col.saturation.value = 6f;
        col.colorFilter.value = Color.white;
        col.hueShift.value = 0f;
    }

    void SetupLighting()
    {
        foreach (var l in FindObjectsOfType<Light>())
            if (l.type == LightType.Directional)
            {
                l.color = new Color(1f, 0.93f, 0.85f);
                l.intensity = 1.7f;
                l.shadows = LightShadows.Soft;
                l.shadowStrength = 0.8f;
            }
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.30f, 0.34f, 0.46f);
        RenderSettings.ambientEquatorColor = new Color(0.19f, 0.18f, 0.21f);
        RenderSettings.ambientGroundColor = new Color(0.07f, 0.06f, 0.06f);
        RenderSettings.ambientIntensity = 1f;
        DynamicGI.UpdateEnvironment();
        var cam = Camera.main;
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.085f, 0.12f);
        }
    }

    Material Lit(Color baseCol, float smooth, Color emission)
    {
        var m = new Material(litTemplate);
        m.SetColor("_BaseColor", baseCol);
        m.SetFloat("_Smoothness", smooth);
        m.SetColor("_EmissionColor", emission);
        return m;
    }

    GameObject Block(string name, Vector3 pos, Vector3 scale, Material m, Transform parent = null)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        DestroyImmediate(go.GetComponent<Collider>());   // visual only, never touches physics
        go.transform.SetParent(parent != null ? parent : transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    void SetupArena()
    {
        // Floor: procedural dark stone tiles.
        var arena = GameObject.Find("Arena");
        if (arena != null)
        {
            var m = Lit(Color.white, 0.32f, Color.black);
            m.SetTexture("_BaseMap", MakeFloorTexture(512));
            m.SetTextureScale("_BaseMap", new Vector2(3f, 3f));
            arena.GetComponent<Renderer>().material = m;
        }

        // Dark ground outside the walls so the raised camera never sees empty space.
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Backdrop";
        DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.SetParent(transform);
        ground.transform.position = new Vector3(0f, -0.03f, 0f);
        ground.transform.localScale = new Vector3(30f, 1f, 30f);
        var gr = ground.GetComponent<Renderer>();
        gr.sharedMaterial = Lit(new Color(0.10f, 0.10f, 0.11f), 0.1f, Color.black);
        gr.shadowCastingMode = ShadowCastingMode.Off;

        // Walls: dark slate with glowing trim along the top and the floor edge.
        var wallMat = Lit(new Color(0.30f, 0.31f, 0.35f), 0.45f, Color.black);
        foreach (var name in new[] { "Wall_North", "Wall_South", "Wall_East", "Wall_West" })
        {
            var w = GameObject.Find(name);
            if (w == null) continue;
            w.GetComponent<Renderer>().sharedMaterial = wallMat;
            Vector3 p = w.transform.position, s = w.transform.localScale;
            bool alongX = s.x > s.z;
            Vector3 inward = -new Vector3(Mathf.Sign(p.x) * (alongX ? 0 : 1), 0, Mathf.Sign(p.z) * (alongX ? 1 : 0));
            Vector3 len = alongX ? new Vector3(s.x, 0, 0) : new Vector3(0, 0, s.z);
            Vector3 thin = alongX ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);

            var top = Lit(Color.black, 0.5f, PhaseCol[0] * 2.5f);
            trimMats.Add(top);
            Block(name + "_TrimTop", new Vector3(p.x, p.y + s.y * 0.5f + 0.03f, p.z),
                  len + thin * 0.12f + Vector3.up * 0.04f, top);
            Block(name + "_TrimFloor", new Vector3(p.x, 0.06f, p.z) + inward * ((alongX ? s.z : s.x) * 0.5f + 0.02f),
                  len + thin * 0.06f + Vector3.up * 0.06f, top);
        }

        // Faint arena markings on the floor.
        foreach (float r in new[] { 7.6f })
        {
            var lr = NewLine("FloorRing", 0.05f);
            SetCircle(lr, new Vector3(0, 0.02f, 0), r, 96);
            floorRings.Add(lr);
        }

        // Corner pillars with braziers (High and up).
        var pillarMat = Lit(new Color(0.22f, 0.22f, 0.25f), 0.35f, Color.black);
        foreach (var c in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
        {
            var root = new GameObject("Pillar");
            root.transform.SetParent(transform);
            root.transform.position = new Vector3(c.x * 10.2f, 0f, c.y * 10.2f);
            Block("Shaft", new Vector3(0, 2.4f, 0), new Vector3(1.3f, 4.8f, 1.3f), pillarMat, root.transform);
            var cap = Lit(Color.black, 0.5f, PhaseCol[0] * 3f);
            trimMats.Add(cap);
            Block("Cap", new Vector3(0, 4.9f, 0), new Vector3(1.0f, 0.25f, 1.0f), cap, root.transform);
            var lgo = new GameObject("Light");
            lgo.transform.SetParent(root.transform, false);
            lgo.transform.localPosition = new Vector3(0, 5.4f, 0);
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 11f;
            l.intensity = 1.6f;
            l.shadows = LightShadows.None;
            pillarLights.Add(l);
            pillars.Add(root);
        }
    }

    FighterAnimator bossAnim, playerAnim;

    void SetupFighters()
    {
        bossR = bossT.GetComponent<Renderer>();
        bossOrig = bossR.material;                 // the instance BossAgent tints per phase
        bossMat = Lit(Color.white, 0.6f, Color.black);

        playerR = playerT.GetComponent<Renderer>();
        playerOrig = playerR.material;             // the instance PlayerController tints per state
        playerMat = Lit(Color.white, 0.55f, Color.black);

        // Visual bodies with energy blades; the physics capsules stay untouched (just hidden).
        bossAnim = FighterAnimator.Attach(bossT.gameObject, bossMat, litTemplate, addTemplate, PhaseCol[0]);
        bossAnim.boss = boss;
        playerAnim = FighterAnimator.Attach(playerT.gameObject, playerMat, litTemplate, addTemplate,
                                            new Color(0.3f, 0.85f, 1f));
        playerAnim.player = player;

        // Visors show which way each fighter faces.
        var bb = bossAnim.body;
        bossVisorMat = Lit(Color.black, 0.8f, PhaseCol[0] * 4f);
        Block("Visor", new Vector3(0, 0.5f, 0.45f), new Vector3(0.75f, 0.13f, 0.18f), bossVisorMat, bb);
        Block("Core", new Vector3(0, 0.05f, 0.47f), new Vector3(0.22f, 0.22f, 0.1f), bossVisorMat, bb);
        playerVisorMat = Lit(Color.black, 0.8f, new Color(0.3f, 0.9f, 1f) * 3f);
        Block("Visor", new Vector3(0, 0.5f, 0.45f), new Vector3(0.6f, 0.1f, 0.15f), playerVisorMat, playerAnim.body);

        var lgo = new GameObject("BossLight");
        lgo.transform.SetParent(bossT, false);
        lgo.transform.localPosition = new Vector3(0, 0.6f, 0.9f);
        bossLight = lgo.AddComponent<Light>();
        bossLight.type = LightType.Point;
        bossLight.range = 6f;
        bossLight.shadows = LightShadows.None;
    }

    void SetupRingsAndTrails()
    {
        ringOuter = NewLine("TelegraphOuter", 0.09f);
        ringInner = NewLine("TelegraphInner", 0.05f);

        bossTrail = AddTrail(bossT.gameObject, 0.22f, 0.9f);
        playerTrail = AddTrail(playerT.gameObject, 0.18f, 0.6f);
    }

    TrailRenderer AddTrail(GameObject go, float time, float width)
    {
        var t = go.AddComponent<TrailRenderer>();
        t.time = time;
        t.widthCurve = new AnimationCurve(new Keyframe(0, width), new Keyframe(1, 0));
        t.sharedMaterial = NewAdditive();
        t.minVertexDistance = 0.05f;
        t.shadowCastingMode = ShadowCastingMode.Off;
        t.emitting = false;
        return t;
    }

    void SetupSparks()
    {
        var go = new GameObject("Sparks");
        go.transform.SetParent(transform);
        sparks = go.AddComponent<ParticleSystem>();
        sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = sparks.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.45f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.13f);
        main.gravityModifier = 0.8f;
        main.maxParticles = 600;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        var em = sparks.emission;
        em.rateOverTime = 0f;
        var shape = sparks.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.25f;
        var col = sparks.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                  new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
        col.color = g;
        var r = sparks.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = NewAdditive();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.velocityScale = 0.04f;
        r.lengthScale = 1.2f;
        r.shadowCastingMode = ShadowCastingMode.Off;
        sparks.Play();
    }

    Material NewAdditive()
    {
        var m = new Material(addTemplate);
        m.SetTexture("_BaseMap", dot);
        return m;
    }

    LineRenderer NewLine(string name, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.widthMultiplier = width;
        lr.sharedMaterial = new Material(addTemplate);
        lr.sharedMaterial.SetColor("_BaseColor", new Color(2.5f, 2.5f, 2.5f, 1f));   // HDR so bloom picks it up
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.alignment = LineAlignment.TransformZ;
        go.transform.rotation = Quaternion.Euler(90, 0, 0);   // lie flat on the floor
        lr.enabled = false;
        return lr;
    }

    static void SetCircle(LineRenderer lr, Vector3 c, float r, int n = 64)
    {
        lr.positionCount = n;
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            lr.SetPosition(i, c + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
        }
        lr.enabled = true;
    }

    static void SetLineColor(LineRenderer lr, Color c)
    {
        lr.startColor = c;
        lr.endColor = c;
    }

    void ApplyQuality()
    {
        int tier = QualityPresets.FxTier;
        if (volume != null) volume.enabled = QualityPresets.PostProcessing;
        if (bloom != null) bloom.active = QualityPresets.Bloom;
        if (chroma != null) chroma.active = tier >= 2;
        foreach (var p in pillars) p.SetActive(tier >= 2);
        foreach (var l in pillarLights) l.enabled = tier >= 3;
        if (bossLight != null) bossLight.enabled = tier >= 1;
        foreach (var lr in floorRings) lr.enabled = tier >= 1;
    }

    // ── Events ───────────────────────────────────────────────────────────
    void OnHit(Vector3 pos, float dmg, bool byBoss, bool special)
    {
        if (!ready) return;
        int phase = Mathf.Clamp(boss.currentPhase, 0, 2);
        Color c = byBoss ? (special ? new Color(1f, 0.2f, 0.9f) : PhaseCol[phase]) : new Color(0.4f, 0.95f, 1f);
        int count = new[] { 8, 16, 30, 40 }[QualityPresets.FxTier];
        Burst(pos + Vector3.up * 0.4f, c, special ? count * 2 : count);
        Popup(pos, dmg, byBoss ? new Color(1f, 0.6f, 0.55f) : new Color(1f, 1f, 1f), special);
        AddRing(new Vector3(pos.x, 0.05f, pos.z), 0.3f, special ? 3.2f : 2.0f, 0.35f, c, 0.12f);
        if (byBoss) { playerFlash = 1f; hurtPulse = special ? 1f : 0.7f; chromaPulse = special ? 1f : 0.6f; }
        else bossFlash = 1f;
    }

    void OnEnded(EpisodeResult r)
    {
        if (!ready) return;
        Vector3 at = r.outcome == "BOSS_WIN" ? playerT.position : bossT.position;
        Burst(at + Vector3.up * 0.5f, Color.white, 60);
        AddRing(new Vector3(at.x, 0.05f, at.z), 0.5f, 8f, 0.9f, Color.white, 0.25f);
    }

    void Burst(Vector3 pos, Color c, int n)
    {
        var ep = new ParticleSystem.EmitParams { position = pos, startColor = c, applyShapeToPosition = true };
        sparks.Emit(ep, n);
    }

    void AddRing(Vector3 c, float r0, float r1, float dur, Color col, float w)
    {
        if (QualityPresets.FxTier < 1 && rings.Count > 2) return;
        var lr = NewLine("Shockwave", w);
        rings.Add(new Ring { lr = lr, c = c, r0 = r0, r1 = r1, dur = dur, col = col, w = w });
    }

    void Popup(Vector3 pos, float dmg, Color col, bool big)
    {
        var go = new GameObject("DamageNumber");
        go.transform.SetParent(transform);
        var t = go.AddComponent<TextMeshPro>();
        t.text = big ? $"-{dmg:0}!" : $"-{dmg:0}";
        t.fontSize = big ? 6f : 4.5f;
        t.alignment = TextAlignmentOptions.Center;
        t.fontStyle = FontStyles.Normal;
        if (UiKit.ShadowMaterial != null) t.fontSharedMaterial = UiKit.ShadowMaterial;
        t.characterSpacing = 4f;
        float jx = (float)(rng.NextDouble() - 0.5) * 0.8f;
        popups.Add(new PopupItem { tmp = t, p = pos + new Vector3(jx, 1.5f, 0), col = col });
    }

    // ── Per frame ────────────────────────────────────────────────────────
    void LateUpdate()
    {
        if (!ready) return;
        float dt = Time.deltaTime;
        int tier = QualityPresets.FxTier;
        int phase = Mathf.Clamp(boss.currentPhase, 0, 2);
        if (phase != lastPhase)
        {
            if (phase > lastPhase)
            {
                phasePulse = 1f;
                AddRing(new Vector3(bossT.position.x, 0.05f, bossT.position.z), 0.5f, 14f, 1.1f, PhaseCol[phase], 0.3f);
                Burst(bossT.position + Vector3.up * 0.5f, PhaseCol[phase], 50);
            }
            lastPhase = phase;
        }
        Color pc = PhaseCol[phase];
        float t = Time.time;
        float enrage = phase == 2 ? 0.5f + 0.5f * Mathf.Sin(t * 7f) : 0f;

        // Arena trim follows the boss's phase.
        foreach (var m in trimMats)
            m.SetColor("_EmissionColor", pc * (0.9f + 0.8f * enrage + 3f * phasePulse));
        foreach (var l in pillarLights)
        {
            l.color = pc;
            l.intensity = 1.4f + 0.4f * Mathf.PerlinNoise(t * 3f, l.GetInstanceID() * 0.01f) + 2f * phasePulse;
        }
        foreach (var lr in floorRings) SetLineColor(lr, pc * new Color(1, 1, 1, 0.12f + 0.2f * phasePulse));

        // Boss body: mirror the colour BossAgent sets, add emission for wind-up and hits.
        var state = boss.currentBossState;
        float telegraph = state == BossAgent.BossState.Telegraph
            ? 1f - Mathf.Clamp01(boss.StateTimeLeft / Mathf.Max(0.01f, boss.telegraphDuration)) : 0f;
        bool attacking = state == BossAgent.BossState.Attack;
        bool special = boss.LastAction == 3;
        Color body = bossOrig.HasProperty("_BaseColor") ? bossOrig.GetColor("_BaseColor") : BossBody[phase];
        bossMat.SetColor("_BaseColor", body);
        float glow = 0.15f + 2.2f * telegraph + (attacking ? 2.5f : 0f) + 3.5f * bossFlash + 0.8f * enrage;
        bossMat.SetColor("_EmissionColor", Color.Lerp(pc, Color.white, bossFlash) * glow * 0.6f);
        bossVisorMat.SetColor("_EmissionColor", (special && (telegraph > 0 || attacking) ? new Color(1f, 0.2f, 0.9f) : pc) *
                                                 (3f + 4f * telegraph + 3f * enrage));
        bossAnim.bladeColor = special && (telegraph > 0 || attacking) ? new Color(1f, 0.2f, 0.9f) : pc;
        if (bossLight != null)
        {
            bossLight.color = pc;
            bossLight.intensity = 1.2f + 3f * telegraph + (attacking ? 4f : 0f) + 2f * enrage;
        }

        // Telegraph rings: outer = attack range, inner fills in as the wind-up completes.
        Vector3 floor = new Vector3(bossT.position.x, 0.04f, bossT.position.z);
        if (telegraph > 0f || attacking)
        {
            Color rc = special ? new Color(1f, 0.15f, 0.85f) : new Color(1f, 0.45f, 0.1f);
            SetCircle(ringOuter, floor, boss.attackRange);
            SetCircle(ringInner, floor, boss.attackRange * (attacking ? 1f : telegraph));
            SetLineColor(ringOuter, rc * (attacking ? 2.2f : 1f));
            SetLineColor(ringInner, rc * new Color(1, 1, 1, 0.8f));
            ringOuter.widthMultiplier = attacking ? 0.16f : 0.09f;
        }
        else
        {
            ringOuter.enabled = false;
            ringInner.enabled = false;
        }

        // Trails on lunges and dodges.
        bossTrail.emitting = tier >= 1 && (attacking || state == BossAgent.BossState.Dodge);
        bossTrail.startColor = pc * new Color(1, 1, 1, 0.55f);
        bossTrail.endColor = new Color(pc.r, pc.g, pc.b, 0f);
        float pSpeed = (playerT.position - lastPlayerPos).magnitude / Mathf.Max(dt, 1e-4f);
        lastPlayerPos = playerT.position;
        playerTrail.emitting = tier >= 1 && pSpeed > 4.2f;
        playerTrail.startColor = new Color(0.4f, 0.9f, 1f, 0.45f);
        playerTrail.endColor = new Color(0.4f, 0.9f, 1f, 0f);

        // Player body mirrors PlayerController's state colour, flashes when hit.
        Color pBody = playerOrig.HasProperty("_BaseColor") ? playerOrig.GetColor("_BaseColor") : Color.white;
        playerMat.SetColor("_BaseColor", pBody);
        playerMat.SetColor("_EmissionColor", Color.Lerp(Color.black, new Color(1f, 0.3f, 0.25f), playerFlash) * 2.5f);

        // Screen effects.
        if (vignette != null)
        {
            vignette.color.value = Color.Lerp(Color.black, new Color(0.55f, 0f, 0f), hurtPulse);
            vignette.intensity.value = 0.32f + 0.18f * hurtPulse + 0.06f * enrage;
        }
        if (chroma != null) chroma.intensity.value = 0.8f * chromaPulse + 0.4f * phasePulse;
        if (bloom != null) bloom.intensity.value = 0.9f + 1.6f * phasePulse;

        UpdateRings(dt);
        UpdatePopups(dt);

        bossFlash = Mathf.Max(0, bossFlash - dt * 6f);
        playerFlash = Mathf.Max(0, playerFlash - dt * 5f);
        hurtPulse = Mathf.Max(0, hurtPulse - dt * 2.2f);
        chromaPulse = Mathf.Max(0, chromaPulse - dt * 4f);
        phasePulse = Mathf.Max(0, phasePulse - dt * 1.1f);
    }

    void UpdateRings(float dt)
    {
        for (int i = rings.Count - 1; i >= 0; i--)
        {
            var r = rings[i];
            r.t += dt;
            float k = Mathf.Clamp01(r.t / r.dur);
            float e = 1f - (1f - k) * (1f - k);
            SetCircle(r.lr, r.c, Mathf.Lerp(r.r0, r.r1, e), 72);
            SetLineColor(r.lr, r.col * new Color(1.5f, 1.5f, 1.5f, 1f - k));
            r.lr.widthMultiplier = r.w * (1f - 0.6f * k);
            if (k >= 1f) { Destroy(r.lr.gameObject); rings.RemoveAt(i); }
        }
    }

    void UpdatePopups(float dt)
    {
        var cam = Camera.main;
        for (int i = popups.Count - 1; i >= 0; i--)
        {
            var p = popups[i];
            p.t += dt;
            float k = p.t / 0.9f;
            p.tmp.transform.position = p.p + Vector3.up * (1.1f * (1f - (1f - k) * (1f - k)));
            float pop = k < 0.12f ? Mathf.Lerp(0.6f, 1.25f, k / 0.12f) : Mathf.Lerp(1.25f, 1f, (k - 0.12f) * 3f);
            p.tmp.transform.localScale = Vector3.one * Mathf.Max(1f, pop) * 0.6f;
            if (cam != null) p.tmp.transform.rotation = Quaternion.LookRotation(p.tmp.transform.position - cam.transform.position);
            p.tmp.color = new Color(p.col.r, p.col.g, p.col.b, 1f - Mathf.Clamp01((k - 0.55f) / 0.45f));
            if (k >= 1f) { Destroy(p.tmp.gameObject); popups.RemoveAt(i); }
        }
    }

    // ── Procedural textures ──────────────────────────────────────────────
    static Texture2D MakeDot(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * n + x] = new Color(1, 1, 1, a * a);
            }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeFloorTexture(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
        var px = new Color[n * n];
        int tile = n / 2;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int tx = x / tile, ty = y / tile;
                float lx = (x % tile) / (float)tile, ly = (y % tile) / (float)tile;
                float edge = Mathf.Min(Mathf.Min(lx, 1 - lx), Mathf.Min(ly, 1 - ly));
                float grout = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((edge - 0.004f) / 0.012f));
                float tileTone = 0.9f + 0.12f * Mathf.PerlinNoise(tx * 3.1f + 7f, ty * 2.7f + 3f);
                float noise = 0.82f + 0.18f * Mathf.PerlinNoise(x * 0.045f, y * 0.045f)
                                    + 0.06f * Mathf.PerlinNoise(x * 0.3f, y * 0.3f);
                float v = 0.42f * tileTone * noise * Mathf.Lerp(0.62f, 1f, grout);
                px[y * n + x] = new Color(v * 0.93f, v * 0.96f, v * 1.06f, 1f);
            }
        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }
}
