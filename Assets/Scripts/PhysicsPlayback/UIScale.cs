using UnityEngine;

// OnGUI 层（网格刻度 / 球速标签 / 参数面板 / AI 面板）统一的虚拟坐标换算。
//
// 之前四个脚本各自写 `float scale = Screen.height / 1080f;`，
// 而 CanvasScaler 用的是 match=0.5 的几何平均。16:9 时两者恰好相等，
// 一旦窗口不是 16:9（比如 4:3 的 640x480、或者被拉窄的游戏视图），
// UGUI 的 HUD 和 OnGUI 的参数面板就会出现在不同的坐标系里，看起来像没对齐。
// 这里改成和 CanvasScaler 完全相同的算法，两套层永远重合。
public static class UIScale
{
    public const float RefW = 1920f;
    public const float RefH = 1080f;

    public static float Factor
    {
        get
        {
            float w = Mathf.Max(1f, Screen.width);
            float h = Mathf.Max(1f, Screen.height);
            return Mathf.Sqrt((w / RefW) * (h / RefH));
        }
    }

    public static Vector2 VirtualSize
    {
        get
        {
            float f = Factor;
            return new Vector2(Mathf.Max(1f, Screen.width) / f, Mathf.Max(1f, Screen.height) / f);
        }
    }

    // 设好 GUI.matrix 后返回虚拟画布尺寸，调用方直接拿去布局。
    public static Vector2 BeginGUI()
    {
        float f = Factor;
        GUI.matrix = Matrix4x4.Scale(new Vector3(f, f, 1f));
        return new Vector2(Mathf.Max(1f, Screen.width) / f, Mathf.Max(1f, Screen.height) / f);
    }

    public static void Fill(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    // 1px 描边：四条边各画一条，比纹理便宜，也不依赖 GUI.skin.box。
    public static void Outline(Rect rect, Color color, float width = 1f)
    {
        Fill(new Rect(rect.x, rect.y, rect.width, width), color);
        Fill(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
        Fill(new Rect(rect.x, rect.y, width, rect.height), color);
        Fill(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
    }

    // 卡片：阴影 + 白底 + 1px 边框。三个面板共用，保证视觉一致。
    public static void Card(Rect rect, Color? fill = null)
    {
        Fill(new Rect(rect.x, rect.y + 3f, rect.width, rect.height), UITheme.CardShadow);
        Fill(rect, fill ?? UITheme.CardBg);
        Outline(rect, UITheme.CardBorder);
    }
}
