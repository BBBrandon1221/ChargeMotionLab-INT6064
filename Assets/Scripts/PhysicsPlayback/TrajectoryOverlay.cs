using System.Collections.Generic;
using UnityEngine;

// 轨迹线 + 理论圆周参考圈。
//
// 轨迹线：从帧数据抽样画出每个电荷已经走过的路径 —— 演示"圆周运动"最直接的一张图。
// 参考圈：按 r = m|v|/(|q|B) 和圆心 = 起点 + (m/(qB))·(vy, -vx) 解析画出理论轨迹，
//         学生把 q 或 B 改了之后重算，参考圈跟着缩放，正好验证公式。
//
// 全部用 LineRenderer 运行时生成，不动场景文件；换数据集时整组重建。
public sealed class TrajectoryOverlay : MonoBehaviour
{
    public PlaybackController player;

    [Tooltip("轨迹线抽稀：每 N 帧取一个点。631 帧全画会超出 LineRenderer 上限")]
    public int trailStride = 4;

    [Tooltip("轨迹线宽（物理单位，米）")]
    public float trailWidth = 0.035f;

    [Tooltip("参考圈线宽（物理单位，米）")]
    public float circleWidth = 0.05f;

    [Tooltip("参考圈分段数")]
    public int circleSegments = 96;

    [Tooltip("是否显示理论参考圈")]
    public bool showGuides = true;

    [Tooltip("轨迹与参考圈的 z。相机在 -10 朝 +z 看，填 0.5 表示画在球后、网格前")]
    public float lineZ = 0.5f;

    Transform _trailRoot;
    Transform _guideRoot;
    Material _trailMaterial;
    Material _guideMaterial;
    SimulationOutput _built;

    // 与 SceneBinder.PositiveColors / NegativeColors 一致的取色（跨脚本复制一份常量，
    // 避免运行时依赖初始化顺序；改颜色两处要同步）。
    static readonly Color[] PositiveColors =
    {
        new Color(0.92f, 0.45f, 0.12f),
        new Color(0.96f, 0.80f, 0.20f),
        new Color(0.85f, 0.30f, 0.25f),
    };
    static readonly Color[] NegativeColors =
    {
        new Color(0.16f, 0.45f, 0.88f),
        new Color(0.20f, 0.65f, 0.75f),
        new Color(0.45f, 0.40f, 0.85f),
    };

    void Awake()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (player != null) player.Loaded += OnLoaded;

        _trailRoot = new GameObject("Trails").transform;
        _trailRoot.SetParent(transform, false);
        _guideRoot = new GameObject("Guides").transform;
        _guideRoot.SetParent(transform, false);

        // LineRenderer 的 sharedMaterial 不创建就是 null，渲染出来是洋红（缺材质警示色）。
        // 与 PhysicsGrid 同一套：内置管线用 Sprites/Default，逐级兜底。
        _trailMaterial = MakeLineMaterial("ChargeTrailLine");
        _guideMaterial = MakeLineMaterial("ChargeGuideLine");
    }

    static Material MakeLineMaterial(string name)
    {
        // 优先走 Resources 里自带的 ChargeLab/LineColor：它按顶点色出图，
        // startColor/endColor 才生效。Sprites/Default 在没引用任何 Sprite 的工程里
        // 会被打包剥离，运行时兜底到 Unlit/Color —— 那个不读顶点色，线全是白的，
        // 在灰白背景上等于没画。
        var shader = Resources.Load<Shader>("Shaders/LineColor");
        if (shader == null) shader = Shader.Find("ChargeLab/LineColor");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");

        var material = new Material(shader);
        material.name = name;
        material.renderQueue = 3000;   // 透明队列；ZWrite 关掉，靠深度测试让球挡住线
        return material;
    }

    void OnDestroy()
    {
        if (player != null) player.Loaded -= OnLoaded;
        if (_trailMaterial != null) Destroy(_trailMaterial);
        if (_guideMaterial != null) Destroy(_guideMaterial);
    }

    // 大厅（第一人称）模式下要整组藏起来：轨迹和参考圈画在物理平面上，
    // 而物理平面正好穿过大厅，不藏的话大厅半空中会飘着两个橙色大圈。
    public void SetVisible(bool visible)
    {
        if (_trailRoot != null) _trailRoot.gameObject.SetActive(visible);
        if (_guideRoot != null) _guideRoot.gameObject.SetActive(visible);
    }

    void OnLoaded(SimulationOutput output)
    {
        if (output != null && output != _built)
        {
            _built = output;
            Build(output);
        }
    }

    void Build(SimulationOutput output)
    {
        Clear(_trailRoot);
        Clear(_guideRoot);

        if (output?.objects == null || output.frames == null || output.frames.Length < 2) return;
        double Bz = output.scenario != null ? output.scenario.Bz : 0.0;

        int positiveIndex = 0, negativeIndex = 0;

        for (int i = 0; i < output.objects.Length; i++)
        {
            var info = output.objects[i];
            if (info == null) continue;

            bool positive = info.charge >= 0f;
            var palette = positive ? PositiveColors : NegativeColors;
            int paletteIndex = positive ? positiveIndex++ : negativeIndex++;
            var color = palette[paletteIndex % palette.Length];

            BuildTrail(output, info.id, color);

            if (showGuides && Mathd.Abs(info.charge) > 1e-9 && Mathd.Abs(Bz) > 1e-12)
                BuildGuide(info, Bz, color);
        }
    }

    // 已走过的路径：逐帧抽稀连线，半透明。
    void BuildTrail(SimulationOutput output, string id, Color color)
    {
        var points = new List<Vector3>();
        var frames = output.frames;
        for (int f = 0; f < frames.Length; f += trailStride)
        {
            var body = FindBody(frames[f], id);
            if (body?.position == null || body.position.Length < 2) continue;
            points.Add(new Vector3(body.position[0], body.position[1], lineZ));
        }
        // 收尾帧补上，避免抽稀把最后一段裁掉
        var last = FindBody(frames[frames.Length - 1], id);
        if (last?.position != null && last.position.Length >= 2)
            points.Add(new Vector3(last.position[0], last.position[1], lineZ));

        if (points.Count < 2) return;

        var line = NewLine("trail_" + id, _trailRoot, Fade(color, 0.55f), trailWidth, _trailMaterial);
        line.positionCount = points.Count;
        line.SetPositions(points.ToArray());
    }

    // 理论圆周：圆心 = 起点 + (m/(qB))·(vy, -vx)，半径 = m|v|/(|q|B)。
    void BuildGuide(SimObject info, double Bz, Color color)
    {
        double qx = info.charge * Bz;
        double vx = info.velocity != null && info.velocity.Length >= 2 ? info.velocity[0] : 0.0;
        double vy = info.velocity != null && info.velocity.Length >= 2 ? info.velocity[1] : 0.0;
        double speed = System.Math.Sqrt(vx * vx + vy * vy);
        if (speed < 1e-9) return;

        double radius = info.mass * speed / System.Math.Abs(qx);
        double centerX = info.position[0] + info.mass / qx * vy;
        double centerY = info.position[1] - info.mass / qx * vx;

        var points = new Vector3[circleSegments + 1];
        for (int k = 0; k <= circleSegments; k++)
        {
            double angle = 2.0 * System.Math.PI * k / circleSegments;
            points[k] = new Vector3(
                (float)(centerX + radius * System.Math.Cos(angle)),
                (float)(centerY + radius * System.Math.Sin(angle)),
                lineZ);
        }

        var line = NewLine("guide_" + info.id, _guideRoot, Fade(color, 0.30f), circleWidth, _guideMaterial);
        line.loop = false;   // 首尾点已闭合（k <= segments）
        line.positionCount = points.Length;
        line.SetPositions(points);
    }

    static FrameBody FindBody(SimFrame frame, string id)
    {
        if (frame?.objects == null) return null;
        foreach (var body in frame.objects)
            if (body != null && body.id == id) return body;
        return null;
    }

    static LineRenderer NewLine(string name, Transform parent, Color color, float width, Material shared)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = 2;
        line.numCornerVertices = 0;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.generateLightingData = false;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = shared;
        return line;
    }

    static Color Fade(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    static void Clear(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
            Destroy(root.GetChild(i).gameObject);
    }

    // System.Math 在这里起别名用，避免和 UnityEngine.Mathf 混淆
    static class Mathd
    {
        public static double Abs(double value) => System.Math.Abs(value);
    }
}
