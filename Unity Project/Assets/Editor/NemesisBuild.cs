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
    public static void BuildWindows()
    {
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
