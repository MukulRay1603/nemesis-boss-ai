using UnityEngine;

/// <summary>
/// Scripted FSM baseline boss for evaluation.
/// Same HP, damage, speed as RL boss — but no telegraph/recovery cycle,
/// no phase-aware speed multipliers, no policy-driven behavior variety.
/// Attach to Boss GameObject, disable BossAgent, enable this.
/// </summary>
public class ScriptedBoss : MonoBehaviour
{
    [Header("References")]
    public Transform player;

    [Header("Stats")]
    public float bossHP  = 300f;
    public float maxHP   = 300f;

    [Header("Combat — identical to BossAgent")]
    public float attackRange  = 2.4f;
    public float attackDamage = 8f;
    public float moveSpeed    = 3.0f;
    public float dodgeSpeed   = 6.0f;
    public float attackCooldown = 1.2f;

    [Header("Episode")]
    private float episodeTime    = 0f;
    private float maxEpisodeTime = 90f;
    private float attackTimer    = 0f;

    // Phase tracking (for logging only — no speed bonus)
    private int currentPhase = 0;

    // Visual — same color system so recording looks consistent
    private Renderer  bossRenderer;
    private Material  bossMat;
    private static readonly Color Phase0Color = Color.white;
    private static readonly Color Phase1Color = new Color(1f, 0.45f, 0f);
    private static readonly Color Phase2Color = new Color(0.6f, 0f, 0f);
    private int previousPhase = 0;

    private Rigidbody        rb;
    private PlayerController playerController;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        rb           = GetComponent<Rigidbody>();
        bossRenderer = GetComponent<Renderer>();
        if (bossRenderer != null) bossMat = bossRenderer.material;

        if (player != null)
            playerController = player.GetComponent<PlayerController>();

        ResetEpisode();
        Debug.Log("[ScriptedBoss] FSM baseline ready.");
    }

    void ResetEpisode()
    {
        transform.position   = new Vector3(0f, 1f, 6f);
        transform.localScale = Vector3.one;

        if (player != null)
        {
            player.position = new Vector3(0f, 1f, -6f);
            playerController = playerController ??
                               player.GetComponent<PlayerController>();
            playerController?.ResetState();
        }

        bossHP        = maxHP;
        episodeTime   = 0f;
        attackTimer   = 0f;
        currentPhase  = 0;
        previousPhase = 0;

        if (rb != null) { rb.velocity = rb.angularVelocity = Vector3.zero; }
        ApplyColor(Phase0Color);
    }

    // ──────────────────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (player == null) return;

        episodeTime += Time.fixedDeltaTime;
        attackTimer -= Time.fixedDeltaTime;

        UpdatePhase();

        Vector3 dirToPlayer  = (player.position - transform.position).normalized;
        float   distToPlayer = Vector3.Distance(transform.position, player.position);

        if (dirToPlayer != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dirToPlayer);

        // ── FSM logic: no telegraph, no recovery window ────────────────────
        // If player very close → 35% chance dodge sideways
        // If in attack range   → attack immediately (no windup)
        // Otherwise            → chase player

        if (distToPlayer < 1.8f && Random.value < 0.35f)
        {
            // Dumb random dodge — no intelligence
            Vector3 perp = new Vector3(-dirToPlayer.z, 0f, dirToPlayer.x);
            if (Random.value > 0.5f) perp = -perp;
            rb.MovePosition(transform.position +
                perp * dodgeSpeed * Time.fixedDeltaTime);
        }
        else if (distToPlayer < attackRange)
        {
            // Attack immediately — no telegraph means no readable opening
            // No recovery means no vulnerability window for player
            rb.MovePosition(transform.position +
                dirToPlayer * moveSpeed * 0.3f * Time.fixedDeltaTime);

            if (attackTimer <= 0f)
            {
                playerController?.TakeDamage(attackDamage);
                attackTimer = attackCooldown;
                float pHP = playerController != null ? playerController.playerHP : 0f;
                Debug.Log($"[FSM] Hit player | pHP={pHP:F1}");
            }
        }
        else
        {
            // Chase — constant speed, no phase multiplier
            rb.MovePosition(transform.position +
                dirToPlayer * moveSpeed * Time.fixedDeltaTime);
        }

        // Arena clamp
        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, -9f, 9f);
        pos.z = Mathf.Clamp(pos.z, -9f, 9f);
        transform.position = pos;

        // ── Episode end ────────────────────────────────────────────────────
        float playerHP = playerController != null ? playerController.playerHP : 0f;
        bool bossWins  = playerHP  <= 0f;
        bool plWins    = bossHP    <= 0f;
        bool timeout   = episodeTime >= maxEpisodeTime;

        if (bossWins || plWins || timeout)
        {
            string outcome = bossWins ? "BOSS_WIN" :
                             plWins   ? "PLAYER_WIN" : "TIMEOUT";
            // Same pipe-delimited format as BossAgent for easy parsing
            Debug.Log($"RESULT|FSM|{outcome}|{episodeTime:F1}|" +
                      $"{bossHP:F1}|{playerHP:F1}|{currentPhase}");
            ResetEpisode();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    void UpdatePhase()
    {
        previousPhase = currentPhase;
        if      (bossHP > maxHP * 0.66f) currentPhase = 0;
        else if (bossHP > maxHP * 0.33f) currentPhase = 1;
        else                             currentPhase = 2;

        // Color changes so recording looks consistent with RL boss
        // But NO speed multiplier — that's the RL boss's advantage
        if (currentPhase != previousPhase)
        {
            Color c = currentPhase == 1 ? Phase1Color : Phase2Color;
            ApplyColor(c);
            Debug.Log($"[FSM] Phase {currentPhase} | HP={bossHP:F1}");
        }
    }

    void ApplyColor(Color c)
    {
        if (bossMat == null) return;
        if (bossMat.HasProperty("_BaseColor")) bossMat.SetColor("_BaseColor", c);
        else bossMat.color = c;
    }

    public void TakeDamage(float damage) => bossHP -= damage;
}
