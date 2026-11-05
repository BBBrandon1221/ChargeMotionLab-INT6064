using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class SceneBinder : MonoBehaviour
{
    // ---------- 供实验室大厅（ChargeLabHub）读取的状态 ----------
    // 大厅是透视第一人称，实验是正交 2.5D。两边都要动同一台相机，
    // 所以大厅在场时由它接管：这里不取景、也不吃键盘。
    public static bool SuppressAutoFrame { get; set; }
    public static bool InputLocked { get; set; }

    public PlaybackController player;

    [Tooltip("相对 StreamingAssets 的路径，例如 SimData/same_q.json；也可填绝对路径或 http URL")]
    public string jsonFileName = "SimData/same_q.json";

    [Tooltip("旧场景里只填了文件名时，自动补这个前缀；留空表示不补")]
    public string dataSubFolder = "SimData";

    public Transform bodiesRoot;
    public Camera viewCamera;
    public bool autoFrameCamera = true;
    public bool autoPlay = true;

    [Tooltip("相机俯角。0 = 正对数据平面（地板天花板会退化成线），20 = 能看到室内空间")]
    public float viewPitch = 20f;

    // 上一次取景用的纵横比。启动那一帧窗口可能还是竖条，不记下来就没法发现并纠正。
    float _framedAspect = -1f;

    [Header("运行时重载")]
    [Tooltip("按 F5 重新读取当前 JSON，Python 重跑后不用退出 Play 模式")]
    public KeyCode reloadKey = KeyCode.F5;

    [Header("运行时切换（数字键 1~9）")]
    [Tooltip("留空则禁用切换。路径同样相对 StreamingAssets")]
    public string[] datasetFiles =
    {
        "SimData/same_q.json",       // 1 同种电荷：同半径同转向
        "SimData/opposite_q.json",   // 2 正负电荷：转向相反
        "SimData/different_qm.json", // 3 电荷量不同：半径不同
    };

    // 按电荷符号取色：正电荷暖色、负电荷冷色，同号多粒子时在各自色系里轮换。
    // 取色比纯饱和色低一档，避免 Standard 材质在方向光下过曝。
    static readonly Color[] PositiveColors =
    {
        new Color(0.92f, 0.45f, 0.12f),   // 橙
        new Color(0.96f, 0.80f, 0.20f),   // 黄橙
        new Color(0.85f, 0.30f, 0.25f),   // 红
    };
    static readonly Color[] NegativeColors =
    {
        new Color(0.16f, 0.45f, 0.88f),   // 蓝
        new Color(0.20f, 0.65f, 0.75f),   // 青
        new Color(0.45f, 0.40f, 0.85f),   // 紫
    };

    void Awake()
    {
        if (player == null) player = GetComponent<PlaybackController>();

        // -flat：退回正对数据平面的老视角，方便对比或排查
        try
        {
            foreach (var a in System.Environment.GetCommandLineArgs())
                if (a == "-flat") viewPitch = 0f;
        }
        catch { /* 拿不到命令行参数时保持默认 */ }
    }

    void Start() => LoadFile(jsonFileName);

    void Update()
    {
        // 纵横比变了就重新取景。启动那一帧窗口还没撑开（实测能拿到 0.5 这种竖条比例），
        // 按它算出来的正交尺寸会一直沿用，整幅画面比该有的大一圈、轨迹细得看不见。
        // 顺带解决用户拖动窗口改尺寸后画面不跟着重排的问题。
        if (!SuppressAutoFrame && autoFrameCamera && viewCamera != null &&
            !Mathf.Approximately(viewCamera.aspect, _framedAspect))
            FrameCamera();

        // 在大厅里时键盘归大厅管（它要拿走 WASD / 数字键），这里不响应
        if (InputLocked) return;

        // F5 重载当前数据集：Python 那边重跑后不用退出 Play 模式。
        if (Input.GetKeyDown(reloadKey)) { LoadFile(jsonFileName); return; }

        if (datasetFiles == null) return;
        for (int i = 0; i < Mathf.Min(datasetFiles.Length, 9); i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) && !string.IsNullOrEmpty(datasetFiles[i]))
            {
                LoadFile(datasetFiles[i]);
                break;
            }
        }
    }

    // 兼容旧场景：只填 "elastic.json" 时自动补成 "SimData/elastic.json"；
    // 已含目录、协议或绝对路径的原样返回。
    // dataSubFolder 读出来是 null/空（旧场景没序列化过这个字段）时按 "SimData" 兜底。
    string ResolvePath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        if (fileName.Contains("/") || fileName.Contains("\\") || fileName.Contains("://")) return fileName;
        var prefix = string.IsNullOrEmpty(dataSubFolder) ? "SimData" : dataSubFolder;
        return prefix + "/" + fileName;
    }

    // 保留同步签名，内部转协程：WebGL / Android 的 StreamingAssets 必须用 UnityWebRequest 读。
    public void LoadFile(string fileName)
    {
        jsonFileName = fileName;
        StartCoroutine(LoadRoutine(fileName));
    }

    IEnumerator LoadRoutine(string fileName)
    {
        yield return player.LoadAsync(ResolvePath(fileName));

        if (!string.IsNullOrEmpty(player.LastError))
        {
            Debug.LogError($"[SceneBinder] 加载 {fileName} 失败：{player.LastError}", this);
            yield break;
        }

        BindTargets();
        player.Seek(0f);   // 绑定完成后重新应用第 0 帧，球不会先停在原点再跳过去
        FrameCamera();
        if (autoPlay) player.Play();

        Debug.Log($"[SceneBinder] {fileName} 已加载，frames = {player.Loader.Output.frames.Length}，bodies = {player.Targets.Count}");
    }

    // 改参数后重算并上屏：求解 → 校验 → 绑定 → 重新取景 → 播放。
    // 走的是和 LoadRoutine 同一条绑定路径，所以球数、视觉半径、颜色规则完全一致。
    public bool ApplySetup(SimulationSolver.Setup setup)
    {
        var result = SimulationSolver.Solve(setup);
        if (!result.Ok)
        {
            Debug.LogError($"[SceneBinder] 参数重算失败：{result.Error}", this);
            return false;
        }

        if (!player.LoadFromOutput(result.Output)) return false;

        // LoadFromOutput 内部已经广播了 Loaded（参数面板据此刷新只读区），
        // 但绑定要在这里补做——质量变了，视觉半径映射也得跟着重算。
        BindTargets();
        player.Seek(0f);
        FrameCamera();
        if (autoPlay) player.Play();

        Debug.Log($"[SceneBinder] 参数重算完成，frames = {result.Output.frames.Length}，bodies = {player.Targets.Count}");
        return true;
    }

    // 取当前数据集的参数快照，供编辑面板初始化输入框；数据不完整时返回 null。
    public SimulationSolver.Setup CaptureSetup()
    {
        return player.Loader.Output == null ? null : SimulationSolver.Capture(player.Loader.Output);
    }

    // 按 JSON 里的 id 找场景同名子物体；找不到就按顺序兜底，并按 radius 调整大小。
    // 颜色按电荷符号分配：正电荷暖色、负电荷冷色 —— 观众不用读数字就能看出谁正谁负。
    void BindTargets()
    {
        if (bodiesRoot == null || player.Loader.Output == null) return;

        var children = new List<Transform>();
        foreach (Transform child in bodiesRoot) children.Add(child);

        var objs = player.Loader.Output.objects;
        player.Targets.Clear();

        // 预摆球不够时动态补齐，否则物体数多于场景球数时多出来的物体会被静默丢掉。
        while (children.Count < objs.Length)
            children.Add(CreateRuntimeBall(children.Count));

        // 没被这一组数据用到的球先隐藏，避免多出来的球停在原点。
        foreach (var child in children) child.gameObject.SetActive(false);

        int positiveIndex = 0, negativeIndex = 0;

        for (int i = 0; i < objs.Length; i++)
        {
            Transform target = null;
            foreach (var child in children)
            {
                if (child.name == objs[i].id) { target = child; break; }
            }
            if (target == null && i < children.Count) target = children[i];
            if (target == null) continue;

            target.gameObject.SetActive(true);

            // Python 的 radius 是物理半径，Unity 内置 Sphere 原始直径 = 1，故 scale = 2r。
            // 磁场版不做"按质量放大"：要比较的是圆周半径 r=mv/(|q|B)，由参考圈表达，
            // 球本身保持物理尺寸，避免视觉信息和物理信息打架。
            target.localScale = Vector3.one * (objs[i].radius * 2f);
            player.Targets[objs[i].id] = target;

            // 按电荷符号染色
            bool positive = objs[i].charge >= 0f;
            var palette = positive ? PositiveColors : NegativeColors;
            int index = positive ? positiveIndex++ : negativeIndex++;
            ApplyBallColor(target, palette[index % palette.Length]);
        }
    }

    // 无论预摆球还是动态补的球，统一走 MaterialPropertyBlock 染色，不污染共享材质。
    static void ApplyBallColor(Transform target, Color color)
    {
        var renderer = target.GetComponent<Renderer>();
        if (renderer == null) return;

        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetColor("_Color", color);
        block.SetColor("_BaseColor", color);
        renderer.SetPropertyBlock(block);
    }

    // 场景里预摆球不足时按需生成，复用已有球的材质，颜色随后由 BindTargets 按电荷分配。
    Transform CreateRuntimeBall(int index)
    {
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "auto_body_" + index;
        ball.transform.SetParent(bodiesRoot, false);

        // 回放靠 Transform 直接赋值，碰撞体留着只会增加开销。
        var collider = ball.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        var renderer = ball.GetComponent<Renderer>();
        if (renderer != null && bodiesRoot.childCount > 1)
        {
            // 借用第一颗预摆球的材质，避免运行时 Shader.Find 找不到而变成洋红。
            // 颜色由 ApplyBallColor 单独覆盖。
            var source = bodiesRoot.GetChild(0).GetComponent<Renderer>();
            if (source != null && source.sharedMaterial != null) renderer.sharedMaterial = source.sharedMaterial;
        }

        return ball.transform;
    }

    // 根据整段轨迹的范围自动取景，2 球和 3 球数据集都不用手调相机。
    void FrameCamera()
    {
        // 大厅模式下相机归第一人称，实验画面还没上台，不能抢
        if (SuppressAutoFrame) return;
        if (!autoFrameCamera || viewCamera == null || player.Loader.Output == null) return;

        var frames = player.Loader.Output.frames;

        // 取景以物理边界（scenario.bounds）为基准，再和实际轨迹取并集。
        // 如果只按可见帧取景，same_q（±2.5）和 opposite_q（±5.6）会差出 2.6 倍的画幅，
        // 场景里的实验室布景就得跟着一起缩放漂移。以 bounds 为准，三个数据集画幅一致。
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        var bounds = player.Loader.Output.scenario != null ? player.Loader.Output.scenario.bounds : null;
        if (bounds != null && bounds.min != null && bounds.max != null &&
            bounds.min.Length >= 2 && bounds.max.Length >= 2)
        {
            minX = bounds.min[0]; maxX = bounds.max[0];
            minY = bounds.min[1]; maxY = bounds.max[1];
        }

        foreach (var frame in frames)
        {
            if (frame?.objects == null) continue;
            foreach (var item in frame.objects)
            {
                if (item == null || item.position == null || item.position.Length < 2) continue;
                minX = Mathf.Min(minX, item.position[0]);
                maxX = Mathf.Max(maxX, item.position[0]);
                minY = Mathf.Min(minY, item.position[1]);
                maxY = Mathf.Max(maxY, item.position[1]);
            }
        }
        if (minX > maxX) return;

        // 加上最大半径，避免球心在视野内但球体贴边被裁掉。
        float pad = 0f;
        foreach (var info in player.Loader.Output.objects)
            if (info != null) pad = Mathf.Max(pad, info.radius);
        minX -= pad; maxX += pad; minY -= pad; maxY += pad;

        // 1D 模式 y 恒为 0，包围盒高度为 0，靠下面的 +0.8 兜底才不会缩成一条线。
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float halfW = (maxX - minX) * 0.5f;
        float halfH = (maxY - minY) * 0.5f;
        float aspect = viewCamera.aspect <= 0f ? 1.7778f : viewCamera.aspect;

        viewCamera.orthographic = true;   // 物理平面铺在屏幕平面上，透视会引入大小偏差
        // 俯视一点，让实验室的地板、天花板、台面都能读出来。
        // 正交投影不会近大远小，圆的纵向只压缩 cos14° ≈ 0.97 ——
        // 半径 2m 的参考圈短轴只差 6cm，看不出来；换来的是整间屋子有纵深。
        var rot = Quaternion.Euler(viewPitch, 0f, 0f);
        // 视线中心略微上抬：画面整体下移一点，多露出一段地面，
        // 数据区仍然完整（只是不再死板地居中）
        var target = new Vector3(centerX, centerY + 0.6f, 0f);
        viewCamera.transform.rotation = rot;
        viewCamera.transform.position = target - rot * Vector3.forward * 12f;
        viewCamera.orthographicSize = Mathf.Max(halfH, halfW / aspect) + 1.5f;
        _framedAspect = aspect;   // 记住这次用的比例，Update 里比对，变了就重排
    }
}
