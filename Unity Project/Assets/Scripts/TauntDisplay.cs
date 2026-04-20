using System.Collections;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Genshin-style bottom dialogue bar for Nemesis taunts.
/// Attach to a persistent GameObject in the scene (e.g. "TauntBridge").
/// Requires a Canvas set up as described in the setup steps below.
/// </summary>
public class TauntDisplay : MonoBehaviour
{
    [Header("UI References")]
    public GameObject dialoguePanel;      // the dark bar panel
    public TextMeshProUGUI speakerLabel;   // "NEMESIS" text
    public TextMeshProUGUI tauntText;      // the taunt body

    [Header("Game References")]
    public BossAgent bossAgent;
    public PlayerController playerController;

    [Header("Timing")]
    public float sendInterval = 2.0f;
    public float displayDuration = 5.0f;
    public float typewriterSpeed = 0.04f;  // seconds per character

    [Header("Phase Colors")]
    public Color phase0Color = new Color(1.00f, 1.00f, 1.00f);   // white
    public Color phase1Color = new Color(1.00f, 0.45f, 0.00f);   // orange
    public Color phase2Color = new Color(0.85f, 0.10f, 0.10f);   // red

    // ── Private state ──────────────────────────────────────────────────────
    private TcpClient _client;
    private NetworkStream _stream;
    private bool _connected = false;
    private float _sendTimer = 0f;
    private float _hideTimer = 0f;
    private bool _showing = false;
    private Coroutine _typeCoroutine;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {   
        if (dialoguePanel != null) dialoguePanel.SetActive(false);
        if (tauntText != null) tauntText.text = "";
        if (speakerLabel != null) speakerLabel.text = "NEMESIS";
        Connect();
    }

    void Connect()
    {
        try
        {
            _client = new TcpClient("localhost", 9999);
            _stream = _client.GetStream();
            _connected = true;
            Debug.Log("[Taunt] Connected to state_bridge.py");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Taunt] Bridge not running — start state_bridge.py first. ({e.Message})");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    void Update()
    {
        if (!_connected) return;

        _sendTimer -= Time.deltaTime;

        // Auto-hide after displayDuration
        if (_showing)
        {
            _hideTimer -= Time.deltaTime;
            if (_hideTimer <= 0f) HidePanel();
        }

        if (_sendTimer <= 0f)
        {
            _sendTimer = sendInterval;
            TrySendState();
        }

        TryReadTaunt();
    }

    // ── Send game state to bridge ──────────────────────────────────────────
    void TrySendState()
    {
        if (_stream == null) return;
        try
        {
            float bHP = bossAgent != null ? bossAgent.bossHP / bossAgent.maxHP : 1f;
            float pHP = playerController != null ? playerController.playerHP / playerController.maxHP : 1f;
            int phase = bossAgent != null ? bossAgent.currentPhase : 0;
            string move = bossAgent != null ? GetMoveFromState(bossAgent.currentBossState) : "idle";

            string bHPStr = bHP.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string pHPStr = pHP.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string json = "{\"boss_hp\":" + bHPStr + ",\"phase\":" + phase +
                             ",\"last_move\":\"" + move + "\",\"player_hp\":" + pHPStr + "}\n";

            byte[] data = Encoding.UTF8.GetBytes(json);
            _stream.Write(data, 0, data.Length);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Taunt] Send error: {e.Message}");
            _connected = false;
        }
    }

    string GetMoveFromState(BossAgent.BossState state) => state switch
    {
        BossAgent.BossState.Attack => "attack",
        BossAgent.BossState.Telegraph => "attack",
        BossAgent.BossState.Dodge => "dodge",
        BossAgent.BossState.Recovery => "idle",
        _ => "idle",
    };

    // ── Read taunt from bridge ─────────────────────────────────────────────
    void TryReadTaunt()
    {
        if (_stream == null || !_stream.DataAvailable) return;
        try
        {
            byte[] buf = new byte[512];
            int n = _stream.Read(buf, 0, buf.Length);
            if (n <= 0) return;

            string taunt = Encoding.UTF8.GetString(buf, 0, n).Trim();
            if (taunt.Length == 0) return;

            ShowTaunt(taunt);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Taunt] Read error: {e.Message}");
        }
    }

    // ── Display ────────────────────────────────────────────────────────────
    void ShowTaunt(string taunt)
    {
        // Don't toggle SetActive — just reset the timer and update content
        if (dialoguePanel != null && !dialoguePanel.activeSelf)
            dialoguePanel.SetActive(true);

        _showing = true;
        _hideTimer = displayDuration;

        if (speakerLabel != null && bossAgent != null)
        {
            speakerLabel.color = bossAgent.currentPhase switch
            {
                1 => phase1Color,
                2 => phase2Color,
                _ => phase0Color,
            };
        }

        if (_typeCoroutine != null) StopCoroutine(_typeCoroutine);
        _typeCoroutine = StartCoroutine(TypewriterEffect(taunt));

        Debug.Log($"[Taunt] \"{taunt}\"");
    }

    IEnumerator TypewriterEffect(string fullText)
    {
        if (tauntText == null) yield break;
        tauntText.text = "";
        foreach (char c in fullText)
        {
            tauntText.text += c;
            yield return new WaitForSeconds(typewriterSpeed);
        }
    }

    void HidePanel()
    {
        _showing = false;
        if (_typeCoroutine != null) { StopCoroutine(_typeCoroutine); _typeCoroutine = null; }
        if (tauntText != null) tauntText.text = "";
        if (dialoguePanel != null) dialoguePanel.SetActive(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    void OnDestroy()
    {
        _stream?.Close();
        _client?.Close();
    }
}