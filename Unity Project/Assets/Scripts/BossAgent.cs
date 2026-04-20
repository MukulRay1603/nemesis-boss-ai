using Unity.Sentis;
using UnityEngine;

public class BossAgent : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────
    [Header("References")]
    public Transform player;
    public ModelAsset modelAsset;

    [Header("Stats")]
    public float bossHP = 300f;
    public float maxHP = 300f;

    [Header("Combat Tuning")]
    public float attackRange = 2.4f;
    public float attackDamage = 8f;
    public float moveSpeed = 3.0f;
    public float dodgeSpeed = 6.0f;

    [Header("Timing (seconds)")]
    public float idleDuration = 0.35f;
    public float telegraphDuration = 0.45f;
    public float attackDuration = 0.30f;
    public float recoveryDuration = 0.85f;
    public float dodgeDuration = 0.50f;

    // ── State machine ──────────────────────────────────────────────────────
    public enum BossState { Idle, Telegraph, Attack, Recovery, Dodge }
    public BossState currentBossState { get; private set; } = BossState.Idle;

    private float stateTimer = 0f;
    private int pendingAction = 0;
    private int lastAction = 0;
    private bool hasHitThisAttack = false;   // ← ONE hit per swing

    private float bossStamina = 100f;
    private float maxStamina = 100f;
    private float episodeTime = 0f;
    private float maxEpisodeTime = 90f;

    // ── Phase system ───────────────────────────────────────────────────────
    public int currentPhase { get; private set; } = 0;
    private int previousPhase = 0;
    private float phaseSpeedMult = 1f;
    private bool phase2Triggered = false;
    private float pulseTimer = 0f;
    private bool isPulsing = false;

    private Renderer bossRenderer;
    private Material bossMat;
    private static readonly Color Phase0Color = Color.white;
    private static readonly Color Phase1Color = new Color(1f, 0.45f, 0f);
    private static readonly Color Phase2Color = new Color(0.6f, 0f, 0f);

    // ── Components ─────────────────────────────────────────────────────────
    private Rigidbody rb;
    private Rigidbody playerRb;
    private PlayerController playerController;

    // ── Sentis ─────────────────────────────────────────────────────────────
    private Model m_RuntimeModel;
    private IWorker m_Worker;
    private bool m_Ready = false;
    private const int OBS_SIZE = 14;

    private System.Diagnostics.Stopwatch m_SW = new System.Diagnostics.Stopwatch();

    private int m_Count = 0;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        bossRenderer = GetComponent<Renderer>();
        if (bossRenderer != null) bossMat = bossRenderer.material;

        if (player != null)
        {
            playerRb = player.GetComponent<Rigidbody>();
            playerController = player.GetComponent<PlayerController>();
        }

        if (modelAsset == null) { Debug.LogError("[Boss] No ModelAsset!"); return; }

        m_RuntimeModel = ModelLoader.Load(modelAsset);
        m_Worker = WorkerFactory.CreateWorker(BackendType.GPUCompute, m_RuntimeModel);
        m_Ready = true;
        Debug.Log("[Boss] Sentis loaded. Ready.");

        ResetEpisode();
    }

    // ──────────────────────────────────────────────────────────────────────
    public void ResetEpisode()
    {
        transform.position = new Vector3(0f, 1f, 6f);
        transform.localScale = Vector3.one;

        if (player != null)
        {
            player.position = new Vector3(0f, 1f, -6f);
            playerController = playerController ?? player.GetComponent<PlayerController>();
            playerController?.ResetState();
        }

        bossHP = maxHP;
        bossStamina = maxStamina;
        episodeTime = 0f;
        lastAction = 0;
        pendingAction = 0;
        stateTimer = 0f;
        hasHitThisAttack = false;
        currentPhase = 0;
        previousPhase = 0;
        phase2Triggered = false;
        isPulsing = false;
        phaseSpeedMult = 1f;
        recoveryDuration = 0.85f;   // reset to base in case phase 2 shortened it

        SetBossState(BossState.Idle);

        if (rb != null) { rb.velocity = rb.angularVelocity = Vector3.zero; }
        ApplyPhaseColor(0);
    }

    // ──────────────────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (!m_Ready || player == null) return;

        episodeTime += Time.fixedDeltaTime;
        stateTimer -= Time.fixedDeltaTime;

        UpdatePhase();
        HandlePulse();
        RunStateMachine();

        float pHP = playerController != null ? playerController.playerHP : 0f;
        bool bossWins = pHP <= 0f;
        bool plWins = bossHP <= 0f;
        bool timeout = episodeTime >= maxEpisodeTime;

        if (bossWins || plWins || timeout)
        {
            string outcome = bossWins ? "BOSS_WIN" : plWins ? "PLAYER_WIN" : "TIMEOUT";
            Debug.Log($"RESULT|RL|{outcome}|{episodeTime:F1}|{bossHP:F1}|{pHP:F1}|{currentPhase}");
            ResetEpisode();
        }
    }

    // ── State machine ──────────────────────────────────────────────────────
    void RunStateMachine()
    {
        Vector3 dirToPlayer = (player.position - transform.position).normalized;
        float distToPlayer = Vector3.Distance(transform.position, player.position);

        if (dirToPlayer != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dirToPlayer);

        float spd = moveSpeed * phaseSpeedMult;

        switch (currentBossState)
        {
            // ── IDLE: brief pause, query policy ───────────────────────────
            case BossState.Idle:
                if (stateTimer <= 0f)
                {
                    pendingAction = QueryPolicy();
                    lastAction = pendingAction;

                    if (pendingAction == 2)
                        SetBossState(BossState.Dodge);
                    else if (pendingAction == 1 || pendingAction == 3)
                        SetBossState(BossState.Telegraph);
                    else
                        SetBossState(BossState.Idle);
                }
                break;

            // ── TELEGRAPH: readable windup, boss slows ─────────────────────
            case BossState.Telegraph:
                rb.MovePosition(transform.position +
                    dirToPlayer * spd * 0.15f * Time.fixedDeltaTime);

                if (stateTimer <= 0f)
                    SetBossState(BossState.Attack);
                break;

            // ── ATTACK: lunge — ONE hit maximum ────────────────────────────
            case BossState.Attack:
                float lunge = pendingAction == 3 ? spd * 2.5f : spd * 1.8f;
                rb.MovePosition(transform.position +
                    dirToPlayer * lunge * Time.fixedDeltaTime);

                if (distToPlayer < attackRange && !hasHitThisAttack)
                {
                    hasHitThisAttack = true;
                    float dmg = pendingAction == 3 ? attackDamage * 2f : attackDamage;

                    if (pendingAction == 3 && bossStamina >= 20f)
                    {
                        bossStamina -= 20f;
                        playerController?.TakeDamage(dmg);
                        Debug.Log($"[Boss] SPECIAL hit | " +
                                  $"pHP={(playerController?.playerHP ?? 0f):F1}");
                    }
                    else if (pendingAction == 1)
                    {
                        playerController?.TakeDamage(dmg);
                        Debug.Log($"[Boss] Hit | " +
                                  $"pHP={(playerController?.playerHP ?? 0f):F1}");
                    }
                }

                if (stateTimer <= 0f)
                    SetBossState(BossState.Recovery);
                break;

            // ── RECOVERY: slowed + VULNERABLE — player punishes here ───────
            case BossState.Recovery:
                rb.MovePosition(transform.position -
                    dirToPlayer * spd * 0.2f * Time.fixedDeltaTime);

                if (stateTimer <= 0f)
                    SetBossState(BossState.Idle);
                break;

            // ── DODGE: sidestep ────────────────────────────────────────────
            case BossState.Dodge:
                Vector3 perp = new Vector3(-dirToPlayer.z, 0f, dirToPlayer.x);
                rb.MovePosition(transform.position +
                    perp * dodgeSpeed * phaseSpeedMult * Time.fixedDeltaTime);

                if (stateTimer <= 0f)
                    SetBossState(BossState.Idle);
                break;
        }

        // Stamina regen + arena clamp
        bossStamina = Mathf.Min(maxStamina, bossStamina + 4f * Time.fixedDeltaTime);
        Vector3 p = transform.position;
        p.x = Mathf.Clamp(p.x, -9f, 9f);
        p.z = Mathf.Clamp(p.z, -9f, 9f);
        transform.position = p;
    }

    void SetBossState(BossState next)
    {
        currentBossState = next;
        switch (next)
        {
            case BossState.Idle:
                stateTimer = idleDuration;
                break;
            case BossState.Telegraph:
                stateTimer = telegraphDuration;
                break;
            case BossState.Attack:
                stateTimer = attackDuration;
                hasHitThisAttack = false;   // ← reset every new swing
                break;
            case BossState.Recovery:
                stateTimer = recoveryDuration;
                break;
            case BossState.Dodge:
                stateTimer = dodgeDuration;
                break;
        }
    }

    // ── Policy query ───────────────────────────────────────────────────────
    int QueryPolicy()
    {
        float[] obs = BuildObservations();
        float[] masks = { 1f, 1f, 1f, 1f };
        if (currentPhase < 2 && bossStamina < 20f) masks[3] = 0f;

        var obsTensor = new TensorFloat(new TensorShape(1, OBS_SIZE), obs);
        var maskTensor = new TensorFloat(new TensorShape(1, 4), masks);

        m_Worker.SetInput("obs_0", obsTensor);
        m_Worker.SetInput("action_masks", maskTensor);
        m_Worker.Execute();

        var output = m_Worker.PeekOutput("discrete_actions") as TensorInt;
        int action = 1;
        if (output != null) { output.MakeReadable(); action = output[0]; }

        obsTensor.Dispose();
        maskTensor.Dispose();

        m_Count++;
        if (m_Count % 50 == 0)
        {
            float pHP = playerController != null ? playerController.playerHP : 0f;
            Debug.Log($"[Sentis] action={action} phase={currentPhase} " +
                      $"state={currentBossState} bHP={bossHP:F1} pHP={pHP:F1}");
    
            m_Count = 0;
        }

        return action;
    }

    float[] BuildObservations()
    {
        float arena = 10f;
        float maxPHP = playerController != null ? playerController.maxHP : 250f;
        float pHP = playerController != null ? playerController.playerHP : maxPHP;
        float[] obs = new float[OBS_SIZE];

        obs[0] = bossHP / maxHP;
        obs[1] = pHP / maxPHP;
        obs[2] = transform.position.x / arena;
        obs[3] = transform.position.z / arena;
        obs[4] = player.position.x / arena;
        obs[5] = player.position.z / arena;
        obs[6] = Vector3.Distance(transform.position, player.position) / (arena * 2f);
        obs[7] = currentPhase / 2f;
        obs[8] = (float)lastAction / 3f;
        obs[9] = playerRb != null ? playerRb.velocity.x / 10f : 0f;
        obs[10] = playerRb != null ? playerRb.velocity.z / 10f : 0f;
        obs[11] = bossStamina / maxStamina;
        obs[12] = episodeTime / maxEpisodeTime;
        obs[13] = 1f / 3f;
        return obs;
    }

    // ── Phase ──────────────────────────────────────────────────────────────
    void UpdatePhase()
    {
        previousPhase = currentPhase;
        if (bossHP > maxHP * 0.66f) currentPhase = 0;
        else if (bossHP > maxHP * 0.33f) currentPhase = 1;
        else currentPhase = 2;

        if (currentPhase != previousPhase)
        {
            ApplyPhaseColor(currentPhase);
            Debug.Log($"[Boss] ── PHASE {currentPhase} ── HP={bossHP:F1}");

            if (currentPhase == 1)
            {
                phaseSpeedMult = 1.3f;
                recoveryDuration = 0.65f;
                Debug.Log("[Boss] Phase 1: Speed x1.3, shorter recovery");
            }
            if (currentPhase == 2 && !phase2Triggered)
            {
                phaseSpeedMult = 1.6f;
                recoveryDuration = 0.45f;
                phase2Triggered = true;
                isPulsing = true;
                pulseTimer = 0f;
                Debug.Log("[Boss] Phase 2: ENRAGED — Speed x1.6, recovery x0.45s");
            }
        }
    }

    void ApplyPhaseColor(int phase)
    {
        if (bossMat == null) return;
        Color c = phase == 0 ? Phase0Color : phase == 1 ? Phase1Color : Phase2Color;
        if (bossMat.HasProperty("_BaseColor")) bossMat.SetColor("_BaseColor", c);
        else bossMat.color = c;
    }

    void HandlePulse()
    {
        if (!isPulsing) return;
        pulseTimer += Time.fixedDeltaTime;
        float t = pulseTimer / 0.6f;
        if (t <= 0.5f) transform.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 1.4f, t * 2f);
        else if (t <= 1.0f) transform.localScale = Vector3.Lerp(Vector3.one * 1.4f, Vector3.one, (t - 0.5f) * 2f);
        else { transform.localScale = Vector3.one; isPulsing = false; }
    }

    public void TakeDamage(float damage) => bossHP -= damage;

    void OnDestroy() => m_Worker?.Dispose();
}
