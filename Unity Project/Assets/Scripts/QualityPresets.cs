using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Four graphics presets plus "Auto", which picks one from the machine's specs.
///
///   Low     URP-Performant, 0.75 render scale, no shadows, no post-processing, minimal effects, 60 fps cap
///   Medium  URP-Balanced, full res, shadows, tonemapping/vignette, no bloom, reduced effects
///   High    URP-HighFidelity, 4x MSAA, soft shadows, full post-processing (bloom etc.), all effects
///   Ultra   High + 1.5x supersampling, longer shadow distance, extra lights
///
/// Choose in the in-game settings menu (Esc), or with -quality low|medium|high|ultra|auto.
/// The choice is saved in PlayerPrefs. URP assets are cloned at runtime so the project files never change.
/// </summary>
public static class QualityPresets
{
    public enum Preset { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    const string PrefKey = "nemesis.quality";   // -1 = auto

    public static Preset Current { get; private set; } = Preset.High;
    public static bool IsAuto { get; private set; } = true;
    public static event Action Changed;

    /// 0 = minimal, 1 = reduced, 2 = full, 3 = full + extras. Read by VisualFX.
    public static int FxTier => (int)Current;
    public static bool PostProcessing => Current >= Preset.Medium;
    public static bool Bloom => Current >= Preset.High;

    static UniversalRenderPipelineAsset[] clones;

    /// Picks a preset from VRAM, CPU cores and shader model. Thresholds are deliberately conservative.
    public static Preset Detect(out string reason)
    {
        int vram = SystemInfo.graphicsMemorySize;        // MB
        int cores = SystemInfo.processorCount;
        int shader = SystemInfo.graphicsShaderLevel;
        bool handheld = SystemInfo.deviceType == DeviceType.Handheld;
        bool integrated = SystemInfo.graphicsDeviceName.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          SystemInfo.graphicsDeviceName.IndexOf("UHD", StringComparison.OrdinalIgnoreCase) >= 0;

        Preset p;
        if (handheld || shader < 45 || vram < 2048) p = Preset.Low;
        else if (integrated || vram < 4096 || cores < 4) p = Preset.Medium;
        else if (vram < 8192 || cores < 8) p = Preset.High;
        else p = Preset.Ultra;

        // Browsers don't report VRAM reliably (it often reads as ~0), so the thresholds above are
        // meaningless there: default to Medium on desktop browsers and Low on phones and tablets.
        if (Application.platform == RuntimePlatform.WebGLPlayer)
            p = Application.isMobilePlatform || handheld ? Preset.Low : Preset.Medium;
        reason = $"{SystemInfo.graphicsDeviceName}, {vram / 1024f:0.#} GB VRAM, {cores} threads";
        return p;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;   // headless eval
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nemesisEval") >= 0) return;

        string arg = null;
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-quality");
        if (i >= 0 && i + 1 < args.Length) arg = args[i + 1].ToLowerInvariant();

        if (arg != null && arg != "auto" && Enum.TryParse(arg, true, out Preset fromArg)) Apply(fromArg, false);
        else if (arg == "auto") ApplyAuto();
        else
        {
            int saved = PlayerPrefs.GetInt(PrefKey, -1);
            if (saved >= 0 && saved <= 3) Apply((Preset)saved, false);
            else ApplyAuto();
        }
    }

    public static void ApplyAuto()
    {
        Apply(Detect(out _), true);
    }

    public static void Apply(Preset p, bool auto)
    {
        Current = p;
        IsAuto = auto;
        PlayerPrefs.SetInt(PrefKey, auto ? -1 : (int)p);

        // Quality levels in ProjectSettings: 0 Performant, 1 Balanced, 2 High Fidelity
        int level = Mathf.Min((int)p, 2);
        QualitySettings.SetQualityLevel(level, true);

        clones ??= new UniversalRenderPipelineAsset[4];
        if (clones[(int)p] == null && QualitySettings.renderPipeline is UniversalRenderPipelineAsset src)
        {
            var c = UnityEngine.Object.Instantiate(src);
            c.name = src.name + " (" + p + ")";
            switch (p)
            {
                case Preset.Low:
                    c.renderScale = 0.75f; c.msaaSampleCount = 1; c.shadowDistance = 0f; break;
                case Preset.Medium:
                    c.renderScale = 1f; c.msaaSampleCount = 2; c.shadowDistance = 35f; break;
                case Preset.High:
                    c.renderScale = 1f; c.msaaSampleCount = 4; c.shadowDistance = 50f; break;
                case Preset.Ultra:
                    c.renderScale = 1.5f; c.msaaSampleCount = 4; c.shadowDistance = 80f; break;
            }
            clones[(int)p] = c;
        }
        if (clones[(int)p] != null) QualitySettings.renderPipeline = clones[(int)p];

        QualitySettings.vSyncCount = p == Preset.Low ? 0 : 1;
        Application.targetFrameRate = p == Preset.Low ? 60 : -1;

        foreach (var cam in Camera.allCameras)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = PostProcessing;
            data.antialiasing = p >= Preset.High ? AntialiasingMode.SubpixelMorphologicalAntiAliasing
                                                 : AntialiasingMode.None;
        }
        Changed?.Invoke();
    }
}
