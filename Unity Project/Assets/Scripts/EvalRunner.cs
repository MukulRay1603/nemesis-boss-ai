using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Sentis;
using UnityEngine;

/// <summary>
/// Headless evaluation and frame-time measurement. Does nothing unless the
/// player is started with one of these command-line flags:
///
///   -nemesisEval N      run N episodes, write per-episode CSV + summary JSON, quit
///   -nemesisFrames S    run S seconds in real time, record frame times, quit
///   -nemesisRecord S    render offscreen at a fixed frame rate and pipe frames to ffmpeg,
///                       stop after S seconds of game time or -fights K finished fights
///
/// Optional flags:
///   -boss rl|fsm        which boss to fight the scripted player (default rl)
///   -seed N             UnityEngine.Random seed for the player/FSM (default 1)
///   -timeScale X        simulation speed for -nemesisEval (default 20, max 100)
///   -policy random|attack  ablation: same boss body, policy choice replaced (RL boss only)
///   -sentis cpu|gpu     Sentis backend for the RL boss (default: gpu, or cpu with -nographics)
///   -out PATH           output file prefix (default ./nemesis_eval); for -nemesisRecord, the .mp4 path
///   -fps N, -width W, -height H, -ffmpeg PATH, -fights K   recording options
///
/// All game logic runs in FixedUpdate (0.02 s), so episode outcomes do not
/// depend on timeScale. Only wall-clock timings (inference, frames) do.
/// </summary>
public class EvalRunner : MonoBehaviour
{
    enum Mode { Episodes, Frames, Record }

    Mode mode;
    int targetEpisodes;
    float frameSeconds;
    string boss = "rl";
    int seed = 1;
    float timeScale = 20f;
    string outPrefix = "nemesis_eval";

    readonly List<EpisodeResult> results = new List<EpisodeResult>();
    readonly List<double> frameMs = new List<double>(200000);
    float wallStart;
    float warmup = 2f;
    bool done;
    int fps = 30, width = 1920, height = 1080, stopAfterFights = 0;
    string ffmpegPath = "ffmpeg";
    float fsmDamageUsed;
    string playerPreset = "default";

    /// Harder scripted opponent used for evaluation: moves 30% faster, attacks 25% more often,
    /// keeps a wider gap and retreats faster. Damage and HP are unchanged.
    public static void ApplyHardPlayer(PlayerController pc)
    {
        pc.moveSpeed *= 1.3f;
        pc.attackCooldown *= 0.75f;
        pc.safeDistance *= 1.2f;
        pc.retreatSpeed *= 1.2f;
    }
    string policyName = "onnx";

    static string ArgValue(string name)
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string name)
        {
            int i = System.Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string eps = Arg("-nemesisEval"), frames = Arg("-nemesisFrames"), rec = Arg("-nemesisRecord");
        if (eps == null && frames == null && rec == null) return;

        var go = new GameObject("EvalRunner");
        var r = go.AddComponent<EvalRunner>();
        if (eps != null) { r.mode = Mode.Episodes; r.targetEpisodes = int.Parse(eps); }
        else if (frames != null) { r.mode = Mode.Frames; r.frameSeconds = float.Parse(frames, CultureInfo.InvariantCulture); }
        else { r.mode = Mode.Record; r.frameSeconds = float.Parse(rec, CultureInfo.InvariantCulture); }
        if (Arg("-fps") != null) r.fps = int.Parse(Arg("-fps"));
        if (Arg("-width") != null) r.width = int.Parse(Arg("-width"));
        if (Arg("-height") != null) r.height = int.Parse(Arg("-height"));
        if (Arg("-fights") != null) r.stopAfterFights = int.Parse(Arg("-fights"));
        if (Arg("-ffmpeg") != null) r.ffmpegPath = Arg("-ffmpeg");
        r.boss = (Arg("-boss") ?? "rl").ToLowerInvariant();
        if (Arg("-seed") != null) r.seed = int.Parse(Arg("-seed"));
        if (Arg("-timeScale") != null) r.timeScale = float.Parse(Arg("-timeScale"), CultureInfo.InvariantCulture);
        if (Arg("-out") != null) r.outPrefix = Arg("-out");
        string sentis = Arg("-sentis");
        if (sentis == "cpu") BossAgent.BackendOverride = BackendType.CPU;
        if (sentis == "gpu") BossAgent.BackendOverride = BackendType.GPUCompute;
        // Seed the policy's action sampling too, so a (seed, backend) pair replays the same fights.
        BossAgent.PolicySeed = r.seed;
        BossAgent.PolicyOverride = Arg("-policy");   // null (trained ONNX policy), "random" or "attack"
        r.policyName = BossAgent.PolicyOverride ?? "onnx";
        r.Setup();
    }

    void Setup()
    {
        UnityEngine.Random.InitState(seed);
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // Pick which boss fights. Runs before any Start(), so the disabled one never initialises.
        var bossGo = GameObject.Find("Boss");
        var rl = bossGo.GetComponent<BossAgent>();
        var fsm = bossGo.GetComponent<ScriptedBoss>();
        rl.enabled = boss == "rl";
        fsm.enabled = boss == "fsm";
        // The scene gives the FSM baseline 8 damage vs the RL boss's 10; -fsmDamage lets you match them.
        string fsmDmg = ArgValue("-fsmDamage");
        if (fsmDmg != null) fsm.attackDamage = float.Parse(fsmDmg, CultureInfo.InvariantCulture);
        fsmDamageUsed = fsm.attackDamage;

        // Opponent difficulty. "default" = scene values; "hard" = faster, more aggressive bot.
        playerPreset = (ArgValue("-playerPreset") ?? "default").ToLowerInvariant();
        var pc = rl.player != null ? rl.player.GetComponent<PlayerController>() : null;
        if (pc != null && playerPreset == "hard") ApplyHardPlayer(pc);

        if (mode == Mode.Episodes)
        {
            // No taunt bridge during evaluation, and keep the log small.
            foreach (var t in FindObjectsOfType<TauntDisplay>()) t.enabled = false;
            Debug.unityLogger.filterLogType = LogType.Warning;
            Time.timeScale = Mathf.Clamp(timeScale, 1f, 100f);
        }

        EpisodeStats.Ended += r => results.Add(r);
        if (mode == Mode.Record) StartCoroutine(Record());
        wallStart = Time.realtimeSinceStartup;
        Debug.LogWarning($"[Eval] mode={mode} boss={boss} seed={seed} timeScale={Time.timeScale} " +
                         $"episodes={targetEpisodes} frames={frameSeconds}s");
    }

    void Update()
    {
        if (done) return;
        if (mode != Mode.Episodes)
        {
            // Graphics presets may turn vsync on; measurement and capture need it off.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }

        if (mode == Mode.Frames)
        {
            float t = Time.realtimeSinceStartup - wallStart;
            if (t > warmup) frameMs.Add(Time.unscaledDeltaTime * 1000.0);
            if (t > warmup + frameSeconds) Finish();
        }
        else if (mode == Mode.Episodes && results.Count >= targetEpisodes)
        {
            Finish();
        }
    }

    // ── Offscreen recording ──────────────────────────────────────────────
    System.Collections.IEnumerator Record()
    {
        Time.captureFramerate = fps;   // game time advances exactly 1/fps per frame, however slow encoding is
        var cam = Camera.main;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;

        // UI goes through a separate overlay camera with no post-processing, so bloom and
        // tonemapping on the 3D scene do not wash out the HUD text.
        const int uiLayer = 5;
        var uiGo = new GameObject("RecordUICamera");
        var uiCam = uiGo.AddComponent<Camera>();
        uiCam.cullingMask = 1 << uiLayer;
        // A second camera draws only the UI into its own transparent texture, which is then
        // alpha-blended over the scene image each frame.
        // URP writes opaque alpha, so render the UI over black and over white and recover coverage
        // from the difference (see Resources/NemesisFX/UIComposite.shader).
        var uiBlack = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var uiWhite = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        uiCam.clearFlags = CameraClearFlags.SolidColor;
        uiCam.depth = cam.depth + 1;
        var compose = new Material(Resources.Load<Shader>("NemesisFX/UIComposite"));
        compose.SetTexture("_UIBlack", uiBlack);
        compose.SetTexture("_UIWhite", uiWhite);
        var outRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        var uiData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(uiCam);
        uiData.renderPostProcessing = false;
        uiData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
        cam.cullingMask &= ~(1 << uiLayer);
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

        string outFile = outPrefix.EndsWith(".mp4") ? outPrefix : outPrefix + ".mp4";
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpegPath,
            $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i - " +
            $"-vf vflip -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart \"{outFile}\"")
        { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
        var ff = System.Diagnostics.Process.Start(psi);
        var pipe = ff.StandardInput.BaseStream;

        int frame = 0;
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            // Overlay-mode canvases do not draw into a camera's RenderTexture; move them onto the camera.
            foreach (var c in FindObjectsOfType<Canvas>())
                if (c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = uiCam;
                    c.planeDistance = 1f;
                    foreach (var tr in c.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = uiLayer;
                }
            // -batchmode skips the normal render loop, so draw the camera ourselves.
            if (Application.isBatchMode) cam.Render();
            uiCam.targetTexture = uiBlack; uiCam.backgroundColor = Color.black; uiCam.Render();
            uiCam.targetTexture = uiWhite; uiCam.backgroundColor = Color.white; uiCam.Render();
            Graphics.Blit(rt, outRt, compose);
            var prev = RenderTexture.active;
            RenderTexture.active = outRt;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = prev;
            var bytes = tex.GetRawTextureData();
            pipe.Write(bytes, 0, bytes.Length);
            frame++;

            bool fightsDone = stopAfterFights > 0 && results.Count >= stopAfterFights;
            if (frame >= frameSeconds * fps || fightsDone) break;
        }
        pipe.Flush();
        pipe.Close();
        ff.WaitForExit();
        Debug.LogWarning($"[Eval] recorded {frame} frames -> {outFile}");
        Finish();
    }

    void Finish()
    {
        done = true;
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPrefix));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var inv = CultureInfo.InvariantCulture;
        if (results.Count > 0)
        {
            var csv = new StringBuilder("episode,boss,outcome,duration_s,boss_hp,player_hp,final_phase," +
                                        "boss_swings,boss_hits,act_idle,act_attack,act_dodge,act_special\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var a = r.actionCounts ?? new int[4];
                csv.AppendLine(string.Join(",", (i + 1).ToString(), r.boss, r.outcome,
                    r.duration.ToString("F2", inv), r.bossHP.ToString("F1", inv), r.playerHP.ToString("F1", inv),
                    r.finalPhase, r.bossSwings, r.bossHits, a[0], a[1], a[2], a[3]));
            }
            File.WriteAllText(outPrefix + "_episodes.csv", csv.ToString());
        }

        var rlBoss = FindObjectOfType<BossAgent>();
        // Skip the first 10 calls: shader/kernel warm-up is a one-off cost.
        var inf = BossAgent.InferenceMs.Skip(10).ToList();

        var s = new StringBuilder("{\n");
        void Kv(string k, string v, bool last = false) => s.Append($"  \"{k}\": {v}{(last ? "" : ",")}\n");
        string Q(string v) => "\"" + v + "\"";
        Kv("mode", Q(mode.ToString()));
        Kv("boss", Q(boss));
        Kv("seed", seed.ToString());
        Kv("time_scale", Time.timeScale.ToString(inv));
        Kv("fixed_dt", Time.fixedDeltaTime.ToString(inv));
        Kv("unity", Q(Application.unityVersion));
        Kv("gpu", Q(SystemInfo.graphicsDeviceName));
        Kv("cpu", Q(SystemInfo.processorType));
        Kv("graphics_api", Q(SystemInfo.graphicsDeviceType.ToString()));
        Kv("screen", Q($"{Screen.width}x{Screen.height}"));
        Kv("sentis_backend", Q(rlBoss != null && rlBoss.enabled ? rlBoss.ActiveBackend.ToString() : "n/a"));
        if (boss == "fsm") Kv("fsm_damage", fsmDamageUsed.ToString(inv));
        if (boss == "rl") Kv("policy", Q(policyName));
        Kv("player_preset", Q(playerPreset));
        Kv("wall_seconds", (Time.realtimeSinceStartup - wallStart).ToString("F1", inv));
        if (results.Count > 0)
        {
            int n = results.Count;
            int bw = results.Count(r => r.outcome == "BOSS_WIN");
            int pw = results.Count(r => r.outcome == "PLAYER_WIN");
            int to = results.Count(r => r.outcome == "TIMEOUT");
            Kv("episodes", n.ToString());
            Kv("boss_wins", bw.ToString());
            Kv("player_wins", pw.ToString());
            Kv("timeouts", to.ToString());
            Kv("boss_win_rate", ((double)bw / n).ToString("F4", inv));
            Kv("mean_duration_s", results.Average(r => r.duration).ToString("F2", inv));
            Kv("mean_boss_hp_end", results.Average(r => r.bossHP).ToString("F1", inv));
            Kv("mean_player_hp_end", results.Average(r => r.playerHP).ToString("F1", inv));
            int sw = results.Sum(r => r.bossSwings), ht = results.Sum(r => r.bossHits);
            Kv("boss_swings", sw.ToString());
            Kv("boss_hits", ht.ToString());
            if (boss == "rl")
                for (int k = 0; k < 4; k++)
                    Kv("action_" + k, results.Sum(r => r.actionCounts[k]).ToString());
        }
        if (inf.Count > 0) AppendStats(s, "inference_ms", inf);
        if (frameMs.Count > 0) AppendStats(s, "frame_ms", frameMs);
        Kv("end", "true", true);
        s.Append("}\n");
        File.WriteAllText(outPrefix + "_summary.json", s.ToString());
        Debug.LogWarning("[Eval] done -> " + outPrefix + "_summary.json");
        Application.Quit();
    }

    static void AppendStats(StringBuilder s, string name, List<double> v)
    {
        var inv = CultureInfo.InvariantCulture;
        var sorted = v.OrderBy(x => x).ToList();
        double P(double q) => sorted[Mathf.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
        s.Append($"  \"{name}\": {{\"n\": {sorted.Count}, \"mean\": {v.Average().ToString("F4", inv)}, " +
                 $"\"p50\": {P(0.5).ToString("F4", inv)}, \"p95\": {P(0.95).ToString("F4", inv)}, " +
                 $"\"p99\": {P(0.99).ToString("F4", inv)}, \"max\": {sorted[sorted.Count - 1].ToString("F4", inv)}}},\n");
    }
}
