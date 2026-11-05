using UnityEngine;

// 周三 ChargeMotionLab 的实验室场景。
//
// 相机在 SceneBinder.FrameCamera 里被摆成「正交投影 + 14° 俯角」，
// 所以水平的地板、天花板在画面里是有纵深的，不会退化成一条线。
// 房间用世界坐标固定摆（不跟相机缩放）：取景已按 scenario.bounds 统一，
// 三个数据集的画幅一致，房间自然稳定。
//
// 尺寸依据：数据集 bounds 是 x/y ∈ [-6, 6]，粒子最高飞到 y=6、最低 -6。
// 所以地板压到 y=-6.8（球几乎贴着地面飞），后墙在 z=8，天花板 y=7.5，
// 左右延伸到 x=±16 —— 画面半宽约 11.9，四周都有实体，不会露出天空。
[DisallowMultipleComponent]
public sealed class LabRoomController : MonoBehaviour
{
    // 房间边界。公开给 ChargeLabHub 用：第一人称视高、走动范围都得跟着这套尺寸走，
    // 写死两份迟早会对不上（改一个忘了另一个就穿墙或浮空）。
    public const float FloorY = -6.80f;
    public const float CeilY = 7.50f;
    public const float BackZ = 6.00f;
    public const float FrontZ = -16.0f;
    public const float HalfX = 16.0f;

    // 实验台 / 吊柜的位置
    public const float BenchZ = 4.20f;
    const float BenchTopY = -5.45f;
    static readonly float[] BenchX = { -7.90f, 7.90f };

    Transform _root;

    static readonly Color WallColor = new Color(0.900f, 0.915f, 0.930f);
    static readonly Color WainscotColor = new Color(0.415f, 0.455f, 0.515f);
    static readonly Color FloorColor = new Color(0.615f, 0.635f, 0.660f);
    static readonly Color CabinetColor = new Color(0.790f, 0.812f, 0.840f);
    static readonly Color SteelColor = new Color(0.655f, 0.680f, 0.705f);
    static readonly Color DarkColor = new Color(0.165f, 0.190f, 0.230f);
    static readonly Color LampColor = new Color(1.000f, 0.965f, 0.895f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstall()
    {
        try
        {
            foreach (var a in System.Environment.GetCommandLineArgs())
                if (a == "-nolab") return;   // 排查问题时关掉场景
        }
        catch { /* 拿不到命令行参数时照常安装 */ }

        var go = new GameObject("LabRoom");
        go.AddComponent<LabRoomController>();
    }

    void Awake()
    {
        _root = new GameObject("LabRoomRoot").transform;
        _root.SetParent(transform, false);

        // 正交投影画不出透视汇聚，远近只能靠雾来区分：后墙开始变淡，越远越浅。
        // 雾色取墙面同色系，免得看起来像糊了一层灰。
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.880f, 0.895f, 0.915f);
        RenderSettings.fogStartDistance = 15f;
        RenderSettings.fogEndDistance = 34f;

        BuildFloor();
        BuildWalls();
        BuildCeiling();
        BuildBenches();
        BuildCabinets();
        BuildChairs();
    }

    // ---------- 地面 ----------

    void BuildFloor()
    {
        float depth = BackZ - FrontZ;
        float zc = (BackZ + FrontZ) * 0.5f;

        // 环氧地坪：浅灰半哑光，带一点反光才像室内
        Box("lab_floor", new Vector3(0f, FloorY - 0.10f, zc),
            new Vector3(HalfX * 2f + 4f, 0.20f, depth + 4f), Solid(FloorColor, 0.52f));

        // 地砖分格线：俯视角下这些线能读出「地面是平的」
        var seam = Solid(new Color(0.470f, 0.495f, 0.530f), 0.40f);
        for (float z = FrontZ + 2f; z <= BackZ; z += 2f)
            Box("floor_seam", new Vector3(0f, FloorY + 0.005f, z),
                new Vector3(HalfX * 2f + 4f, 0.012f, 0.045f), seam);
        for (float x = -14f; x <= 14f; x += 2f)
            Box("floor_seam", new Vector3(x, FloorY + 0.005f, zc),
                new Vector3(0.045f, 0.012f, depth + 4f), seam);

        // 踢脚线
        Box("floor_skirting", new Vector3(0f, FloorY + 0.09f, BackZ - 0.06f),
            new Vector3(HalfX * 2f, 0.18f, 0.14f), Solid(new Color(0.560f, 0.585f, 0.625f), 0.45f));

        // 墙脚接触阴影：一道比地面略深的窄带，把地面和墙分开
        Box("floor_contact", new Vector3(0f, FloorY + 0.004f, BackZ - 0.62f),
            new Vector3(HalfX * 2f, 0.012f, 0.90f), Solid(new Color(0.400f, 0.420f, 0.450f), 0.30f));
    }

    // ---------- 后墙 ----------

    void BuildWalls()
    {
        float wallH = CeilY - FloorY;
        float wallYc = (CeilY + FloorY) * 0.5f;

        Box("lab_wall", new Vector3(0f, wallYc, BackZ + 0.15f),
            new Vector3(HalfX * 2f, wallH, 0.30f), Solid(WallColor, 0.14f));

        // 墙裙：实验室下半截常见的耐擦洗深色板
        Box("lab_wainscot", new Vector3(0f, FloorY + 0.62f, BackZ - 0.02f),
            new Vector3(HalfX * 2f, 1.24f, 0.10f), Solid(WainscotColor, 0.28f));
        Box("lab_wainscot_edge", new Vector3(0f, FloorY + 1.28f, BackZ - 0.08f),
            new Vector3(HalfX * 2f, 0.10f, 0.16f), Solid(DarkColor, 0.50f));

        // 墙面竖向分缝
        var seam = Solid(new Color(0.830f, 0.848f, 0.868f), 0.12f);
        for (int i = -7; i <= 7; i++)
            Box("lab_seam", new Vector3(i * 2.4f, FloorY + 4.2f, BackZ - 0.02f),
                new Vector3(0.055f, 5.6f, 0.06f), seam);

        // 横向桥架（走线槽）
        var tray = Solid(new Color(0.520f, 0.552f, 0.580f), 0.55f);
        Box("lab_tray", new Vector3(0f, FloorY + 4.55f, BackZ - 0.12f),
            new Vector3(HalfX * 2f - 1f, 0.22f, 0.30f), tray);

        // 排风口
        var vent = Solid(new Color(0.470f, 0.500f, 0.535f), 0.45f);
        for (int s = -1; s <= 1; s += 2)
            Box("lab_vent", new Vector3(11.4f * s, FloorY + 4.55f, BackZ - 0.22f),
                new Vector3(1.05f, 0.72f, 0.10f), vent);

        // 左右侧墙 + 前墙：2.5D 正交取景的半宽约 15.3m，比 HalfX=16 小，
        // 所以这三面墙在实验画面里恰好在画框外，不挡数据；
        // 但第一人称一转身，没有它们就是一片纯色虚空。
        var wallMat = Solid(WallColor, 0.14f);
        var skirtMat = Solid(WainscotColor, 0.28f);
        float depth = BackZ - FrontZ;
        float zc = (BackZ + FrontZ) * 0.5f;

        for (int s = -1; s <= 1; s += 2)
        {
            float x = s * (HalfX + 0.15f);
            Box("lab_wall_side", new Vector3(x, wallYc, zc),
                new Vector3(0.30f, wallH, depth + 0.30f), wallMat);
            Box("lab_skirt_side", new Vector3(s * (HalfX - 0.02f), FloorY + 0.62f, zc),
                new Vector3(0.10f, 1.24f, depth), skirtMat);
        }

        Box("lab_wall_front", new Vector3(0f, wallYc, FrontZ - 0.15f),
            new Vector3(HalfX * 2f + 0.6f, wallH, 0.30f), wallMat);
        Box("lab_skirt_front", new Vector3(0f, FloorY + 0.62f, FrontZ + 0.02f),
            new Vector3(HalfX * 2f, 1.24f, 0.10f), skirtMat);

        BuildWallProps();
    }

    // 侧墙上的壁挂设备：第一人称走到房间中段时，两侧不能只有一片白墙。
    // 位置刻意避开 x∈[-6,6] 的中央通道，不挡走向实验箱的视线。
    void BuildWallProps()
    {
        var caseMat = Solid(new Color(0.845f, 0.862f, 0.885f), 0.30f);
        var glassMat = Solid(new Color(0.700f, 0.815f, 0.855f), 0.85f,
            new Color(0.10f, 0.26f, 0.32f) * 0.5f);
        var darkMat = Solid(DarkColor, 0.45f);

        foreach (int s in new[] { -1, 1 })
        {
            for (int i = 0; i < 2; i++)
            {
                float z = -5.0f - i * 5.0f;
                var root = new GameObject("lab_wallcase");
                root.transform.SetParent(_root, false);
                root.transform.localPosition = new Vector3(s * (HalfX - 0.36f), 0f, z);
                var t = root.transform;

                Box("wallcase_body", new Vector3(0f, FloorY + 2.55f, 0f),
                    new Vector3(0.52f, 1.60f, 1.30f), caseMat, t);
                Box("wallcase_glass", new Vector3(-s * 0.28f, FloorY + 2.55f, 0f),
                    new Vector3(0.05f, 1.34f, 1.06f), glassMat, t);
                Box("wallcase_seam", new Vector3(-s * 0.30f, FloorY + 2.55f, 0f),
                    new Vector3(0.03f, 1.36f, 0.05f), darkMat, t);
                Box("wallcase_handle", new Vector3(-s * 0.32f, FloorY + 2.55f, 0.34f),
                    new Vector3(0.04f, 0.26f, 0.05f), darkMat, t);
            }

            // 侧墙踢脚上方的电缆槽，横向拉一条，给墙面分层
            Box("lab_conduit", new Vector3(s * (HalfX - 0.10f), FloorY + 4.05f, (BackZ + FrontZ) * 0.5f),
                new Vector3(0.14f, 0.16f, BackZ - FrontZ - 1.0f),
                Solid(new Color(0.520f, 0.552f, 0.580f), 0.55f));
        }
    }

    // ---------- 天花板 ----------

    void BuildCeiling()
    {
        float depth = BackZ - FrontZ;
        float zc = (BackZ + FrontZ) * 0.5f;

        Box("lab_ceiling", new Vector3(0f, CeilY, zc),
            new Vector3(HalfX * 2f + 4f, 0.20f, depth + 4f),
            Solid(new Color(0.760f, 0.780f, 0.800f), 0.10f));

        // 三排灯带
        var tube = Solid(LampColor, 0.30f, LampColor * 2.4f);
        var shell = Solid(new Color(0.700f, 0.725f, 0.750f), 0.55f);
        for (int i = 0; i < 3; i++)
        {
            float z = 3.0f - i * 3.5f;
            Box("ceiling_light_shell", new Vector3(0f, CeilY - 0.16f, z),
                new Vector3(13.0f, 0.14f, 0.46f), shell);
            Box("ceiling_light_tube", new Vector3(0f, CeilY - 0.26f, z),
                new Vector3(12.4f, 0.10f, 0.30f), tube);
        }
    }

    // ---------- 左右实验台 ----------

    void BuildBenches()
    {
        var bodyMat = Solid(CabinetColor, 0.32f);
        var topMat = Solid(SteelColor, 0.68f);
        var darkMat = Solid(DarkColor, 0.45f);
        var screenMat = Solid(new Color(0.085f, 0.300f, 0.500f), 0.60f, new Color(0.16f, 0.46f, 0.66f));
        var glassMat = Solid(new Color(0.760f, 0.880f, 0.920f), 0.85f);

        foreach (float bx in BenchX)
        {
            var root = new GameObject("lab_bench");
            root.transform.SetParent(_root, false);
            root.transform.localPosition = new Vector3(bx, 0f, BenchZ);
            var t = root.transform;

            // 台下柜体 + 门缝 + 拉手
            Box("bench_body", new Vector3(0f, FloorY + 0.62f, 0f),
                new Vector3(3.40f, 1.24f, 1.05f), bodyMat, t);
            Box("bench_seam", new Vector3(0f, FloorY + 0.72f, -0.54f),
                new Vector3(3.30f, 0.035f, 0.06f), darkMat, t);
            Box("bench_handle", new Vector3(0f, FloorY + 0.98f, -0.55f),
                new Vector3(0.90f, 0.045f, 0.06f), darkMat, t);

            // 台面（比柜体悬出 8cm，侧看有厚度）
            Box("bench_top", new Vector3(0f, BenchTopY, 0f),
                new Vector3(3.86f, 0.13f, 1.33f), topMat, t);
            Box("bench_lip", new Vector3(0f, BenchTopY + 0.09f, -0.62f),
                new Vector3(3.86f, 0.09f, 0.07f), darkMat, t);

            float deckY = BenchTopY + 0.065f;   // 台面上表面

            // 示波器：机身 + 发光屏 + 三个旋钮
            Box("scope_body", new Vector3(-0.92f, deckY + 0.34f, 0.10f),
                new Vector3(1.30f, 0.68f, 0.60f), darkMat, t);
            Box("scope_screen", new Vector3(-0.92f, deckY + 0.44f, -0.21f),
                new Vector3(1.06f, 0.46f, 0.05f), screenMat, t);
            for (int k = 0; k < 3; k++)
            {
                var knob = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                knob.name = "scope_knob";
                Destroy(knob.GetComponent<Collider>());
                knob.transform.SetParent(t, false);
                knob.transform.localPosition = new Vector3(-1.36f + k * 0.20f, deckY + 0.11f, -0.26f);
                knob.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                knob.transform.localScale = new Vector3(0.13f, 0.035f, 0.13f);
                Paint(knob.GetComponent<MeshRenderer>(), Solid(new Color(0.700f, 0.725f, 0.755f), 0.55f));
            }

            // 直流电源箱
            Box("psu_body", new Vector3(0.95f, deckY + 0.24f, 0.12f),
                new Vector3(0.96f, 0.46f, 0.54f), Solid(new Color(0.525f, 0.555f, 0.600f), 0.55f), t);
            Box("psu_panel", new Vector3(0.95f, deckY + 0.30f, -0.16f),
                new Vector3(0.78f, 0.24f, 0.05f), darkMat, t);

            // 一排试管
            for (int k = 0; k < 4; k++)
            {
                var vial = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                vial.name = "vial";
                Destroy(vial.GetComponent<Collider>());
                vial.transform.SetParent(t, false);
                vial.transform.localPosition = new Vector3(-0.26f + k * 0.24f, deckY + 0.19f, 0.42f);
                vial.transform.localScale = new Vector3(0.085f, 0.19f, 0.085f);
                Paint(vial.GetComponent<MeshRenderer>(), glassMat);
            }

            // 台下隔板，避免柜体是一整块实心板
            Box("bench_shelf", new Vector3(0f, FloorY + 0.30f, 0.30f),
                new Vector3(3.10f, 0.07f, 0.62f), topMat, t);
        }
    }

    // ---------- 实验台上方的吊柜 ----------

    void BuildCabinets()
    {
        var cabMat = Solid(CabinetColor, 0.34f);
        var darkMat = Solid(DarkColor, 0.45f);
        var glassMat = Solid(new Color(0.640f, 0.760f, 0.800f), 0.80f);

        foreach (float bx in BenchX)
        {
            var root = new GameObject("lab_cabinet");
            root.transform.SetParent(_root, false);
            root.transform.localPosition = new Vector3(bx * 1.24f, 0f, BackZ - 0.55f);
            var t = root.transform;

            Box("cab_body", new Vector3(0f, FloorY + 4.35f, 0f),
                new Vector3(3.50f, 1.05f, 0.62f), cabMat, t);
            for (int s = -1; s <= 1; s += 2)
                Box("cab_glass", new Vector3(0.74f * s, FloorY + 4.35f, -0.33f),
                    new Vector3(1.52f, 0.84f, 0.04f), glassMat, t);
            Box("cab_seam", new Vector3(0f, FloorY + 4.35f, -0.34f),
                new Vector3(0.05f, 0.84f, 0.05f), darkMat, t);
            Box("cab_light", new Vector3(0f, FloorY + 3.80f, -0.10f),
                new Vector3(3.30f, 0.05f, 0.16f), Solid(LampColor, 0.30f, LampColor * 1.6f), t);
        }
    }

    // ---------- 实验台前的凳子 ----------

    void BuildChairs()
    {
        var seatMat = Solid(new Color(0.365f, 0.400f, 0.445f), 0.40f);
        var legMat = Solid(new Color(0.520f, 0.545f, 0.575f), 0.60f);

        foreach (float bx in BenchX)
        {
            for (int i = 0; i < 2; i++)
            {
                var root = new GameObject("lab_stool");
                root.transform.SetParent(_root, false);
                root.transform.localPosition = new Vector3(bx - 0.85f + i * 1.70f, 0f, BenchZ - 1.85f);
                var t = root.transform;

                Box("stool_seat", new Vector3(0f, FloorY + 0.63f, 0f),
                    new Vector3(0.52f, 0.09f, 0.52f), seatMat, t);
                Box("stool_post", new Vector3(0f, FloorY + 0.30f, 0f),
                    new Vector3(0.09f, 0.62f, 0.09f), legMat, t);
                Box("stool_base", new Vector3(0f, FloorY + 0.03f, 0f),
                    new Vector3(0.56f, 0.05f, 0.56f), legMat, t);
            }
        }
    }

    // ---------- 小工具 ----------

    // smoothness 在 Glow 分支里用不到（Unlit 没有高光），保留参数是为了调用处不改。
    static Material Solid(Color color, float smoothness = 0.2f, Color? emission = null)
    {
        // 自发光走自己写的 Unlit shader：Standard 的 _EMISSION 变体在打包后会被剥离，
        // 灯带和仪器屏幕会静默变成「一块不亮的灰板」。
        if (emission.HasValue) return Glow(emission.Value);

        var shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
        var m = new Material(shader);
        m.color = color;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        return m;
    }

    static Material Glow(Color color)
    {
        var shader = Resources.Load<Shader>("Shaders/Glow");
        if (shader == null) shader = Shader.Find("ChargeLab/Glow");
        if (shader == null) return Solid(color);

        var m = new Material(shader);
        m.name = "Lab_Glow";
        var c = new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), color.a);
        m.color = c;
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    static void Paint(Renderer r, Material m) { if (r != null) r.sharedMaterial = m; }

    void Box(string name, Vector3 worldPos, Vector3 size, Material mat, Transform parent = null)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        var col = cube.GetComponent<Collider>();
        if (col != null) Destroy(col);          // 场景不参与碰撞，免得影响射线命中
        cube.transform.SetParent(parent != null ? parent : _root, false);
        cube.transform.localPosition = worldPos;
        cube.transform.localScale = size;
        Paint(cube.GetComponent<MeshRenderer>(), mat);
    }
}
