using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line build used by the evaluation scripts:
///   Unity.exe -batchmode -quit -projectPath "Unity Project" -executeMethod NemesisBuild.BuildWindows
/// Output: Unity Project/Builds/Windows/Nemesis.exe (gitignored)
/// </summary>
public static class NemesisBuild
{
    /// Materials the runtime effects load from Resources. Saved as assets so the build keeps
    /// their shader variants (transparent additive particles, emissive Lit), which Unity would
    /// otherwise strip because no scene material uses them.
    [MenuItem("Nemesis/Create FX Materials")]
    public static void CreateFxAssets()
    {
        const string dir = "Assets/Resources/NemesisFX";
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/Resources", "NemesisFX");

        var add = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        add.SetFloat("_Surface", 1f);          // transparent
        add.SetFloat("_Blend", 2f);            // additive
        add.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        add.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        add.SetFloat("_ZWrite", 0f);
        add.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        add.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        add.SetColor("_BaseColor", Color.white);
        Save(add, dir + "/FX_Additive.mat");

        var lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        lit.EnableKeyword("_EMISSION");
        lit.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        lit.SetColor("_EmissionColor", Color.black);
        lit.SetFloat("_Smoothness", 0.4f);
        Save(lit, dir + "/FX_EmissiveLit.mat");
        AssetDatabase.SaveAssets();
    }

    static void Save(Material m, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { EditorUtility.CopySerialized(m, existing); EditorUtility.SetDirty(existing); }
        else AssetDatabase.CreateAsset(m, path);
    }

    /// Browser build: Unity Project/Builds/WebGL (gitignored). Gzip with a decompression fallback,
    /// so it can be served by any static host (GitHub Pages, itch.io) without special headers.
    public static void BuildWebGL()
    {
        CreateFxAssets();
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.memorySize = 512;
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/CombatScene.unity" },
            locationPathName = "Builds/WebGL",
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[NemesisBuild] WebGL {report.summary.result} in {report.summary.totalTime}, " +
                  $"{report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB");
        if (report.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    public static void BuildWindows()
    {
        CreateFxAssets();
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/CombatScene.unity" },
            locationPathName = "Builds/Windows/Nemesis.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[NemesisBuild] {report.summary.result} in {report.summary.totalTime}, " +
                  $"{report.summary.totalErrors} errors");
        if (report.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
