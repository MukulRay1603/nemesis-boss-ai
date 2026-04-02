//OLD ML-AGENTS SCRIPT - REFERENCE ONLY, NOT USED IN FINAL PROJECT

//using UnityEngine;
//using Unity.MLAgents;
//using Unity.MLAgents.Sensors;
//using Unity.MLAgents.Actuators;

//public class BossAgent : Agent
//{
//    [Header("References")]
//    public Transform player;
//    private Rigidbody rb;
//    private Rigidbody playerRb;
//    private PlayerController playerController;

//    [Header("Stats")]
//    public float bossHP = 100f;
//    public float playerHP = 100f;
//    public float maxHP = 100f;

//    [Header("Combat")]
//    public float attackRange = 2.5f;
//    public float attackDamage = 10f;
//    public float moveSpeed = 3f;
//    public float dodgeSpeed = 6f;

//    [Header("Curriculum")]
//    public float playerSpeedMultiplier = 1f;

//    [Header("Episode Tracking")]
//    private float episodeTime = 0f;
//    private float maxEpisodeTime = 15f;
//    private int lastAction = 0;
//    private float bossStamina = 100f;
//    private float maxStamina = 100f;

//    public override void Initialize()
//    {
//        Debug.Log("[BossAgent] Initialize called");
//        rb = GetComponent<Rigidbody>();
//        if (rb == null) Debug.LogError("[BossAgent] No Rigidbody!");
//        if (player == null) Debug.LogError("[BossAgent] Player is null!");
//        else
//        {
//            playerRb = player.GetComponent<Rigidbody>();
//            playerController = player.GetComponent<PlayerController>();
//            Debug.Log($"[BossAgent] playerRb={playerRb != null} playerController={playerController != null}");
//        }
//    }

//    public override void OnEpisodeBegin()
//    {
//        // Re-fetch references in case they weren't ready at Initialize
//        if (player != null && playerRb == null)
//            playerRb = player.GetComponent<Rigidbody>();
//        if (player != null && playerController == null)
//            playerController = player.GetComponent<PlayerController>();
//        Debug.Log("[BossAgent] OnEpisodeBegin called");

//        transform.position = new Vector3(0f, 1f, 6f);
//        rb.velocity = Vector3.zero;
//        rb.angularVelocity = Vector3.zero;

//        if (player != null)
//            player.position = new Vector3(0f, 1f, -6f);

//        // Skip playerController entirely for now
//        bossHP = maxHP;
//        playerHP = maxHP;
//        bossStamina = maxStamina;
//        episodeTime = 0f;
//        lastAction = 0;

//        Debug.Log("[BossAgent] OnEpisodeBegin complete");
//    }

//    public override void CollectObservations(VectorSensor sensor)
//    {
//        float arenaSize = 10f;
//        sensor.AddObservation(bossHP / maxHP);
//        sensor.AddObservation(playerHP / maxHP);
//        sensor.AddObservation(transform.position.x / arenaSize);
//        sensor.AddObservation(transform.position.z / arenaSize);

//        if (player != null)
//        {
//            sensor.AddObservation(player.position.x / arenaSize);
//            sensor.AddObservation(player.position.z / arenaSize);
//            sensor.AddObservation(Vector3.Distance(transform.position, player.position) / (arenaSize * 2f));
//        }
//        else
//        {
//            sensor.AddObservation(0f);
//            sensor.AddObservation(0f);
//            sensor.AddObservation(0f);
//        }

//        float phase = 0f;
//        if (bossHP < maxHP * 0.66f) phase = 1f;
//        if (bossHP < maxHP * 0.33f) phase = 2f;
//        sensor.AddObservation(phase / 2f);
//        sensor.AddObservation((float)lastAction / 3f);

//        if (playerRb != null)
//        {
//            sensor.AddObservation(playerRb.velocity.x / 10f);
//            sensor.AddObservation(playerRb.velocity.z / 10f);
//        }
//        else
//        {
//            // Try to get it now if Initialize missed it
//            if (player != null)
//                playerRb = player.GetComponent<Rigidbody>();
//            sensor.AddObservation(0f);
//            sensor.AddObservation(0f);
//        }

//        sensor.AddObservation(bossStamina / maxStamina);
//        sensor.AddObservation(episodeTime / maxEpisodeTime);
//        sensor.AddObservation(playerSpeedMultiplier / 3f);
//    }

//    public override void OnActionReceived(ActionBuffers actions)
//    {
//        //var envParams = Academy.Instance.EnvironmentParameters;
//        //playerSpeedMultiplier = envParams.GetWithDefault("player_speed", 1f);

//        int action = actions.DiscreteActions[0];
//        lastAction = action;
//        episodeTime += Time.fixedDeltaTime;

//        if (player == null)
//        {
//            if (episodeTime >= maxEpisodeTime) EndEpisode();
//            return;
//        }

//        Vector3 dirToPlayer = (player.position - transform.position).normalized;
//        float distToPlayer = Vector3.Distance(transform.position, player.position);

//        if (dirToPlayer != Vector3.zero)
//            transform.rotation = Quaternion.LookRotation(dirToPlayer);

//        switch (action)
//        {
//            case 0:
//                AddReward(-0.005f);
//                break;

//            case 1:
//                rb.MovePosition(transform.position + dirToPlayer * moveSpeed * Time.fixedDeltaTime);
//                if (distToPlayer < attackRange)
//                {
//                    playerHP -= attackDamage;
//                    AddReward(0.4f);
//                    if (playerHP <= 0f) { AddReward(1.0f); EndEpisode(); return; }
//                }
//                else
//                {
//                    AddReward(0.05f);
//                }
//                break;

//            case 2:
//                Vector3 perp = new Vector3(-dirToPlayer.z, 0f, dirToPlayer.x);
//                rb.MovePosition(transform.position + perp * dodgeSpeed * Time.fixedDeltaTime);
//                AddReward(0.02f);
//                break;

//            case 3:
//                if (bossStamina >= 20f)
//                {
//                    rb.MovePosition(transform.position + dirToPlayer * moveSpeed * 2f * Time.fixedDeltaTime);
//                    bossStamina -= 20f;
//                    if (distToPlayer < attackRange * 1.5f)
//                    {
//                        playerHP -= attackDamage * 2f;
//                        AddReward(0.7f);
//                        if (playerHP <= 0f) { AddReward(1.0f); EndEpisode(); return; }
//                    }
//                }
//                else
//                {
//                    AddReward(-0.05f);
//                }
//                break;
//        }

//        bossStamina = Mathf.Min(maxStamina, bossStamina + 5f * Time.fixedDeltaTime);

//        if (Mathf.Abs(transform.position.x) > 9f || Mathf.Abs(transform.position.z) > 9f)
//            AddReward(-0.1f);

//        if (episodeTime >= maxEpisodeTime)
//            EndEpisode();
//    }

//    public override void Heuristic(in ActionBuffers actionsOut)
//    {
//        var d = actionsOut.DiscreteActions;
//        if (Input.GetKey(KeyCode.Alpha1) || Input.GetKey(KeyCode.UpArrow)) d[0] = 1;
//        else if (Input.GetKey(KeyCode.Alpha2) || Input.GetKey(KeyCode.LeftArrow)) d[0] = 2;
//        else if (Input.GetKey(KeyCode.Alpha3) || Input.GetKey(KeyCode.Space)) d[0] = 3;
//        else d[0] = 0;
//    }

//    public void TakeDamage(float damage)
//    {
//        bossHP -= damage;
//        AddReward(-0.3f);
//    }
//}



//using Unity.Barracuda;
using Unity.Sentis;
using UnityEngine;

public class BossAgent : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    public ModelAsset modelAsset;
    private Rigidbody rb;
    private Rigidbody playerRb;

    [Header("Stats")]
    public float bossHP = 100f;
    public float playerHP = 100f;
    public float maxHP = 100f;

    [Header("Combat")]
    public float attackRange = 2.5f;
    public float attackDamage = 10f;
    public float moveSpeed = 3f;
    public float dodgeSpeed = 6f;

    [Header("Episode Tracking")]
    private float episodeTime = 0f;
    private float maxEpisodeTime = 15f;
    private int lastAction = 0;
    private float bossStamina = 100f;
    private float maxStamina = 100f;
    private float playerSpeedMultiplier = 1f;

    // Sentis
    private Model m_RuntimeModel;
    private IWorker m_Worker;
    private bool m_Ready = false;
    private const int OBS_SIZE = 14;

    // Latency tracking
    private System.Diagnostics.Stopwatch m_SW = new System.Diagnostics.Stopwatch();
    private float m_TotalMs = 0f;
    private int m_Count = 0;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (player != null)
            playerRb = player.GetComponent<Rigidbody>();

        if (modelAsset == null)
        {
            Debug.LogError("[BossAgent] ModelAsset not assigned!");
            return;
        }

        m_RuntimeModel = ModelLoader.Load(modelAsset);
        m_Worker = WorkerFactory.CreateWorker(BackendType.GPUCompute, m_RuntimeModel);
        m_Ready = true;
        Debug.Log("[BossAgent] Sentis loaded. Ready.");

        ResetEpisode();
    }

    void ResetEpisode()
    {
        transform.position = new Vector3(0f, 1f, 6f);
        if (player != null) player.position = new Vector3(0f, 1f, -6f);
        bossHP = maxHP;
        playerHP = maxHP;
        bossStamina = maxStamina;
        episodeTime = 0f;
        lastAction = 0;
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    void FixedUpdate()
    {
        if (!m_Ready || player == null) return;

        episodeTime += Time.fixedDeltaTime;

        float[] obs = BuildObservations();

        m_SW.Restart();
        int action = RunInference(obs);
        m_SW.Stop();

        m_TotalMs += (float)(m_SW.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        m_Count++;
        if (m_Count % 100 == 0)
        {
            Debug.Log($"[Sentis] Avg {m_TotalMs / 100f:F2}ms/inference | action={action}");
            m_TotalMs = 0f;  // ← reset after each window
            m_Count = 0;     // ← reset count too
        }

        lastAction = action;
        ExecuteAction(action);

        if (episodeTime >= maxEpisodeTime || playerHP <= 0f || bossHP <= 0f)
            ResetEpisode();
    }

    float[] BuildObservations()
    {
        float arenaSize = 10f;
        float[] obs = new float[OBS_SIZE];
        obs[0] = bossHP / maxHP;
        obs[1] = playerHP / maxHP;
        obs[2] = transform.position.x / arenaSize;
        obs[3] = transform.position.z / arenaSize;
        obs[4] = player.position.x / arenaSize;
        obs[5] = player.position.z / arenaSize;
        obs[6] = Vector3.Distance(transform.position, player.position) / (arenaSize * 2f);
        float phase = 0f;
        if (bossHP < maxHP * 0.66f) phase = 1f;
        if (bossHP < maxHP * 0.33f) phase = 2f;
        obs[7] = phase / 2f;
        obs[8] = (float)lastAction / 3f;
        obs[9] = playerRb != null ? playerRb.velocity.x / 10f : 0f;
        obs[10] = playerRb != null ? playerRb.velocity.z / 10f : 0f;
        obs[11] = bossStamina / maxStamina;
        obs[12] = episodeTime / maxEpisodeTime;
        obs[13] = playerSpeedMultiplier / 3f;
        return obs;
    }

    int RunInference(float[] obs)
    {
        float[] masks = { 1f, 1f, 1f, 1f };
        using var obsTensor = new TensorFloat(new TensorShape(1, OBS_SIZE), obs);
        using var maskTensor = new TensorFloat(new TensorShape(1, 4), masks);
        m_Worker.SetInput("obs_0", obsTensor);
        m_Worker.SetInput("action_masks", maskTensor);
        m_Worker.Execute();
        var output = m_Worker.PeekOutput("discrete_actions") as TensorInt;
        output.MakeReadable();
        return output[0];
    }

    void ExecuteAction(int action)
    {
        Vector3 dirToPlayer = (player.position - transform.position).normalized;
        float distToPlayer = Vector3.Distance(transform.position, player.position);

        if (dirToPlayer != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dirToPlayer);

        switch (action)
        {
            case 1:
                rb.MovePosition(transform.position + dirToPlayer * moveSpeed * Time.fixedDeltaTime);
                if (distToPlayer < attackRange)
                    playerHP -= attackDamage;
                break;
            case 2:
                Vector3 perp = new Vector3(-dirToPlayer.z, 0f, dirToPlayer.x);
                rb.MovePosition(transform.position + perp * dodgeSpeed * Time.fixedDeltaTime);
                break;
            case 3:
                if (bossStamina >= 20f)
                {
                    rb.MovePosition(transform.position + dirToPlayer * moveSpeed * 2f * Time.fixedDeltaTime);
                    bossStamina -= 20f;
                    if (distToPlayer < attackRange * 1.5f)
                        playerHP -= attackDamage * 2f;
                }
                break;
        }

        bossStamina = Mathf.Min(maxStamina, bossStamina + 5f * Time.fixedDeltaTime);
        Vector3 pos = transform.position;
        pos.x = Mathf.Clamp(pos.x, -9f, 9f);
        pos.z = Mathf.Clamp(pos.z, -9f, 9f);
        transform.position = pos;
    }

    public void TakeDamage(float damage) => bossHP -= damage;

    void OnDestroy() => m_Worker?.Dispose();
}