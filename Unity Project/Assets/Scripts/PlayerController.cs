//[OLD SCRIPT PHASE -1 ]
//using UnityEngine;

//public class PlayerController : MonoBehaviour
//{
//    private Rigidbody rb;

//    [Header("Movement")]
//    public float moveSpeed = 2f;
//    public float arenaSize = 9f;

//    private float directionTimer = 0f;
//    private float directionInterval = 2f;
//    private Vector3 moveDirection;

//    void Start()
//    {
//        rb = GetComponent<Rigidbody>();
//        if (rb == null)
//            rb = gameObject.AddComponent<Rigidbody>();

//        rb.freezeRotation = true;
//        PickNewDirection();
//    }

//    void FixedUpdate()
//    {
//        directionTimer += Time.fixedDeltaTime;
//        if (directionTimer >= directionInterval)
//        {
//            PickNewDirection();
//            directionTimer = 0f;
//        }

//        // Move in current direction
//        Vector3 newPos = transform.position + moveDirection * moveSpeed * Time.fixedDeltaTime;

//        // Clamp to arena
//        newPos.x = Mathf.Clamp(newPos.x, -arenaSize, arenaSize);
//        newPos.z = Mathf.Clamp(newPos.z, -arenaSize, arenaSize);
//        newPos.y = 1f;

//        rb.MovePosition(newPos);
//    }

//    void PickNewDirection()
//    {
//        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
//        moveDirection = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
//    }

//    // Called by BossAgent to reset position each episode
//    public void ResetPlayer()
//    {
//        transform.position = new Vector3(0f, 1f, -6f);
//        rb.velocity = Vector3.zero;
//        rb.angularVelocity = Vector3.zero;
//        PickNewDirection();
//    }
//}

using UnityEngine;

public class PlayerController : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────
    [Header("Movement")]
    public float moveSpeed = 2.8f;
    public float arenaLimit = 8.0f;

    [Header("Combat")]
    public float attackRange = 2.3f;
    public float attackDamage = 5f;
    public float attackCooldown = 1.8f;
    public float safeDistance = 5.0f;   // preferred distance from boss
    public float retreatSpeed = 4.0f;

    [Header("Stats")]
    public float playerHP = 250f;
    public float maxHP = 250f;

    // ── State machine ──────────────────────────────────────────────────────
    private enum PlayerState
    {
        KeepDistance,   // maintain safe distance, wait for opportunity
        Strafe,         // circle boss at safe range
        Commit,         // dart in to attack
        Retreat,        // back off after hit or when boss attacks
        Dodge           // emergency sidestep
    }
    private PlayerState state = PlayerState.KeepDistance;

    // ── Components ─────────────────────────────────────────────────────────
    private Rigidbody rb;
    private Transform boss;
    private BossAgent bossAgent;

    // ── Timers ─────────────────────────────────────────────────────────────
    private float attackTimer = 0f;
    private float retreatTimer = 0f;
    private float strafeTimer = 0f;
    private float dodgeTimer = 0f;
    private float colorTimer = 0f;
    private float strafeSign = 1f;
    private Vector3 retreatDir;
    private Vector3 dodgeDir;

    // ── Visual ─────────────────────────────────────────────────────────────
    private Renderer rend;
    private Material mat;
    private static readonly Color IdleColor = Color.white;
    private static readonly Color AttackColor = new Color(0.3f, 0.8f, 1f);
    private static readonly Color RetreatColor = new Color(1f, 0.85f, 0.1f);
    private static readonly Color DodgeColor = new Color(0.4f, 1f, 0.4f);

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rend = GetComponent<Renderer>();
        if (rend != null) mat = rend.material;

        GameObject bossObj = GameObject.Find("Boss");
        if (bossObj != null)
        {
            boss = bossObj.transform;
            bossAgent = bossObj.GetComponent<BossAgent>();
        }
    }

    // Called by BossAgent.ResetEpisode()
    public void ResetState()
    {
        playerHP = maxHP;
        state = PlayerState.KeepDistance;
        attackTimer = 0f;
        dodgeTimer = 0f;
        retreatTimer = 0f;
        strafeTimer = 0f;
        colorTimer = 0f;
        if (rb != null) { rb.velocity = rb.angularVelocity = Vector3.zero; }
        SetColor(IdleColor);
    }

    // ──────────────────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (boss == null || bossAgent == null) return;

        // Tick timers
        attackTimer -= Time.fixedDeltaTime;
        dodgeTimer -= Time.fixedDeltaTime;
        retreatTimer -= Time.fixedDeltaTime;
        colorTimer -= Time.fixedDeltaTime;
        if (colorTimer <= 0f) SetColor(IdleColor);

        float dist = Vector3.Distance(transform.position, boss.position);
        Vector3 dirToBoss = (boss.position - transform.position).normalized;
        Vector3 perp = new Vector3(-dirToBoss.z, 0f, dirToBoss.x) * strafeSign;

        // ── Boss state awareness ───────────────────────────────────────────
        bool bossIsVulnerable = bossAgent.currentBossState == BossAgent.BossState.Recovery;
        bool bossIsAttacking = bossAgent.currentBossState == BossAgent.BossState.Attack ||
                                bossAgent.currentBossState == BossAgent.BossState.Telegraph;

        // ── Emergency: dodge if boss attacks and is very close ─────────────
        if (bossIsAttacking && dist < 3.0f && dodgeTimer <= 0f &&
            state != PlayerState.Dodge)
        {
            dodgeDir = (perp - dirToBoss * 0.5f).normalized;
            dodgeDir.y = 0f;
            dodgeTimer = 2.0f;
            SetColor(DodgeColor); colorTimer = 0.4f;
            state = PlayerState.Dodge;
        }

        // ── Wall safety override ───────────────────────────────────────────
        Vector3 pos = transform.position;
        bool nearWall = Mathf.Abs(pos.x) > arenaLimit - 1.5f ||
                        Mathf.Abs(pos.z) > arenaLimit - 1.5f;
        if (nearWall && state != PlayerState.Dodge)
        {
            Vector3 toCenter = (Vector3.zero - pos).normalized;
            rb.MovePosition(pos + toCenter * moveSpeed * 2f * Time.fixedDeltaTime);
            state = PlayerState.KeepDistance;
            goto ClampAndFace;
        }

        // ── State machine ──────────────────────────────────────────────────
        switch (state)
        {
            // ── KeepDistance: stay at safe range, wait for opening ─────────
            case PlayerState.KeepDistance:
                if (dist < safeDistance - 0.5f)
                {
                    // Too close — back off
                    rb.MovePosition(transform.position -
                        dirToBoss * moveSpeed * Time.fixedDeltaTime);
                }
                else if (dist > safeDistance + 1.5f)
                {
                    // Too far — close in
                    rb.MovePosition(transform.position +
                        dirToBoss * moveSpeed * 0.7f * Time.fixedDeltaTime);
                }
                else
                {
                    // Good range — start strafing
                    strafeSign = Random.value > 0.5f ? 1f : -1f;
                    strafeTimer = Random.Range(0.6f, 1.4f);
                    state = PlayerState.Strafe;
                }

                // Seize vulnerability window immediately
                if (bossIsVulnerable && dist < safeDistance + 1f &&
                    attackTimer <= 0f)
                {
                    state = PlayerState.Commit;
                }
                break;

            // ── Strafe: circle at safe distance, look for recovery window ──
            case PlayerState.Strafe:
                strafeTimer -= Time.fixedDeltaTime;

                // Circle
                rb.MovePosition(transform.position +
                    perp * moveSpeed * 0.9f * Time.fixedDeltaTime);

                // Maintain safe distance
                if (dist < safeDistance - 1f)
                    rb.MovePosition(transform.position -
                        dirToBoss * moveSpeed * 0.5f * Time.fixedDeltaTime);
                else if (dist > safeDistance + 2f)
                    rb.MovePosition(transform.position +
                        dirToBoss * moveSpeed * 0.5f * Time.fixedDeltaTime);

                // Jump on recovery window
                if (bossIsVulnerable && attackTimer <= 0f)
                {
                    state = PlayerState.Commit;
                    break;
                }

                if (strafeTimer <= 0f)
                {
                    strafeSign = -strafeSign; // reverse direction
                    strafeTimer = Random.Range(0.5f, 1.2f);
                    state = PlayerState.KeepDistance;
                }

                // Run from incoming attack
                if (bossIsAttacking && dist < 3.5f)
                {
                    retreatDir = (-dirToBoss).normalized;
                    retreatTimer = 0.6f;
                    state = PlayerState.Retreat;
                }
                break;

            // ── Commit: dart into attack range during boss recovery ─────────
            case PlayerState.Commit:
                rb.MovePosition(transform.position +
                    dirToBoss * moveSpeed * 2.2f * Time.fixedDeltaTime);

                if (dist <= attackRange && attackTimer <= 0f)
                {
                    bossAgent.TakeDamage(attackDamage);
                    attackTimer = attackCooldown;
                    SetColor(AttackColor); colorTimer = 0.3f;
                    Debug.Log($"[Player] Hit boss | BossHP={bossAgent.bossHP:F1}");

                    // Retreat after successful hit
                    retreatDir = (-dirToBoss +
                        new Vector3(Random.Range(-0.3f, 0.3f), 0f,
                                    Random.Range(-0.3f, 0.3f))).normalized;
                    retreatDir.y = 0f;
                    retreatTimer = 1.2f;
                    state = PlayerState.Retreat;
                }
                else if (!bossIsVulnerable && dist > attackRange)
                {
                    // Boss recovered before we landed — abort
                    state = PlayerState.KeepDistance;
                }
                break;

            // ── Retreat: back off after hit or incoming attack ─────────────
            case PlayerState.Retreat:
                rb.MovePosition(transform.position +
                    retreatDir * retreatSpeed * Time.fixedDeltaTime);
                SetColor(RetreatColor); colorTimer = 0.1f;

                if (retreatTimer <= 0f)
                    state = PlayerState.KeepDistance;

                // If boss chases into us while retreating, dodge
                if (dist < 1.8f && dodgeTimer <= 0f)
                {
                    dodgeDir = (perp - dirToBoss * 0.3f).normalized;
                    dodgeDir.y = 0f;
                    dodgeTimer = 2.0f;
                    SetColor(DodgeColor); colorTimer = 0.4f;
                    state = PlayerState.Dodge;
                }
                break;

            // ── Dodge: emergency sidestep burst ───────────────────────────
            case PlayerState.Dodge:
                rb.MovePosition(transform.position +
                    dodgeDir * retreatSpeed * 2f * Time.fixedDeltaTime);

                if (dodgeTimer < 2.0f - 0.4f) // short burst
                    state = PlayerState.KeepDistance;
                break;
        }

    ClampAndFace:
        // Face boss
        if (dirToBoss != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dirToBoss);

        // Arena clamp
        Vector3 p = transform.position;
        p.x = Mathf.Clamp(p.x, -arenaLimit, arenaLimit);
        p.z = Mathf.Clamp(p.z, -arenaLimit, arenaLimit);
        transform.position = p;
    }

    // ──────────────────────────────────────────────────────────────────────
    void SetColor(Color c)
    {
        if (mat == null) return;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        else mat.color = c;
    }

    public void TakeDamage(float damage) => playerHP -= damage;
}