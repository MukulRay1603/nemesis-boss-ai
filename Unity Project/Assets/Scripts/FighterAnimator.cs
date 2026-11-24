using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Procedural sword animation for the capsule fighters. Visual only.
/// The fighter's own capsule renderer is hidden and replaced by a child "body" that can lean
/// and bob freely (scaling or rotating the real capsule would change its collider). The body holds
/// an energy blade on a hand pivot that is posed every frame:
///   boss   - driven by BossAgent's real state machine, so the swing you see is exactly the
///            window in which the hit check runs: guard, wind-up (Telegraph), swing (Attack,
///            plus a full spin for the special), follow-through (Recovery), tuck (Dodge)
///   player - wind-up while committing, a swing when its hit lands, tuck on dodge, low guard on retreat
/// </summary>
public class FighterAnimator : MonoBehaviour
{
    public BossAgent boss;               // set for the boss
    public PlayerController player;      // set for the player
    public Transform body, hand;
    public Color bladeColor = Color.cyan;

    Material bladeMat, glowMat;
    TrailRenderer tipTrail;
    float playerSwing = -1f, commitTime;
    int lastPlayerState = -1;
    Quaternion handRot;

    static readonly Quaternion Guard = Quaternion.Euler(40f, 0f, -15f);
    static readonly Quaternion Windup = Quaternion.Euler(-55f, -35f, -40f);
    static readonly Quaternion Strike = Quaternion.Euler(118f, 55f, 30f);
    static readonly Quaternion Tuck = Quaternion.Euler(10f, 0f, -75f);
    static readonly Quaternion Lowered = Quaternion.Euler(80f, 10f, -10f);

    /// Builds the body, hand and blade under <paramref name="fighter"/>. Returns the visual body.
    public static FighterAnimator Attach(GameObject fighter, Material bodyMat, Material litTemplate,
                                         Material additiveTemplate, Color blade)
    {
        var a = fighter.AddComponent<FighterAnimator>();
        a.bladeColor = blade;

        var rootRenderer = fighter.GetComponent<Renderer>();
        var bodyGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        bodyGo.name = "VisualBody";
        DestroyImmediate(bodyGo.GetComponent<Collider>());
        bodyGo.transform.SetParent(fighter.transform, false);
        bodyGo.GetComponent<Renderer>().sharedMaterial = bodyMat;
        if (rootRenderer != null) rootRenderer.enabled = false;   // hide the physics capsule, keep its collider
        a.body = bodyGo.transform;

        var handGo = new GameObject("Hand");
        handGo.transform.SetParent(a.body, false);
        handGo.transform.localPosition = new Vector3(0.52f, 0.05f, 0.25f);
        a.hand = handGo.transform;
        a.handRot = Guard;

        a.bladeMat = new Material(litTemplate);
        a.bladeMat.SetColor("_BaseColor", Color.white);
        a.glowMat = new Material(additiveTemplate);

        Part(a.hand, PrimitiveType.Cylinder, new Vector3(0, 0.02f, 0), new Vector3(0.07f, 0.13f, 0.07f),
             MakeHiltMat(litTemplate));
        Part(a.hand, PrimitiveType.Cylinder, new Vector3(0, 0.72f, 0), new Vector3(0.045f, 0.58f, 0.045f), a.bladeMat);
        var glow = Part(a.hand, PrimitiveType.Cylinder, new Vector3(0, 0.72f, 0), new Vector3(0.13f, 0.61f, 0.13f), a.glowMat);
        glow.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

        var tip = new GameObject("BladeTip");
        tip.transform.SetParent(a.hand, false);
        tip.transform.localPosition = new Vector3(0, 1.22f, 0);
        a.tipTrail = tip.AddComponent<TrailRenderer>();
        a.tipTrail.time = 0.14f;
        a.tipTrail.minVertexDistance = 0.02f;
        a.tipTrail.widthCurve = new AnimationCurve(new Keyframe(0, 0.32f), new Keyframe(1, 0f));
        a.tipTrail.sharedMaterial = new Material(additiveTemplate);
        a.tipTrail.shadowCastingMode = ShadowCastingMode.Off;
        a.tipTrail.alignment = LineAlignment.View;
        a.tipTrail.emitting = false;
        return a;
    }

    static Material MakeHiltMat(Material lit)
    {
        var m = new Material(lit);
        m.SetColor("_BaseColor", new Color(0.15f, 0.15f, 0.17f));
        m.SetFloat("_Smoothness", 0.8f);
        m.SetColor("_EmissionColor", Color.black);
        return m;
    }

    static GameObject Part(Transform parent, PrimitiveType t, Vector3 pos, Vector3 scale, Material m)
    {
        var go = GameObject.CreatePrimitive(t);
        DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    void OnEnable() => CombatEvents.Hit += OnHit;
    void OnDisable() => CombatEvents.Hit -= OnHit;

    void OnHit(Vector3 pos, float dmg, bool byBoss, bool special)
    {
        if (player != null && !byBoss) playerSwing = 0f;   // the player's blow just landed
    }

    static float EaseOut(float k) => 1f - (1f - k) * (1f - k) * (1f - k);
    static float Smooth(float k) => k * k * (3f - 2f * k);

    void LateUpdate()
    {
        float dt = Time.deltaTime, t = Time.time;
        Quaternion target = Guard;
        float lean = 0f, roll = 0f, bob = Mathf.Sin(t * 2.4f + (boss != null ? 0f : 1.7f)) * 0.03f;
        bool slash = false, snap = false;
        float spin = 0f;

        if (boss != null)
        {
            var s = boss.currentBossState;
            float left = boss.StateTimeLeft;
            switch (s)
            {
                case BossAgent.BossState.Telegraph:
                {
                    float k = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, boss.telegraphDuration));
                    target = Quaternion.Slerp(Guard, Windup, EaseOut(k));
                    lean = -10f * k;
                    break;
                }
                case BossAgent.BossState.Attack:
                {
                    float k = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, boss.attackDuration));
                    target = Quaternion.Slerp(Windup, Strike, EaseOut(Mathf.Clamp01(k * 1.5f)));
                    if (boss.LastAction == 3) spin = 360f * EaseOut(k);
                    lean = 16f;
                    slash = true;
                    snap = true;
                    break;
                }
                case BossAgent.BossState.Recovery:
                {
                    float k = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, boss.recoveryDuration));
                    target = Quaternion.Slerp(Strike, Guard, Smooth(k));
                    lean = 12f * (1f - k);
                    slash = k < 0.15f;
                    break;
                }
                case BossAgent.BossState.Dodge:
                    target = Tuck;
                    roll = 20f;
                    lean = -4f;
                    break;
            }
        }
        else if (player != null)
        {
            int st = player.StateIndex;
            if (st != lastPlayerState) { commitTime = 0f; lastPlayerState = st; }
            commitTime += dt;
            if (st == 2) { target = Quaternion.Slerp(Guard, Windup, EaseOut(Mathf.Clamp01(commitTime / 0.3f))); lean = 8f; }
            else if (st == 3) target = Lowered;
            else if (st == 4) { target = Tuck; roll = -22f; }

            if (playerSwing >= 0f)
            {
                playerSwing += dt;
                float k = Mathf.Clamp01(playerSwing / 0.22f);
                target = Quaternion.Slerp(Windup, Strike, EaseOut(k));
                lean = 14f * (1f - k);
                slash = true;
                snap = true;
                if (playerSwing > 0.35f) playerSwing = -1f;
            }
        }

        handRot = snap ? target : Quaternion.Slerp(handRot, target, 1f - Mathf.Exp(-14f * dt));
        hand.localRotation = Quaternion.Euler(0f, spin, 0f) * handRot;
        body.localPosition = new Vector3(0f, bob, 0f);
        body.localRotation = Quaternion.Slerp(body.localRotation, Quaternion.Euler(lean, 0f, roll), 1f - Mathf.Exp(-12f * dt));

        // Blade: white-hot core with a coloured glow; brighter while slashing.
        float hum = 1f + 0.08f * Mathf.Sin(t * 37f) + (slash ? 0.8f : 0f);
        bladeMat.SetColor("_EmissionColor", Color.Lerp(bladeColor, Color.white, 0.35f) * 5f * hum);
        glowMat.SetColor("_BaseColor", new Color(bladeColor.r, bladeColor.g, bladeColor.b, 0.22f * hum));
        tipTrail.emitting = slash && QualityPresets.FxTier >= 1;
        tipTrail.startColor = new Color(bladeColor.r, bladeColor.g, bladeColor.b, 0.75f);
        tipTrail.endColor = new Color(bladeColor.r, bladeColor.g, bladeColor.b, 0f);
    }
}
