using UnityEngine;

// 每颗球上方的实时速度标签。
// 数据来自当前帧的 frames[].objects[].velocity，不另算、不缓存，所以拖进度条、单步、变速都跟着变。
// 用 OnGUI 画在球附近，而不是挂 TextMesh：不用字体资源、不用管朝向、也不会出现 TextMesh 材质变洋红。
public sealed class SpeedLabels : MonoBehaviour
{
    public PlaybackController player;

    public float fontSize = 22f;

    [Tooltip("标签与球边的间距（像素，按 1080p 基准）")]
    public float gap = 10f;

    [Tooltip("勾选后显示速度的 x 分量（一维实验看它最直观）；不勾则显示速度大小")]
    public bool showSignedX = true;

    [Tooltip("避开左上数据卡与右上参数面板：球滚到那两块底下时，标签自动翻到另一侧")]
    public bool avoidPanels = true;

    const float LabelWidth = 220f;

    // 禁区尺寸必须跟 PlaybackSceneBuilder / ParameterPanel 保持一致
    const float HudLeft = 24f, HudTop = 24f, HudWidth = 404f, HudHeight = 268f;
    const float ParamRight = 30f, ParamTop = 30f, ParamWidth = 520f, ParamHeight = 340f;
    const float BarHeight = 100f;

    GUIStyle _style;
    Camera _camera;
    AIChatPanel _chat;

    void Awake()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
    }

    // 不能放在 Awake 里缓存：脚本是 PhysicsOverlayBootstrap 运行时逐个挂上去的，
    // 挂 SpeedLabels 的时候 AIChatPanel 还没加到场景里，会永远是 null。
    AIChatPanel Chat
    {
        get
        {
            if (_chat == null) _chat = FindObjectOfType<AIChatPanel>();
            return _chat;
        }
    }

    Camera Cam
    {
        get
        {
            if (_camera == null) _camera = Camera.main;
            return _camera;
        }
    }

    void OnGUI()
    {
        if (player == null || player.Loader.Output == null) return;

        var cam = Cam;
        if (cam == null) return;

        var output = player.Loader.Output;
        var frames = output.frames;
        if (frames == null || frames.Length == 0) return;

        var frame = frames[Mathf.Clamp(player.CurrentFrame, 0, frames.Length - 1)];
        if (frame == null || frame.objects == null) return;

        var virtualSize = UIScale.BeginGUI();
        float virtualWidth = virtualSize.x;
        float virtualHeight = virtualSize.y;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(fontSize),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
        }

        // 正交相机下，1 米在虚拟坐标里占多少像素：与分辨率无关，窗口缩放也不会变形。
        float pixelsPerMeter = virtualHeight / (2f * Mathf.Max(0.01f, cam.orthographicSize));

        var hudZone = new Rect(HudLeft, HudTop, HudWidth, HudHeight);
        var paramZone = new Rect(virtualWidth - ParamRight - ParamWidth, ParamTop, ParamWidth, ParamHeight);
        var barZone = new Rect(0f, virtualHeight - BarHeight, virtualWidth, BarHeight);

        // AI 面板展开时会盖住右下大片区域，标签浮在它上面会像"文字穿透"。
        // 面板位置可拖动，所以每帧问它要当前矩形；没展开就是空矩形，不影响。
        var chat = Chat;
        var aiZone = chat != null ? chat.OccupiedRect : new Rect();

        float labelHeight = fontSize + 8f;
        float factor = UIScale.Factor;

        for (int i = 0; i < frame.objects.Length; i++)
        {
            var body = frame.objects[i];
            if (body == null || body.velocity == null || body.velocity.Length < 2) continue;
            if (!player.Targets.TryGetValue(body.id, out var target) || target == null) continue;

            var screenPoint = cam.WorldToScreenPoint(target.position);
            if (screenPoint.z <= 0f) continue;

            float vx = screenPoint.x / factor;
            float vy = (Screen.height - screenPoint.y) / factor;

            // 球在屏幕上的半径：localScale 是直径（原始球直径 1），所以半径 = scale.x / 2
            float radiusPx = target.lossyScale.x * 0.5f * pixelsPerMeter;

            // 一维实验两颗球的 y 完全相同，标签只靠 x 分开会重叠，
            // 所以按序号上下交替：偶数在上、奇数在下，永不互相遮挡。
            float above = vy - radiusPx - gap - labelHeight;
            float below = vy + radiusPx + gap;
            float labelY = (i % 2 == 0) ? above : below;

            var rect = new Rect(vx - LabelWidth * 0.5f, labelY, LabelWidth, labelHeight);

            if (avoidPanels)
            {
                if (Blocked(rect, hudZone, paramZone, barZone, aiZone))
                {
                    // 先试另一侧
                    float alternate = Mathf.Approximately(labelY, above) ? below : above;
                    var alternateRect = new Rect(rect.x, alternate, LabelWidth, labelHeight);
                    if (!Blocked(alternateRect, hudZone, paramZone, barZone, aiZone))
                    {
                        rect = alternateRect;
                    }
                    else
                    {
                        // 两侧都被挡（球正好滚到卡片后面）：横向挪到两块卡片之间的空档
                        float safeLeft = hudZone.xMax + 8f;
                        float safeRight = paramZone.x - 8f - LabelWidth;
                        // AI 面板也占着右侧一大块，不把它算进来就会挪进面板里
                        if (aiZone.width > 0f)
                            safeRight = Mathf.Min(safeRight, aiZone.x - 8f - LabelWidth);
                        rect.x = Mathf.Clamp(rect.x, safeLeft, Mathf.Max(safeLeft, safeRight));
                    }
                }
            }

            // 永不出屏
            rect.x = Mathf.Clamp(rect.x, 4f, Mathf.Max(4f, virtualWidth - LabelWidth - 4f));
            rect.y = Mathf.Clamp(rect.y, 4f, Mathf.Max(4f, virtualHeight - labelHeight - 4f));

            // 兜底：挪完还是压在 AI 面板上（面板正好在球上方），宁可不显示也不要文字穿透
            if (avoidPanels && aiZone.width > 0f && rect.Overlaps(aiZone)) continue;

            string text = FormatVelocity(body.velocity[0], body.velocity[1], output);
            _style.normal.textColor = ColorFor(body.velocity[0], body.velocity[1]);

            GUI.Label(rect, text, _style);
        }
    }

    static bool Blocked(Rect rect, Rect a, Rect b, Rect c, Rect d)
    {
        if (d.width > 0f && d.height > 0f && rect.Overlaps(d)) return true;
        return rect.Overlaps(a) || rect.Overlaps(b) || rect.Overlaps(c);
    }

    string FormatVelocity(float vx, float vy, SimulationOutput output)
    {
        bool oneDimensional = output.scenario != null && output.scenario.mode == "1d";

        // 一维实验里速度只有 x 分量，带正负号比速度大小更能说明"碰后反向"
        if (oneDimensional || (showSignedX && Mathf.Abs(vy) < 1e-4f))
            return "v = " + vx.ToString("0.00") + " m/s";

        float speed = Mathf.Sqrt(vx * vx + vy * vy);
        return "|v| = " + speed.ToString("0.00") + " m/s";
    }

    // 颜色只用来区分方向：向右/向上偏暖，向左/向下偏冷，静止灰。
    static Color ColorFor(float vx, float vy)
    {
        float speed = Mathf.Sqrt(vx * vx + vy * vy);
        if (speed < 0.01f) return new Color(0.35f, 0.37f, 0.42f, 1f);
        return vx >= 0f
            ? new Color(0.66f, 0.26f, 0.13f, 1f)
            : new Color(0.10f, 0.32f, 0.62f, 1f);
    }
}
