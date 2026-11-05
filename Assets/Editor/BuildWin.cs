using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// batchmode 构建 Windows 版，产物进 _build/Win64（与之前组长那次构建同目录）。
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod BuildWin.Build -logFile build_log.txt
// 注意：环境里不能同时存在 http_proxy 和 HTTP_PROXY（Unity 的 Bee 会因 Hashtable 键大小写冲突崩掉）。
public static class BuildWin
{
    const string ScenePath = "Assets/Scenes/Main.unity";

    // 一次跑完：重建场景 → 打包。省去两次启动 Unity（每次 40 秒起）。
    [MenuItem("Tools/81 Rebuild Scene + Build Win64")]
    public static void RebuildAndBuild()
    {
        PlaybackSceneBuilder.BuildScene();
        Build();
    }

    [MenuItem("Tools/80 Build Windows (Win64)")]
    public static void Build()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string outDir = Path.Combine(Directory.GetParent(projectRoot).FullName, "_build", "Win64");
        Directory.CreateDirectory(outDir);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = Path.Combine(outDir, "ChargeMotionLab.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        Debug.Log($"[BuildWin] result={summary.result} size={summary.totalSize / (1024 * 1024)}MB " +
                  $"errors={summary.totalErrors} output={summary.outputPath}");

        if (summary.result == BuildResult.Succeeded) Debug.Log("[BuildWin] 构建成功");
        else Debug.LogError("[BuildWin] 构建失败: " + summary.result);
    }
}
