using UnityEngine;

// 统一配色。改颜色只改这里，不要在各面板里各写一套。
// 取色原则：浅底 + 深字 + 一个强调色，投影仪偏色时也还能看清。
public static class UITheme
{
    // ---- 画布 ----
    public static readonly Color CanvasBg = Hex(0xEDF0F5);
    public static readonly Color GridLine = Hex(0xD9DEE7);
    public static readonly Color GridAxis = Hex(0x9AA5B5);
    public static readonly Color TickText = Hex(0x697384);

    // ---- 卡片 ----
    public static readonly Color CardBg = new Color(1f, 1f, 1f, 0.95f);
    public static readonly Color CardBorder = Hex(0xD3DAE4);
    public static readonly Color CardShadow = new Color(0.10f, 0.14f, 0.20f, 0.07f);

    // ---- 文字 ----
    public static readonly Color TextPrimary = Hex(0x1A1F2B);
    public static readonly Color TextMuted = Hex(0x5C6678);
    public static readonly Color TextOnDark = Hex(0xF4F7FB);

    // ---- 控件 ----
    public static readonly Color ButtonBg = Hex(0x24334A);
    public static readonly Color ButtonBgIdle = Hex(0x33445C);
    public static readonly Color Accent = Hex(0xE08A2E);
    public static readonly Color Divider = Hex(0xCFD6E0);

    // ---- 状态色 ----
    public static readonly Color Good = Hex(0x1F8A56);
    public static readonly Color Warn = Hex(0xB4761B);
    public static readonly Color Bad = Hex(0xB03A3A);

    // ---- 球体 ----
    // 比"纯饱和色"低一档：Standard 材质在方向光下会把高饱和色打成过曝的荧光色
    public static readonly Color Ball1 = new Color(0.16f, 0.45f, 0.88f);
    public static readonly Color Ball2 = new Color(0.92f, 0.45f, 0.12f);
    public static readonly Color Ball3 = new Color(0.20f, 0.62f, 0.30f);

    static Color Hex(int rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1f);
    }
}
