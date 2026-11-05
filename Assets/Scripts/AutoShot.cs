using System.Collections;
using System.IO;
using UnityEngine;

// 只用于自动截图验证：带命令行参数启动时，等画面稳定后截一张图然后退出。
//
//   MomentumLab.exe -autoshot "D:/shot.png" -shotdelay 4 -lang en
//
// 没有 -autoshot 参数时完全不介入，正常手动玩。
// -lang 只影响这一次启动，用来出中英对照截图，不会写回 PlayerPrefs。
public sealed class AutoShot : MonoBehaviour
{
    static string _path;
    static float _delay = 4f;
    static bool _openAi;
    static string _dataset;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        var args = System.Environment.GetCommandLineArgs();
        // 注意：-shotai 这类无值开关可以被放在最后一个，循环必须扫到 args.Length-1；
        // 原来写 i < args.Length - 1，放在末尾的 -shotai 永远读不到，面板就不弹。
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-autoshot" && i + 1 < args.Length) _path = args[++i];
            else if (args[i] == "-shotdelay" && i + 1 < args.Length) float.TryParse(args[++i], out _delay);
            else if (args[i] == "-lang" && i + 1 < args.Length) ApplyLang(args[++i]);
            else if (args[i] == "-shotai") _openAi = true;
            else if (args[i] == "-dataset" && i + 1 < args.Length) _dataset = args[++i];
        }

        if (string.IsNullOrEmpty(_path)) return;

        var go = new GameObject("AutoShot");
        DontDestroyOnLoad(go);
        go.AddComponent<AutoShot>();
    }

    static void ApplyLang(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        bool english = value.StartsWith("en", System.StringComparison.OrdinalIgnoreCase);
        bool chinese = value.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase) || value.StartsWith("cn", System.StringComparison.OrdinalIgnoreCase);
        if (!english && !chinese) return;

        UILang.Set(english ? UILangId.En : UILangId.Zh, false);
    }

    IEnumerator Start()
    {
        // 切算例要在 SceneBinder 自己的 Start 加载之前接管，等一帧再 LoadFile 会闪一下默认算例
        if (!string.IsNullOrEmpty(_dataset))
        {
            yield return null;
            var binder = Object.FindObjectOfType<SceneBinder>();
            if (binder != null) binder.LoadFile(_dataset);
        }

        if (_openAi)
        {
            var panel = Object.FindObjectOfType<AIChatPanel>();
            Debug.Log($"[AutoShot] -shotai: AIChatPanel = {(panel != null ? "找到" : "!! 没找到")}");
            if (panel != null)
            {
                panel.OpenPanel();
                Debug.Log($"[AutoShot] OpenPanel 已调用，IsOpen = {panel.IsOpen}");
            }
        }

        // 等 load + 首帧 + 可能的自动播放起步，避免截到空白画面
        yield return new WaitForSeconds(_delay);

        ScreenCapture.CaptureScreenshot(_path);
        Debug.Log("[AutoShot] 已截图 " + _path);

        // CaptureScreenshot 是异步落盘，退出前给足时间
        yield return new WaitForSeconds(1.5f);

        if (File.Exists(_path)) Debug.Log("[AutoShot] 文件已生成");
        else Debug.LogWarning("[AutoShot] 文件未生成: " + _path);

        Application.Quit();
    }
}
