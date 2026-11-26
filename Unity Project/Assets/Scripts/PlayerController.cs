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

    // Read-only, for the animation layer (FighterAnimator). 0 KeepDistance, 1 Strafe, 2 Commit, 3 Retreat, 4 Dodge
    public int StateIndex => (int)state;

    // ── Human control (play mode). Off by default, so the scripted bot and all evaluation are unchanged.
    [Header("Human control (play mode)")]
    public bool humanControlled = false;
    public float humanAttackCooldown = 0.8f;
    public float dashSpeed = 9f;
    public float dashDuration = 0.22f;
    public float dashCooldown = 0.8f;
    public float dashInvulnerability = 0.2f;
    public static event System.Action Swung;   // any human attack, hit or miss (for animation)
    private bool wantAttack, wantDash;
    private float dashTimer, dashCdTimer, invulnTimer;
    private Vector3 dashDir;
    public static bool InputBlocked = false;  // set while a menu is open
    // Touch controls (TouchControls.cs) write these; they share the keyboard path below.
    public static Vector2 VirtualMove;
    public static bool VirtualAttack, VirtualDash;

    // ── Agent control (co-training, TrainingBridge -hunter 1). Same moveset as a human player:
    // world-space move direction, attack and dash triggers. Off by default.
    [System.NonSerialized] public bool agentControlled = false;
    [System.NonSerialized] public Vector3 agentMove;
    [System.NonSerialized] public bool agentAttack, agentDash;
    public bool AttackReady => attackTimer <= 0f;
    public bool DashReady => dashCdTimer <= 0f;
    public bool Invulnerable => invulnTimer > 0f;

    // ── Components ─────────────────────────────────────────────────────────
    private Rigidbody rb;
    private Transform boss;
    private BossAgent bossAgent;
    private ScriptedBoss scriptedBoss;   // ← NEW: reference for FSM eval mode

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
    private static readonly Color IdleColor = new Color(0.45f, 0.65f, 1f);   // blue, so the player never looks like the white phase-1 boss
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
            scriptedBoss = bossObj.GetComponent<ScriptedBoss>();   // ← NEW
        }
    }

    // Called by BossAgent.ResetEpisode() and ScriptedBoss.ResetEpisode()
    public void ResetState()
    {
        playerHP = maxHP;
        state = PlayerState.KeepDistance;
        dashTimer = dashCdTimer = invulnTimer = 0f;
        wantAttack = wantDash = false;
        agentAttack = agentDash = false;
        agentMove = Vector3.zero;
        attackTimer = 0f;
        dodgeTimer = 0f;
        retreatTimer = 0f;
        strafeTimer = 0f;
        colorTimer = 0f;
        if (rb != null) { rb.velocity = rb.angularVelocity = Vector3.zero; }
        SetColor(IdleColor);
    }

    // ── Helper: deal damage to whichever boss is active ───────────────────
    private void DamageBoss(float dmg)
    {
        if (boss != null) CombatEvents.RaiseHit(boss.position, dmg, false, false);
        if (bossAgent != null && bossAgent.enabled)
            bossAgent.TakeDamage(dmg);
        else if (scriptedBoss != null && scriptedBoss.enabled)
            scriptedBoss.TakeDamage(dmg);
    }

    // ── Helper: get current boss HP for logging ────────────────────────────
    private float GetBossHP()
    {
        if (bossAgent != null && bossAgent.enabled) return bossAgent.bossHP;
        if (scriptedBoss != null && scriptedBoss.enabled) return scriptedBoss.bossHP;
        return 0f;
    }

    // ── Helper: is the RL boss in a vulnerable state? ──────────────────────
    // FSM boss has no recovery window — always returns false in FSM mode,
    // which means the player commits on cooldown instead (correct behavior).
    private bool BossIsVulnerable()
    {
        if (bossAgent != null && bossAgent.enabled)
            return bossAgent.currentBossState == BossAgent.BossState.Recovery;
        return false;
    }

    // ── Helper: is the RL boss actively attacking? ─────────────────────────
    // FSM boss attacks without telegraph — always returns false in FSM mode,
    // so the player doesn't dodge phantom telegraphs.
    private bool BossIsAttacking()
    {
        if (bossAgent != null && bossAgent.enabled)
            return bossAgent.currentBossState == BossAgent.BossState.Attack ||
                   bossAgent.currentBossState == BossAgent.BossState.Telegraph;
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    void Update()
    {
        if (agentControlled) return;
        if (!humanControlled || InputBlocked) { VirtualAttack = VirtualDash = false; return; }
        if (VirtualAttack) { wantAttack = true; VirtualAttack = false; }
        if (VirtualDash) { wantDash = true; VirtualDash = false; }
        // On touch screens a tap on the screen is not an attack; the ATTACK button is.
        if (Input.GetKeyDown(KeyCode.Space) || (Input.GetMouseButtonDown(0) && !TouchControls.Active)) wantAttack = true;
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)) wantDash = true;
    }

    void HumanFixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        attackTimer -= dt; dashTimer -= dt; dashCdTimer -= dt; invulnTimer -= dt; colorTimer -= dt;
        if (colorTimer <= 0f) SetColor(IdleColor);

        Vector3 dirToBoss = (boss.position - transform.position); dirToBoss.y = 0f;
        float dist = dirToBoss.magnitude;
        dirToBoss = dist > 0.001f ? dirToBoss / dist : transform.forward;

        // Camera-relative WASD
        var cam = Camera.main;
        Vector3 fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 move = Vector3.zero;
        if (agentControlled)
        {
            move = agentMove;
            if (agentAttack) wantAttack = true;
            if (agentDash) wantDash = true;
            agentAttack = agentDash = false;
        }
        else if (!InputBlocked)
        {
            if (Input.GetKey(KeyCode.W)) move += fwd;
            if (Input.GetKey(KeyCode.S)) move -= fwd;
            if (Input.GetKey(KeyCode.D)) move += right;
            if (Input.GetKey(KeyCode.A)) move -= right;
            move += fwd * VirtualMove.y + right * VirtualMove.x;
        }
        move = move.sqrMagnitude > 0.01f ? move.normalized : Vector3.zero;

        if (wantDash && dashCdTimer <= 0f)
        {
            dashDir = move != Vector3.zero ? move : -dirToBoss;
            dashTimer = dashDuration;
            dashCdTimer = dashCooldown;
            invulnTimer = dashInvulnerability;
            SetColor(DodgeColor); colorTimer = 0.3f;
        }
        wantDash = false;

        if (dashTimer > 0f)
        {
            state = PlayerState.Dodge;
            rb.MovePosition(transform.position + dashDir * dashSpeed * dt);
        }
        else
        {
            state = move != Vector3.zero ? PlayerState.Strafe : PlayerState.KeepDistance;
            rb.MovePosition(transform.position + move * moveSpeed * 1.6f * dt);
        }

        if (wantAttack && attackTimer <= 0f)
        {
            attackTimer = humanAttackCooldown;
            Swung?.Invoke();
            if (dist <= attackRange)
            {
                DamageBoss(attackDamage);
                SetColor(AttackColor); colorTimer = 0.3f;
            }
        }
        wantAttack = false;

        transform.rotation = Quaternion.LookRotation(dirToBoss);
        Vector3 p = transform.position;
        p.x = Mathf.Clamp(p.x, -arenaLimit, arenaLimit);
        p.z = Mathf.Clamp(p.z, -arenaLimit, arenaLimit);
        transform.position = p;
    }

    void FixedUpdate()
    {
        // ← FIXED: only bail if boss transform is missing entirely
        if (boss == null) return;
        if (humanControlled || agentControlled) { HumanFixedUpdate(); return; }

        // Tick timers
        attackTimer -= Time.fixedDeltaTime;
        dodgeTimer -= Time.fixedDeltaTime;
        retreatTimer -= Time.fixedDeltaTime;
        colorTimer -= Time.fixedDeltaTime;
        if (colorTimer <= 0f) SetColor(IdleColor);

        float dist = Vector3.Distance(transform.position, boss.position);
        Vector3 dirToBoss = (boss.position - transform.position).normalized;
        Vector3 perp = new Vector3(-dirToBoss.z, 0f, dirToBoss.x) * strafeSign;

        // ── Boss state awareness (safe in both RL and FSM mode) ────────────
        bool bossIsVulnerable = BossIsVulnerable();
        bool bossIsAttacking = BossIsAttacking();

        // ── Emergency: dodge if RL boss attacks and is very close ──────────
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
                    rb.MovePosition(transform.position -
                        dirToBoss * moveSpeed * Time.fixedDeltaTime);
                }
                else if (dist > safeDistance + 1.5f)
                {
                    rb.MovePosition(transform.position +
                        dirToBoss * moveSpeed * 0.7f * Time.fixedDeltaTime);
                }
                else
                {
                    strafeSign = Random.value > 0.5f ? 1f : -1f;
                    strafeTimer = Random.Range(0.6f, 1.4f);
                    state = PlayerState.Strafe;
                }

                // Seize vulnerability window (RL only) or commit on cooldown (FSM)
                if (bossIsVulnerable && dist < safeDistance + 1f && attackTimer <= 0f)
                {
                    state = PlayerState.Commit;
                }
                // In FSM mode, commit whenever cooldown is ready and in range
                else if (!bossIsVulnerable && bossAgent == null || (bossAgent != null && !bossAgent.enabled))
                {
                    if (attackTimer <= 0f && dist <= safeDistance + 1f)
                        state = PlayerState.Commit;
                }
                break;

            // ── Strafe: circle at safe distance, look for recovery window ──
            case PlayerState.Strafe:
                strafeTimer -= Time.fixedDeltaTime;

                rb.MovePosition(transform.position +
                    perp * moveSpeed * 0.9f * Time.fixedDeltaTime);

                if (dist < safeDistance - 1f)
                    rb.MovePosition(transform.position -
                        dirToBoss * moveSpeed * 0.5f * Time.fixedDeltaTime);
                else if (dist > safeDistance + 2f)
                    rb.MovePosition(transform.position +
                        dirToBoss * moveSpeed * 0.5f * Time.fixedDeltaTime);

                if (bossIsVulnerable && attackTimer <= 0f)
                {
                    state = PlayerState.Commit;
                    break;
                }

                if (strafeTimer <= 0f)
                {
                    strafeSign = -strafeSign;
                    strafeTimer = Random.Range(0.5f, 1.2f);
                    state = PlayerState.KeepDistance;
                }

                if (attackTimer <= 0f && dist <= attackRange + 1.0f)
                    state = PlayerState.Commit;

                if (bossIsAttacking && dist < 3.5f)
                {
                    retreatDir = (-dirToBoss).normalized;
                    retreatTimer = 0.6f;
                    state = PlayerState.Retreat;
                }
                break;

            // ── Commit: dart into attack range ────────────────────────────
            case PlayerState.Commit:
                rb.MovePosition(transform.position +
                    dirToBoss * moveSpeed * 2.2f * Time.fixedDeltaTime);

                if (dist <= attackRange && attackTimer <= 0f)
                {
                    DamageBoss(attackDamage);   // ← FIXED: works in both modes
                    attackTimer = attackCooldown;
                    SetColor(AttackColor); colorTimer = 0.3f;
                    Debug.Log($"[Player] Hit boss | BossHP={GetBossHP():F1}");

                    retreatDir = (-dirToBoss +
                        new Vector3(Random.Range(-0.3f, 0.3f), 0f,
                                    Random.Range(-0.3f, 0.3f))).normalized;
                    retreatDir.y = 0f;
                    retreatTimer = 1.2f;
                    state = PlayerState.Retreat;
                }
                else if (!bossIsVulnerable && dist > attackRange)
                {
                    // In RL mode: abort if boss recovered. In FSM mode: always commit through.
                    if (bossAgent != null && bossAgent.enabled)
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

                if (dodgeTimer < 2.0f - 0.4f)
                    state = PlayerState.KeepDistance;
                break;
        }

    ClampAndFace:
        if (dirToBoss != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dirToBoss);

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

    public void TakeDamage(float damage)
    {
        if ((humanControlled || agentControlled) && invulnTimer > 0f) return;   // dash i-frames (human moveset only)
        playerHP -= damage;
    }
}