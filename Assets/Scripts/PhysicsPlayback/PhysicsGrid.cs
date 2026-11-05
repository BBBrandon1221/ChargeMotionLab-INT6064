using System.Collections.Generic;
using UnityEngine;

// 背景网格 + 刻度数字。
// 范围取自 JSON 的 scenario.bounds（物理世界边界），不是可见帧的 min/max —— 用可见范围会让网格随物体运动抖动。
// 只有网格线是场景里的真实物体（LineRenderer）；刻度数字走 OnGUI，不生成任何场景物体，
// 所以不需要动 Main.unity，也不会有字体资源 / 材质变成洋红的问题。
public sealed class PhysicsGrid : MonoBehaviour
{
    public PlaybackController player;

    [Tooltip("网格间距（物理单位，米）")]
    public float cellSize = 1f;

    [Tooltip("普通网格线颜色")]
    public Color lineColor = UITheme.GridLine;

    [Tooltip("坐标轴线颜色（x=0 / y=0 那两条）")]
    public Color axisColor = UITheme.GridAxis;

    [Tooltip("线宽（物理单位）。正交视角下 0.015 约等于 2~3 像素")]
    public float lineWidth = 0.015f;

    [Tooltip("网格线的 z。相机在 -10 朝 +z 看，填 1 表示画在小球后面")]
    public float gridZ = 1f;

    [Tooltip("是否画底座上的刻度数字")]
    public bool showNumbers = true;

    public float numberFontSize = 20f;

    [Header("磁场符号")]
    [Tooltip("在网格交点画 B 场符号：⊙ = 垂直纸面向外（Bz>0），⊗ = 向里（Bz<0）")]
    public bool showFieldSymbols = true;

    [Tooltip("磁场符号字体大小")]
    public float fieldSymbolFontSize = 22f;

    [Tooltip("是否显示左下角的磁场方向图例")]
    public bool showFieldLegend = true;

    // OnGUI 统一用 1920x1080 的虚拟坐标，换算见 UIScale（与 CanvasScaler 一致）。
    const float ButtonBandTop = 970f;   // 底部控制条上沿（1080-20-76=984），数字要躲开它

    Transform _root;
    Material _material;
    GUIStyle _numberStyle;
    Vector3 _tickOrigin;                // 刻度数字的起点（网格底边）
    bool _hasTicks;
    SimulationOutput _built;            // 已经按哪份数据建过网格，避免重复重建

    Camera _camera;
    Camera Cam
    {
        get
        {
            if (_camera == null) _camera = Camera.main;
            return _camera;
        }
    }

    void Awake()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (player != null) player.Loaded += OnLoaded;

        _root = new GameObject("GridLines").transform;
        _root.SetParent(transform, false);
    }

    void OnDestroy()
    {
        if (player != null) player.Loaded -= OnLoaded;
        if (_material != null) Destroy(_material);
    }

    void Start()
    {
        // 加载可能发生在 Start 之前（本地文件走同步分支），这里补一次兜底重建。
        if (player != null && player.Loader.Output != null && player.Loader.Output != _built) Build();
    }

    void OnLoaded(SimulationOutput output)
    {
        if (output != null && output != _built) Build();
    }

    // ---------- 建网格 ----------

    void Build()
    {
        if (player == null || player.Loader.Output == null) return;

        var output = player.Loader.Output;
        _built = output;

        Clear();

        float minX, maxX, minY, maxY;
        if (!TryRange(output, out minX, out maxX, out minY, out maxY)) return;

        float step = Mathf.Max(0.05f, cellSize);

        // 竖线：沿 y 方向铺满，x 从 minX 每 step 一条
        for (float x = Mathf.Ceil(minX / step) * step; x <= maxX + 1e-3f; x += step)
            AddLine(new Vector3(x, minY, gridZ), new Vector3(x, maxY, gridZ), lineColor, lineWidth);

        // 横线：沿 x 方向铺满，y 从 minY 每 step 一条
        for (float y = Mathf.Ceil(minY / step) * step; y <= maxY + 1e-3f; y += step)
            AddLine(new Vector3(minX, y, gridZ), new Vector3(maxX, y, gridZ), lineColor, lineWidth);

        // 坐标轴单独画粗一点，一眼能看出原点和正负方向
        if (minY <= 0f && maxY >= 0f)
            AddLine(new Vector3(minX, 0f, gridZ), new Vector3(maxX, 0f, gridZ), axisColor, lineWidth * 1.8f);
        if (minX <= 0f && maxX >= 0f)
            AddLine(new Vector3(0f, minY, gridZ), new Vector3(0f, maxY, gridZ), axisColor, lineWidth * 1.8f);

        // 刻度数字贴在网格底边上。一维算例底边在 y=-1，正好在小球下方，不会压到球。
        _tickOrigin = new Vector3(0f, minY, 0f);
        _hasTicks = true;
    }

    // 优先用 bounds；旧数据没有 bounds 时退回扫帧算范围，保证不画错也不崩。
    bool TryRange(SimulationOutput output, out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = maxX = minY = maxY = 0f;

        var scenario = output.scenario;
        if (scenario != null && scenario.bounds != null &&
            scenario.bounds.min != null && scenario.bounds.min.Length >= 2 &&
            scenario.bounds.max != null && scenario.bounds.max.Length >= 2)
        {
            minX = scenario.bounds.min[0]; minY = scenario.bounds.min[1];
            maxX = scenario.bounds.max[0]; maxY = scenario.bounds.max[1];
            return maxX > minX || maxY > minY;
        }

        if (output.frames == null || output.frames.Length == 0) return false;

        bool any = false;
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        foreach (var frame in output.frames)
        {
            if (frame == null || frame.objects == null) continue;
            foreach (var body in frame.objects)
            {
                if (body == null || body.position == null || body.position.Length < 2) continue;
                any = true;
                minX = Mathf.Min(minX, body.position[0]); maxX = Mathf.Max(maxX, body.position[0]);
                minY = Mathf.Min(minY, body.position[1]); maxY = Mathf.Max(maxY, body.position[1]);
            }
        }
        return any;
    }

    void AddLine(Vector3 from, Vector3 to, Color color, float width)
    {
        var go = new GameObject("grid_line");
        go.transform.SetParent(_root, false);

        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.SetPosition(0, from);
        line.SetPosition(1, to);
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = 0;
        line.numCornerVertices = 0;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.generateLightingData = false;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        // sharedMaterial：所有网格线共用一个材质，不产生 20 多份材质实例
        line.sharedMaterial = LineMaterial();
    }

    Material LineMaterial()
    {
        if (_material != null) return _material;

        // 内置管线用 Sprites/Default；万一项目换成 URP，退到 Unlit/Color 也不会变洋红。
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");

        _material = new Material(shader);
        _material.name = "PhysicsGridLine";
        // 放不透明队列：网格在球后面（z 更大），靠深度测试让球挡住线，不会出现线盖在球上
        _material.renderQueue = 2000;
        return _material;
    }

    void Clear()
    {
        for (int i = _root.childCount - 1; i >= 0; i--)
            Destroy(_root.GetChild(i).gameObject);
    }

    // ---------- 磁场符号层（OnGUI） ----------

    GUIStyle _fieldStyle;
    GUIStyle _legendStyle;

    // Bz>0 = 垂直纸面向外 = ⊙；Bz<0 = 向里 = ⊗。旧数据没有 Bz 字段时反序列化为 0，不画符号。
    void DrawFieldLayer(Camera cam, float scale, float virtualWidth, float virtualHeight)
    {
        var scenario = _built == null ? null : _built.scenario;
        if (scenario == null) return;

        double bz = scenario.Bz;
        bool outOfPage = bz > 0.0;

        if (showFieldSymbols && System.Math.Abs(bz) > 1e-12)
        {
            if (_fieldStyle == null)
            {
                _fieldStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(fieldSymbolFontSize),
                    alignment = TextAnchor.MiddleCenter,
                };
            }
            _fieldStyle.normal.textColor = new Color(UITheme.TickText.r, UITheme.TickText.g, UITheme.TickText.b, 0.55f);

            string symbol = outOfPage ? "⊙" : "⊗";
            float step = Mathf.Max(0.05f, cellSize);
            var b = scenario.bounds;

            // 每格中心画一个符号；太密就跳格，按可见宽度抽稀
            float spanX = b.max[0] - b.min[0];
            int stride = Mathf.Max(1, Mathf.CeilToInt(spanX / step / 16f));

            int col = 0;
            for (float x = Mathf.Floor(b.min[0] / step) * step + step * 0.5f; x <= b.max[0]; x += step, col++)
            {
                if (col % stride != 0) continue;
                for (float y = Mathf.Floor(b.min[1] / step) * step + step * 0.5f; y <= b.max[1]; y += step)
                {
                    var screenPoint = cam.WorldToScreenPoint(new Vector3(x, y, 0f));
                    if (screenPoint.z <= 0f) continue;

                    float vx = screenPoint.x / scale;
                    float vy = (Screen.height - screenPoint.y) / scale;

                    if (vx < -20f || vx > virtualWidth + 20f) continue;
                    if (vy < 0f || vy > virtualHeight) continue;

                    // 底部控制条会挡符号，压到按钮区就别画
                    if (vy > ButtonBandTop) continue;

                    GUI.Label(new Rect(vx - 20f, vy - 20f, 40f, 40f), symbol, _fieldStyle);
                }
            }
        }

        if (showFieldLegend)
        {
            if (_legendStyle == null)
            {
                _legendStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(fieldSymbolFontSize),
                    alignment = TextAnchor.UpperLeft,
                };
            }
            _legendStyle.normal.textColor = UITheme.TickText;

            // 左下角图例：写清这套符号的意思。Bz=0 或缺失时不画。
            if (System.Math.Abs(bz) > 1e-12)
            {
                string legend = UILang.F(bz > 0 ? "field.legend.out" : "field.legend.in",
                                         System.Math.Abs(bz).ToString("0.##"));
                GUI.Label(new Rect(24f, virtualHeight - 40f - fieldSymbolFontSize, 560f, fieldSymbolFontSize + 12f),
                          legend, _legendStyle);
            }
        }
    }

    // ---------- 刻度数字 ----------

    void OnGUI()
    {
        if (!showNumbers && !showFieldSymbols && !showFieldLegend) return;
        var cam = Cam;
        if (cam == null) return;

        var virtualSize = UIScale.BeginGUI();
        float virtualWidth = virtualSize.x;
        float virtualHeight = virtualSize.y;
        float scale = UIScale.Factor;

        if (showFieldSymbols || showFieldLegend) DrawFieldLayer(cam, scale, virtualWidth, virtualHeight);

        if (!showNumbers || !_hasTicks) return;

        if (_numberStyle == null)
        {
            _numberStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(numberFontSize),
                alignment = TextAnchor.UpperCenter,
            };
        }
        _numberStyle.normal.textColor = UITheme.TickText;

        float step = Mathf.Max(0.05f, cellSize);

        // 范围过大时刻度会挤成一团，按可见宽度抽稀
        float spanX = 20f;
        if (_built != null && _built.scenario != null && _built.scenario.bounds != null)
        {
            var b = _built.scenario.bounds;
            if (b.min != null && b.max != null && b.min.Length >= 1 && b.max.Length >= 1)
                spanX = Mathf.Max(0.01f, b.max[0] - b.min[0]);
        }
        int stride = Mathf.Max(1, Mathf.CeilToInt(spanX / step / 14f));

        var scenario = _built == null ? null : _built.scenario;
        float minX = 0f, maxX = 0f;
        if (scenario != null && scenario.bounds != null &&
            scenario.bounds.min != null && scenario.bounds.min.Length >= 1 &&
            scenario.bounds.max != null && scenario.bounds.max.Length >= 1)
        {
            minX = scenario.bounds.min[0];
            maxX = scenario.bounds.max[0];
        }

        // 刻度数字的基准线。相机是按轨迹范围取景的，区域底边不保证在画面里：
        // 轨迹集中在上半部时底边会被裁到屏幕外，整排数字就被「超出虚拟高度」这条判断丢掉。
        // 所以基准线取「区域底边」和「可见底边」里更靠上的那个。
        float tickY = _tickOrigin.y;
        if (cam.orthographic)
        {
            float visibleBottom = cam.transform.position.y - cam.orthographicSize;
            tickY = Mathf.Max(tickY, visibleBottom + cam.orthographicSize * 0.03f);
        }

        int index = 0;
        for (float x = Mathf.Ceil(minX / step) * step; x <= maxX + 1e-3f; x += step, index++)
        {
            if (index % stride != 0) continue;

            var screenPoint = cam.WorldToScreenPoint(new Vector3(x, tickY, 0f));
            if (screenPoint.z <= 0f) continue;

            float vx = screenPoint.x / scale;
            float vy = (Screen.height - screenPoint.y) / scale + 4f;

            if (vx < -20f || vx > virtualWidth + 20f) continue;
            if (vy < 0f || vy > virtualHeight) continue;

            // 底部那排按钮会挡住数字，压到按钮区就把数字顶到按钮上方（横向对齐不受影响，仍能读数）
            if (vy > ButtonBandTop) vy = ButtonBandTop - 36f;

            GUI.Label(new Rect(vx - 40f, vy, 80f, 26f), x.ToString("0.##"), _numberStyle);
        }
    }
}
