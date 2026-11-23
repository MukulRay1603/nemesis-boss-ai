using UnityEngine;

/// <summary>
/// Elevated three-quarter camera that keeps both fighters in view.
/// The scene's original camera sits at player height behind the player, so the player
/// capsule hides the boss. This replaces it at runtime; pass -fixedCamera to keep the original.
/// </summary>
public class ArenaCamera : MonoBehaviour
{
    public Transform a, b;
    public Vector3 offset = new Vector3(0f, 12.5f, -12.5f);
    [Range(0f, 1f)] public float follow = 0.35f;   // 0 = locked on arena centre, 1 = locked on fighters
    public float smoothing = 3f;

    Vector3 focus;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (System.Array.IndexOf(args, "-fixedCamera") >= 0) return;
        var cam = Camera.main;
        var boss = GameObject.Find("Boss");
        var player = GameObject.Find("Player");
        if (cam == null || boss == null || player == null) return;
        var rig = cam.gameObject.AddComponent<ArenaCamera>();
        rig.a = boss.transform;
        rig.b = player.transform;
        cam.fieldOfView = 50f;

        // From the raised camera you can see past the walls into empty space, which renders as a
        // black band. Put a large, darker ground plane under the arena to fill it.
        var arena = GameObject.Find("Arena");
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Backdrop";
        Destroy(ground.GetComponent<Collider>());
        ground.transform.position = new Vector3(0f, -0.02f, 0f);
        ground.transform.localScale = new Vector3(30f, 1f, 30f);
        var r = ground.GetComponent<Renderer>();
        r.receiveShadows = false;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var src = arena != null ? arena.GetComponent<Renderer>() : null;
        if (src != null) r.sharedMaterial = new Material(src.sharedMaterial);
        var tint = new Color(0.32f, 0.30f, 0.29f);
        if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", tint);
        else r.sharedMaterial.color = tint;
    }

    void Start()
    {
        focus = Target();
        Place();
    }

    Vector3 Target()
    {
        Vector3 mid = (a.position + b.position) * 0.5f;
        mid.y = 0f;
        return Vector3.Lerp(Vector3.zero, mid, follow);
    }

    void LateUpdate()
    {
        focus = Vector3.Lerp(focus, Target(), 1f - Mathf.Exp(-smoothing * Time.deltaTime));
        Place();
    }

    void Place()
    {
        transform.position = focus + offset;
        transform.rotation = Quaternion.LookRotation((focus + Vector3.up * 0.5f) - transform.position);
    }
}
