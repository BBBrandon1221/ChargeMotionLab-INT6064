using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// 只读审计：把当前 Main 场景里 Canvas 下所有 RectTransform 的真实值 dump 出来。
// 用途：改布局前先拿到"现状数字"，避免凭编辑器脚本推测。
// batchmode 用法：
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod UIAudit.Run -logFile ui_audit_log.txt
public static class UIAudit
{
    const string ScenePath = "Assets/Scenes/Main.unity";

    // batchmode 下进程工作目录不一定是项目根，落盘路径按 dataPath 推导。
    static string OutPath
    {
        get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ui_audit.txt"); }
    }

    [MenuItem("Tools/90 UI Audit (read only)")]
    public static void Run()
    {
        var sb = new StringBuilder();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        sb.AppendLine("scene = " + scene.path);
        sb.AppendLine("screen (game view 未必等于此值) = " + Screen.width + " x " + Screen.height);

        var canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            sb.AppendLine("!! 找不到 Canvas");
            Write(sb);
            return;
        }

        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            sb.AppendLine($"CanvasScaler: mode={scaler.uiScaleMode} ref={scaler.referenceResolution} match={scaler.matchWidthOrHeight}");
        }
        sb.AppendLine("Canvas renderMode=" + canvas.renderMode + " sortingOrder=" + canvas.sortingOrder);
        sb.AppendLine();

        sb.AppendLine("--- Canvas 子树（按 sibling 顺序）---");
        Dump(canvas.transform, 0, sb);
        sb.AppendLine();

        sb.AppendLine("--- 场景根对象 ---");
        foreach (var go in scene.GetRootGameObjects())
        {
            sb.AppendLine("  " + go.name + "  active=" + go.activeSelf + "  components=" + ComponentList(go));
        }
        sb.AppendLine();

        sb.AppendLine("--- 运行时自动挂载的显示层脚本（由 PhysicsOverlayBootstrap 创建，不在场景里）---");
        sb.AppendLine("  PhysicsGrid / SpeedLabels / ParameterPanel / AIChatPanel 均无场景实例（应为 0）");
        sb.AppendLine("  PhysicsGrid 在场景里的实例数 = " + Object.FindObjectsOfType<PhysicsGrid>().Length);
        sb.AppendLine("  ParameterPanel 在场景里的实例数 = " + Object.FindObjectsOfType<ParameterPanel>().Length);
        sb.AppendLine("  SpeedLabels 在场景里的实例数 = " + Object.FindObjectsOfType<SpeedLabels>().Length);

        Write(sb);
    }

    static string ComponentList(GameObject go)
    {
        var names = new StringBuilder();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) { names.Append("<missing> "); continue; }
            names.Append(c.GetType().Name).Append(' ');
        }
        return names.ToString().Trim();
    }

    static void Dump(Transform t, int depth, StringBuilder sb)
    {
        var rt = t as RectTransform;
        var line = new StringBuilder();
        line.Append(new string(' ', depth * 2)).Append(t.name);

        if (rt != null)
        {
            line.Append($"  pos=({rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0})")
                .Append($" size=({rt.sizeDelta.x:F0},{rt.sizeDelta.y:F0})")
                .Append($" anchor=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2})->({rt.anchorMax.x:F2},{rt.anchorMax.y:F2})")
                .Append($" pivot=({rt.pivot.x:F2},{rt.pivot.y:F2})");
        }

        var txt = t.GetComponent<Text>();
        if (txt != null)
        {
            line.Append($"  [Text] fontSize={txt.fontSize} color=({txt.color.r:F2},{txt.color.g:F2},{txt.color.b:F2},{txt.color.a:F2})")
                .Append($" align={txt.alignment} text=\"{txt.text}\"");
        }

        var img = t.GetComponent<Image>();
        if (img != null)
        {
            line.Append($"  [Image] color=({img.color.r:F2},{img.color.g:F2},{img.color.b:F2},{img.color.a:F2}) sprite={(img.sprite == null ? "null" : img.sprite.name)}");
        }

        var btn = t.GetComponent<Button>();
        if (btn != null) line.Append("  [Button]");

        sb.AppendLine(line.ToString());

        for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, sb);
    }

    static void Write(StringBuilder sb)
    {
        try
        {
            File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(true));
            Debug.Log("[UIAudit] 已写入 " + Path.GetFullPath(OutPath));
        }
        catch (System.Exception e)
        {
            Debug.LogError("[UIAudit] 写文件失败: " + e.Message);
        }
        Debug.Log(sb.ToString());
    }
}
