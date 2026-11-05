using System;
using System.IO;
using System.Text;
using UnityEngine;

// AI 配置：key 从哪来、用哪个模型、请求发到哪。
//
// 查找顺序（先命中先用）：
//   1. 工程根目录 / exe 同目录 的 ai_config.json   ← 开发期改这个，不进仓库
//   2. StreamingAssets/ai_config.json              ← 随包分发，组长拿到即用
//   3. PlayerPrefs                                 ← 面板里粘贴保存，只在本机生效
//
// 任何一级读失败都只跳过、不抛异常：AI 挂了不能影响实验回放。
[Serializable]
public class AIConfigData
{
    public string api_key = string.Empty;
    public string model = AIConfig.DefaultModel;
    public string base_url = AIConfig.DefaultBaseUrl;
    public float temperature = 1f;
    public int max_tokens = 600;

    // deepseek-v4-flash 默认开着思考模式，会把 max_tokens 全烧在 reasoning_content 上，
    // 导致 content 返回空串。请求体里必须显式写 disabled，所以这里也留成配置项。
    public string thinking = "disabled";

    public bool ThinkingEnabled
    {
        get { return !string.IsNullOrEmpty(thinking) && thinking.Trim().ToLowerInvariant() == "enabled"; }
    }
}

public static class AIConfig
{
    public const string DefaultModel = "deepseek-v4-flash";
    public const string DefaultBaseUrl = "https://api.deepseek.com/chat/completions";
    public const string PlayerPrefsKey = "momentum_ai_config";

    // 配置从哪个文件读出来的，显示在面板上方便排查"为什么改了文件没生效"
    public static string LastSource { get; private set; }

    public static AIConfigData Load()
    {
        LastSource = string.Empty;

        foreach (var path in CandidateFiles())
        {
            var fromFile = TryReadFile(path);
            if (fromFile != null)
            {
                LastSource = path;
                return Normalize(fromFile);
            }
        }

        var fromPrefs = TryReadPrefs();
        if (fromPrefs != null)
        {
            LastSource = "PlayerPrefs（面板内保存）";
            return Normalize(fromPrefs);
        }

        return null;
    }

    // 面板里粘贴 key 后调用：只存本机，不落盘到仓库。
    public static AIConfigData SaveToPlayerPrefs(string apiKey, string model)
    {
        var data = new AIConfigData
        {
            api_key = (apiKey ?? string.Empty).Trim(),
            model = string.IsNullOrEmpty(model) ? DefaultModel : model.Trim(),
        };
        PlayerPrefs.SetString(PlayerPrefsKey, JsonUtility.ToJson(data));
        PlayerPrefs.Save();
        LastSource = "PlayerPrefs（面板内保存）";
        return Normalize(data);
    }

    public static bool HasUsableKey(AIConfigData data)
    {
        if (data == null) return false;
        var key = (data.api_key ?? string.Empty).Trim();
        return key.Length > 0 && key != "REPLACE_ME" && key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase);
    }

    // 面板上显示用：只留前 6 位和后 4 位，避免录屏 / 截图泄露。
    public static string MaskKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return "(空)";
        var key = apiKey.Trim();
        if (key.Length <= 12) return "sk-…";
        return key.Substring(0, 6) + "…" + key.Substring(key.Length - 4);
    }

    static string[] CandidateFiles()
    {
        var list = new string[2];

        // Application.dataPath：编辑器里是 <项目>/Assets，打包后是 <exe 目录>/<产品名>_Data。
        // 两种情况取上一级，正好都是"工程根目录 / exe 同目录"。
        try
        {
            var parent = Directory.GetParent(Application.dataPath);
            list[0] = parent != null ? Path.Combine(parent.FullName, "ai_config.json") : null;
        }
        catch (Exception)
        {
            list[0] = null;
        }

        try { list[1] = Path.Combine(Application.streamingAssetsPath, "ai_config.json"); }
        catch (Exception) { list[1] = null; }

        return list;
    }

    static AIConfigData TryReadFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text)) return null;

            var data = JsonUtility.FromJson<AIConfigData>(text);
            if (data == null) return null;
            return HasUsableKey(data) ? data : null;
        }
        catch (Exception error)
        {
            Debug.LogWarning("[AIConfig] 读取失败，跳过 " + path + "：" + error.Message);
            return null;
        }
    }

    static AIConfigData TryReadPrefs()
    {
        if (!PlayerPrefs.HasKey(PlayerPrefsKey)) return null;

        try
        {
            var data = JsonUtility.FromJson<AIConfigData>(PlayerPrefs.GetString(PlayerPrefsKey));
            return HasUsableKey(data) ? data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // JSON 里缺字段时保留默认值；写了离谱值也在这里夹回可用范围。
    static AIConfigData Normalize(AIConfigData data)
    {
        if (data == null) return null;

        data.api_key = (data.api_key ?? string.Empty).Trim();
        data.model = string.IsNullOrWhiteSpace(data.model) ? DefaultModel : data.model.Trim();
        data.base_url = string.IsNullOrWhiteSpace(data.base_url) ? DefaultBaseUrl : data.base_url.Trim();
        data.temperature = Mathf.Clamp(data.temperature, 0f, 2f);
        data.max_tokens = Mathf.Clamp(data.max_tokens, 64, 2000);
        data.thinking = string.IsNullOrWhiteSpace(data.thinking) ? "disabled" : data.thinking.Trim();
        return data;
    }

    // 打包检查用：把当前解析结果打一行日志，方便确认读的是哪个文件。
    public static string Describe(AIConfigData data)
    {
        if (data == null) return "未找到任何 ai_config.json";
        var sb = new StringBuilder();
        sb.Append("key=").Append(MaskKey(data.api_key));
        sb.Append("  model=").Append(data.model);
        sb.Append("  base_url=").Append(data.base_url);
        sb.Append("  thinking=").Append(data.thinking);
        if (!string.IsNullOrEmpty(LastSource)) sb.Append("  ← ").Append(LastSource);
        return sb.ToString();
    }
}
