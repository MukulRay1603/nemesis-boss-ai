using UnityEngine;

/// <summary>
/// Runtime camera rig with two modes (C cycles, or -camera third|broadcast):
///   ThirdPerson  over-the-shoulder lock-on behind the player, always framing the boss
///   Broadcast    elevated three-quarter view of the whole arena
/// Adds camera shake on hits (driven by CombatEvents, Perlin noise so it never touches
/// UnityEngine.Random, which the scripted player and FSM boss use).
/// The scene's original camera sat at player height behind the player, so the player capsule
/// hid the boss. Pass -fixedCamera to keep that original camera.
/// </summary>
public class ArenaCamera : MonoBehaviour
{
    public enum Mode { ThirdPerson, Broadcast }
    public static Mode CurrentMode = Mode.ThirdPerson;
    public static event System.Action ModeChanged;

    public Transform boss, player;

    [Header("Third person")]
    public float distance = 7.4f;
    public float height = 3.5f;
    public float shoulder = 2.3f;
    public float lookBias = 0.65f;          // 0 = look at player, 1 = look at boss
    public float positionSmooth = 0.16f;

    [Header("Broadcast")]
    public Vector3 broadcastOffset = new Vector3(0f, 12.5f, -12.5f);
    public float broadcastFollow = 0.35f;

    Camera cam;
    Vector3 vel, lookPoint, lastFwd = Vector3.forward;
    float trauma, shakeT;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-fixedCamera") >= 0) return;
        if (System.Array.IndexOf(args, "-nemesisEval") >= 0) return;
        int i = System.Array.IndexOf(args, "-camera");
        if (i >= 0 && i + 1 < args.Length)
            CurrentMode = args[i + 1].ToLowerInvariant().StartsWith("broad") ? Mode.Broadcast : Mode.ThirdPerson;

        var cam = Camera.main;
        var boss = GameObject.Find("Boss");
        var player = GameObject.Find("Player");
        if (cam == null || boss == null || player == null) return;
        var rig = cam.gameObject.AddComponent<ArenaCamera>();
        rig.boss = boss.transform;
        rig.player = player.transform;
    }

    public static void SetMode(Mode m)
    {
        CurrentMode = m;
        ModeChanged?.Invoke();
    }

    void Start()
    {
        cam = GetComponent<Camera>();
        CombatEvents.Hit += OnHit;
        Snap();
    }

    void OnDestroy() => CombatEvents.Hit -= OnHit;

    void OnHit(Vector3 pos, float dmg, bool byBoss, bool special)
    {
        // Bigger kick when the boss lands a blow; the camera is "with" the player.
        trauma = Mathf.Min(1f, trauma + (byBoss ? (special ? 0.75f : 0.5f) : 0.18f));
    }

    void Snap()
    {
        Desired(out var pos, out var look);
        transform.position = pos;
        lookPoint = look;
        transform.rotation = Quaternion.LookRotation(look - pos);
    }

    void Desired(out Vector3 pos, out Vector3 look)
    {
        if (CurrentMode == Mode.Broadcast)
        {
            Vector3 mid = (boss.position + player.position) * 0.5f;
            mid.y = 0f;
            Vector3 focus = Vector3.Lerp(Vector3.zero, mid, broadcastFollow);
            pos = focus + broadcastOffset;
            look = focus + Vector3.up * 0.5f;
            return;
        }

        Vector3 toBoss = boss.position - player.position;
        toBoss.y = 0f;
        Vector3 fwd = toBoss.sqrMagnitude > 0.04f ? toBoss.normalized : lastFwd;
        lastFwd = fwd;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 pivot = player.position + Vector3.up * 0.9f;

        // Pull back a little when the boss is far so both stay on screen.
        float dist = distance + Mathf.Clamp(toBoss.magnitude - 4f, 0f, 8f) * 0.25f;
        pos = pivot - fwd * dist + Vector3.up * height + right * shoulder;

        // Stay inside the arena walls (inner face at +-9.5) so they never block the view.
        pos.x = Mathf.Clamp(pos.x, -9.2f, 9.2f);
        pos.z = Mathf.Clamp(pos.z, -9.2f, 9.2f);
        look = Vector3.Lerp(pivot, boss.position + Vector3.up * 0.6f, lookBias);
    }

    GameObject playerLabel;

    void LateUpdate()
    {
        if (boss == null || player == null) return;
        // The floating "HUNTER" label sits right in the line of sight from behind the player.
        if (playerLabel == null)
            foreach (Transform c in player) if (c.name.StartsWith("Label_")) playerLabel = c.gameObject;
        if (playerLabel != null && FindObjectOfType<BossDebugOverlay>() == null)
            playerLabel.SetActive(CurrentMode == Mode.Broadcast);
        Desired(out var target, out var look);

        float dt = Time.deltaTime;
        transform.position = Vector3.SmoothDamp(transform.position, target, ref vel, positionSmooth, Mathf.Infinity, dt);
        lookPoint = Vector3.Lerp(lookPoint, look, 1f - Mathf.Exp(-10f * dt));
        transform.rotation = Quaternion.LookRotation(lookPoint - transform.position);
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, CurrentMode == Mode.Broadcast ? 50f : 58f, 1f - Mathf.Exp(-6f * dt));

        // Trauma-based shake: offset and roll from Perlin noise, scaled by trauma squared.
        shakeT += dt * 22f;
        float s = trauma * trauma;
        if (s > 0.0001f)
        {
            transform.position += transform.right * (Mathf.PerlinNoise(shakeT, 0.1f) - 0.5f) * 0.5f * s
                                + transform.up * (Mathf.PerlinNoise(0.3f, shakeT) - 0.5f) * 0.5f * s;
            transform.rotation *= Quaternion.Euler(0f, 0f, (Mathf.PerlinNoise(shakeT, 0.7f) - 0.5f) * 6f * s);
        }
        trauma = Mathf.Max(0f, trauma - dt * 1.6f);

        if (Input.GetKeyDown(KeyCode.C))
            SetMode(CurrentMode == Mode.ThirdPerson ? Mode.Broadcast : Mode.ThirdPerson);
    }
}
