using System;
using System.IO;
using UnityEngine;

// 把「填的文件名」解析成各平台都能读取的形式，供 File.ReadAllText 或 UnityWebRequest 使用。
// 规则（按优先级）：
//   1. 已带协议（http:// https:// file: jar:）→ 原样返回
//   2. 绝对路径且文件存在 → 转成 file:// URI
//   3. 其余视为相对路径 → 拼到 StreamingAssets 后面
// 所以在 Inspector 里只填 "SimData/elastic.json" 即可，换平台、换机器都不用改代码。
public static class SimulationFileResolver
{
    // WebGL / Android 的 StreamingAssets 在压缩包或 APK 内，File IO 读不到，必须走 UnityWebRequest。
    public static bool RequiresWebRequest(string path)
    {
        var url = ToUrl(path);
        if (url.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
#if UNITY_WEBGL || UNITY_ANDROID
        return url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    public static string ToUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Simulation path is empty", nameof(path));

        path = path.Trim().Replace('\\', '/');

        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Path.IsPathRooted(path) && File.Exists(path))
            return new Uri(path).AbsoluteUri;

        return (Application.streamingAssetsPath + "/" + path.TrimStart('/')).Replace("\\", "/");
    }

    // 交给 File.ReadAllText 用的系统路径；只在 RequiresWebRequest 为 false 时调用。
    public static string ToLocalPath(string path)
    {
        var url = ToUrl(path);
        return url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(url).LocalPath
            : url.Replace('/', Path.DirectorySeparatorChar);
    }
}
