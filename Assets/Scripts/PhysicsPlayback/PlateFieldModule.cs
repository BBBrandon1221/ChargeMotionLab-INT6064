using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 第 4 模块：平行板电场偏转靶场（2D）
//
// 教学场景：上下两块平行极板（上 + 下 −）之间是匀强电场，方向由上往下；
// 电子带负电，所以电子流是被电场「顶」着从下往上跑的（与学生直觉相反，正好要他们看出来）。
// 一颗带正电的小球从左侧水平射入，被电场推向下方，要落在下极板的靶心上。
// 学生调「电压 / 电荷量 / 质量 / 初速度」四个参数，看小球能不能打到靶。
//
// 为什么单独一个模块、不并进 SimulationSolver：
//   原来的引擎是纯磁场（Boris 旋转、洛伦兹力不做功、速率不变），而这里电场会做功，
//   速率一路变，物理结论正好相反。硬塞进同一个求解器就要给 Validate / Step / HUD
//   全部加分支，那 9 个已验收的磁场算例会被牵连。
//
// 积分器：不积分。极板之间是匀强场，加速度恒定，轨迹是精确抛物线；
//   分三段（板外直线 → 板间抛物线 → 板外直线）解析求解，误差只有 float 舍入。
//   自检见 PlateFieldSelfTest：与 dt=2e-5 的数值积分逐点比对。
public sealed class PlateFieldModule : MonoBehaviour
{
    public static PlateFieldModule Instance { get; private set; }
    public static bool Active { get; private set; }

    // ---------- 几何（世界单位，全部躺 z=0 平面）----------
    const float HalfSpan = 3.20f;    // 极板半长：x ∈ [-3.2, +3.2]
    const float Gap = 4.40f;         // 极板间距：y = ±2.2
    const float PlateThick = 0.14f;
    const float PlateDepth = 0.30f;
    const float BallRadius = 0.20f;
    const float LaunchX = -6.30f;
    const float TargetHalf = 0.55f;  // 靶心半宽（命中）
    const float AmberHalf = 1.15f;   // 擦边半宽

    // 板面/靶面高度（球的圆心到达这里就算落地）
    const float BottomY = -Gap * 0.5f;
    const float TopY = Gap * 0.5f;

    // ---------- 参数表（学生改的就是这四个）----------
    public float Voltage = 40f;    // U，示意单位
    public float Charge = 1.0f;    // q
    public float Mass = 1.0f;      // m
    // v0 默认 7.2：落点 +1.57，靶心在 0 —— 第一次发射必定打偏。
    // 这是课程设计的一部分：先让「失败」出现，学生才有动机去调参数，
    // 而不是一进来就看到「命中」然后无感。调到 ≈4.8 才进绿区。
    public float Speed = 7.2f;     // v0
    public float StartY;
    public bool ShowPredict = true;   // [G] 关掉灰色预测线，先让学生猜

    // ---------- 试射状态 ----------
    float _targetX;
    int _scoreHit, _scoreTries;

    Flight _flight;
    float _elapsed;
    bool _running;
    int _verdict = -1;            // -1 未结算，其余为 FlightOutcome
    static bool _fireOnEnter;     // -platefire
    float _autoFireAt;            // >0 表示等着自动发射

    // ---------- 场景对象 ----------
    Camera _cam;
    Transform _root;
    Transform _ball;
    Transform _targetPad, _padLeft, _padRight;
    TextMesh _ballLabel;
    LineRenderer _trail, _ghost;
    readonly List<Transform> _electrons = new List<Transform>();
    float _electronPhase;

    readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();

    GUIStyle _title, _text, _muted, _value, _btn, _banner, _bannerSub;
    bool _styles;

    // ---------- 物理：解析解 ----------

    public enum FlightOutcome { None = 0, Bottom = 1, Top = 2, Escaped = 3 }

    public struct Flight
    {
        public FlightOutcome Outcome;
        public float ImpactX;      // 命中板面时的 x（Outcome = Bottom / Top）
        public float TotalTime;    // 从发射到结束的时长
        public Vector2[] Points;   // 采样轨迹（世界坐标）
        public float Acceleration; // a = qU/(m·d)，向上为正
        public float Field;        // E = U/d
        public float ExitY;        // 飞出右侧时的 y（仅 Outcome = Escaped 有意义）
    }

    // 从上极板指向下极板的场，方向 -y。所以 a_y = -qU/(m·d)。
    public float Acceleration
    {
        get { return -Charge * (Voltage / Gap) / Mathf.Max(0.0001f, Mass); }
    }

    // 解析求解整条轨迹。dt 只是采样密度，不参与物理。
    public Flight Solve()
    {
        var flight = new Flight
        {
            Acceleration = Acceleration,
            Field = Voltage / Gap,
            ImpactX = 0f,
            TotalTime = 0f,
            Outcome = FlightOutcome.None,
        };

        float a = flight.Acceleration;
        float v = Mathf.Max(0.05f, Speed);
        float xIn = -HalfSpan, xOut = HalfSpan;
        float yBottom = BottomY + BallRadius;   // 球心贴在下板上的高度
        float yTop = TopY - BallRadius;

        float tEnter = (xIn - LaunchX) / v;     // 走完板外那段直线的时间
        float tCross = (xOut - xIn) / v;        // 穿过板间所需时间

        var pts = new List<Vector2>(160);
        pts.Add(new Vector2(LaunchX, StartY));
        pts.Add(new Vector2(xIn, StartY));

        // 板间：y(τ) = StartY + ½aτ²，τ 从进入板间开始算
        float tauHit = -1f;
        FlightOutcome hitSide = FlightOutcome.None;
        if (Mathf.Abs(a) > 1e-6f)
        {
            float tauBottom = Mathf.Sqrt(2f * (yBottom - StartY) / a);
            float tauTop = Mathf.Sqrt(2f * (yTop - StartY) / a);
            if (a < 0f && !float.IsNaN(tauBottom) && tauBottom <= tCross) { tauHit = tauBottom; hitSide = FlightOutcome.Bottom; }
            if (a > 0f && !float.IsNaN(tauTop) && tauTop <= tCross) { tauHit = tauTop; hitSide = FlightOutcome.Top; }
        }

        float endTau = tauHit >= 0f ? tauHit : tCross;
        int samples = 72;
        for (int i = 1; i <= samples; i++)
        {
            float tau = endTau * i / samples;
            float x = xIn + v * tau;
            float y = StartY + 0.5f * a * tau * tau;
            pts.Add(new Vector2(x, y));
        }

        if (tauHit >= 0f)
        {
            flight.Outcome = hitSide;
            flight.ImpactX = xIn + v * tauHit;
            flight.TotalTime = tEnter + tauHit;
        }
        else
        {
            // 出了极板就没有电场了：从出口按出射速度走直线，直到离开画面
            float yExit = StartY + 0.5f * a * tCross * tCross;
            float vy = a * tCross;
            flight.Outcome = FlightOutcome.Escaped;
            float stopX = 8.6f;
            float extra = (stopX - xOut) / v;
            flight.ExitY = yExit + vy * extra;
            pts.Add(new Vector2(stopX, flight.ExitY));
            flight.TotalTime = tEnter + tCross + extra;
        }

        flight.Points = pts.ToArray();
        return flight;
    }

    // 任意时刻的位置（动画和判定共用同一条曲线，画出来的和算出来的不会打架）
    public Vector2 PositionAt(Flight flight, float t)
    {
        float v = Mathf.Max(0.05f, Speed);
        float xIn = -HalfSpan;
        float tEnter = (xIn - LaunchX) / v;

        if (t <= 0f) return new Vector2(LaunchX, StartY);
        if (t <= tEnter)
            return new Vector2(LaunchX + v * t, StartY);

        float tau = t - tEnter;
        float a = flight.Acceleration;
        float y = StartY + 0.5f * a * tau * tau;
        float x = xIn + v * tau;

        // 出了极板就改走直线
        float tCross = (HalfSpan * 2f) / v;
        if (tau > tCross)
        {
            float yExit = StartY + 0.5f * a * tCross * tCross;
            float vy = a * tCross;
            y = yExit + vy * (tau - tCross);
        }
        return new Vector2(x, y);
    }

    // ---------- 自检：解析解 vs 数值积分 ----------
    //
    // 极板之间 a 恒定，所以整条轨迹有闭式解；这里用 dt=2e-5 的半隐式欧拉独立走一遍，
    // 只比落点。差超过 1cm 就说明解析解的符号 / 根号 / 边界写错了，不是浮点误差。
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var go = new GameObject("plate_selftest");
        var probe = go.AddComponent<PlateFieldModule>();
        probe.StartY = 0f;
        probe.Mass = 1f;
        probe.Speed = 4f;

        probe.Charge = 1f; probe.Voltage = 40f;
        OneCase(sb, probe, "正常  q=+1  U=40  打到下板");
        probe.Voltage = 0f;
        OneCase(sb, probe, "无场  U=0   应当直线飞出");
        probe.Voltage = 40f; probe.Charge = -1f;
        OneCase(sb, probe, "反号  q=−1  U=40  打到上板");

        // 单调性：电压越大偏转越强，落点越靠左
        probe.Charge = 1f;
        probe.Voltage = 20f; float x20 = probe.Solve().ImpactX;
        probe.Voltage = 40f; float x40 = probe.Solve().ImpactX;
        probe.Voltage = 60f; float x60 = probe.Solve().ImpactX;
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "单调性  U=20/40/60 → 落点 {0:0.000} / {1:0.000} / {2:0.000}   {3}",
            x20, x40, x60, (x20 > x40 && x40 > x60) ? "OK" : "!! 不单调"));

        UnityEngine.Object.Destroy(go);
        return sb.ToString();
    }

    static void OneCase(StringBuilder sb, PlateFieldModule m, string name)
    {
        var f = m.Solve();
        float v = Mathf.Max(0.05f, m.Speed);
        float a = f.Acceleration;
        float yLim = f.Outcome == FlightOutcome.Bottom ? BottomY + BallRadius
                   : f.Outcome == FlightOutcome.Top ? TopY - BallRadius : 0f;
        bool expectHit = f.Outcome == FlightOutcome.Bottom || f.Outcome == FlightOutcome.Top;

        const float dt = 2e-5f;
        float x = LaunchX, y = m.StartY, vy = 0f;
        int guard = 0;
        while (guard++ < 3000000)
        {
            float acc = (x >= -HalfSpan && x <= HalfSpan) ? a : 0f;
            vy += acc * dt;
            x += v * dt;
            y += vy * dt;
            if (expectHit && ((a < 0f && y <= yLim) || (a > 0f && y >= yLim))) break;
            if (x > 8.6f) break;
        }

        if (expectHit)
        {
            float d = Mathf.Abs(x - f.ImpactX) * 1000f;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0}：解析 {1:0.000} m  数值 {2:0.000} m  差 {3:0.0} mm   {4}",
                name, f.ImpactX, x, d, d < 10f ? "OK" : "!! 偏差过大"));
        }
        else
        {
            float d = Mathf.Abs(y - f.ExitY) * 1000f;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0}：解析出口 y {1:0.000} m  数值 {2:0.000} m  差 {3:0.0} mm   {4}",
                name, f.ExitY, y, d, d < 10f ? "OK" : "!! 偏差过大"));
        }
    }

    // ---------- 入口 ----------

    public static void ShowModule(Camera camera)
    {
        if (Instance == null)
        {
            var go = new GameObject("PlateFieldModule");
            Instance = go.AddComponent<PlateFieldModule>();
        }
        Instance.EnsureBuilt(camera);
        Instance.ApplyCli();
        Instance._root.gameObject.SetActive(true);
        Active = true;
        Instance.FrameCamera();
        Instance.ResetShot();
        // 进站时球停在发射口（不是停在落点）：先让学生看参数、猜落点，按空格才飞。
        // 灰色预测线仍然画着，老师可以用 [G] 关掉，改成「先猜后验」。
        Instance._elapsed = 0f;
        Instance._running = false;
        Instance._scoreHit = 0;
        Instance._scoreTries = 0;
        Instance.ApplyBall();

        // -platefire：进站 1.2 秒后自动发射一次，只为出验收图（手动演示不受影响）
        if (_fireOnEnter) Instance._autoFireAt = Time.realtimeSinceStartup + 1.2f;
    }

    // -plateparams U,q,m,v0：给截图脚本用的参数覆盖，正常启动不传就没有副作用
    static bool _cliDone;
    void ApplyCli()
    {
        if (_cliDone) return;
        _cliDone = true;
        var argv = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "-platefire") { _fireOnEnter = true; continue; }
            if (argv[i] != "-plateparams" || i + 1 >= argv.Length) continue;
            var p = argv[i + 1].Split(',');
            if (p.Length < 4) continue;
            float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out Voltage);
            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Charge);
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out Mass);
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out Speed);
        }
    }

    public static void HideModule()
    {
        Active = false;
        if (Instance == null) return;
        if (Instance._root != null) Instance._root.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Active = false;
    }

    // ---------- 摄像机 ----------

    void FrameCamera()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        // 与 SceneBinder.FrameCamera 同样的坑：只给位置不给朝向，
        // 从大厅带过来的斜朝向会让正交画面歪掉，必须先把 rotation 归零。
        _cam.orthographic = true;
        _cam.transform.rotation = Quaternion.identity;
        _cam.transform.position = new Vector3(0.55f, 0.10f, -12f);

        float aspect = _cam.aspect <= 0f ? 1.7778f : _cam.aspect;
        // 左边到发射器（-6.3）、右边到飞出边界（8.6），留 0.7 的余量
        float halfW = 8.6f;
        float halfH = 2.6f;
        _cam.orthographicSize = Mathf.Max(halfH, halfW / aspect);
    }

    void Update()
    {
        if (!Active) return;
        if (_cam == null) _cam = Camera.main;

        // 窗口比例变了要重新取景，否则极板会被裁掉
        if (_cam != null && _cam.orthographic)
        {
            float aspect = _cam.aspect <= 0f ? 1.7778f : _cam.aspect;
            float size = Mathf.Max(2.6f, 8.6f / aspect);
            if (!Mathf.Approximately(_cam.orthographicSize, size)) _cam.orthographicSize = size;
        }

        AnimateElectrons();

        if (Input.GetKeyDown(KeyCode.Space)) Launch();
        if (Input.GetKeyDown(KeyCode.R)) ResetShot();
        if (Input.GetKeyDown(KeyCode.N)) RandomTarget();
        if (Input.GetKeyDown(KeyCode.G)) { ShowPredict = !ShowPredict; DrawTrajectory(); }

        if (_autoFireAt > 0f && Time.realtimeSinceStartup >= _autoFireAt)
        {
            _autoFireAt = 0f;
            if (!_running && _verdict < 0) Launch();
        }

        if (_running)
        {
            _elapsed += Time.deltaTime;
            if (_elapsed >= _flight.TotalTime)
            {
                _elapsed = _flight.TotalTime;
                _running = false;
                Settle();
            }
            ApplyBall();
        }
    }

    // ---------- 试射 ----------

    void Launch()
    {
        _flight = Solve();
        _elapsed = 0f;
        _running = true;
        _verdict = -1;
        _scoreTries++;
        DrawTrajectory();
        ApplyBall();
    }

    void ResetShot()
    {
        _targetX = Mathf.Clamp(_targetX, -2.0f, 2.0f);
        _flight = Solve();
        _elapsed = _flight.TotalTime;
        _running = false;
        _verdict = -1;
        DrawTrajectory();
        ApplyBall();
    }

    void RandomTarget()
    {
        _targetX = UnityEngine.Random.Range(-2.00f, 2.00f);
        _scoreHit = 0;
        _scoreTries = 0;
        _verdict = -1;
        PlaceTarget();
        ResetShot();
    }

    void Settle()
    {
        _verdict = (int)_flight.Outcome;
        if (_flight.Outcome == FlightOutcome.Bottom)
        {
            float dx = Mathf.Abs(_flight.ImpactX - _targetX);
            if (dx <= TargetHalf) _scoreHit++;
        }
        DrawTrajectory();
    }

    void ApplyBall()
    {
        if (_ball == null) return;
        var p = PositionAt(_flight, _elapsed);
        _ball.position = new Vector3(p.x, p.y, -0.45f);

        if (_trail != null)
        {
            int count = Mathf.Clamp(Mathf.RoundToInt(_flight.Points.Length * Mathf.Clamp01(_elapsed / Mathf.Max(0.02f, _flight.TotalTime))), 2, _flight.Points.Length);
            _trail.positionCount = count;
            for (int i = 0; i < count; i++)
                _trail.SetPosition(i, new Vector3(_flight.Points[i].x, _flight.Points[i].y, -0.36f));
        }
    }

    void DrawTrajectory()
    {
        if (_ghost == null || _flight.Points == null) return;
        _ghost.enabled = ShowPredict || _running;
        _ghost.positionCount = _flight.Points.Length;
        for (int i = 0; i < _flight.Points.Length; i++)
            _ghost.SetPosition(i, new Vector3(_flight.Points[i].x, _flight.Points[i].y, -0.38f));

        // 落在板上时把落点画成一个圈，省得学生自己拿尺子量
        ApplyBall();
    }

    // ---------- 几何构造 ----------

    void EnsureBuilt(Camera camera)
    {
        _cam = camera != null ? camera : Camera.main;
        if (_root != null) return;

        _root = new GameObject("plate_field_root").transform;
        _root.SetParent(transform, false);

        BuildBackdrop();
        BuildPlates();
        BuildFieldArrows();
        BuildPads();
        BuildLauncher();
        BuildElectrons();
        BuildBall();

        _targetX = 0f;
        PlaceTarget();
    }

    void BuildBackdrop()
    {
        // 相机在 z=-12 朝 +z 看：z 越大越远。背景板必须比极板更远，
        // 写成负值会把它推到所有东西前面，屏幕上只剩一块深色平板。
        Quad("plate_bg", 0.60f, 0f, 20.0f, 11.0f, new Color(0.055f, 0.075f, 0.11f, 1f), 0.60f);

        var line = Mat("grid", new Color(0.16f, 0.21f, 0.29f, 1f));
        for (int i = -9; i <= 10; i++)
            Strip("grid_v", new Vector2(i, -5.4f), new Vector2(0f, 10.8f), 0.012f, line, 0.55f);
        for (int j = -5; j <= 5; j++)
            Strip("grid_h", new Vector2(-9f, j), new Vector2(19.5f, 0f), 0.012f, line, 0.55f);
    }

    void BuildPlates()
    {
        // 上板 = 正极（红），下板 = 负极（蓝）
        Box("plate_top", new Vector3(0f, TopY + PlateThick * 0.5f, 0f),
            new Vector3(HalfSpan * 2f + 0.5f, PlateThick, PlateDepth), Glow(new Color(0.86f, 0.30f, 0.26f)));
        Box("plate_bottom", new Vector3(0f, BottomY - PlateThick * 0.5f, 0f),
            new Vector3(HalfSpan * 2f + 0.5f, PlateThick, PlateDepth), Glow(new Color(0.22f, 0.47f, 0.86f)));

        // 极性符号用线段画，不依赖字体（打包后缺字会露出方块）
        var plus = Mat("plus", new Color(0.98f, 0.62f, 0.55f, 1f));
        var minus = Mat("minus", new Color(0.62f, 0.78f, 0.98f, 1f));
        for (int i = -2; i <= 2; i++)
        {
            float cx = i * 1.35f;
            float cyTop = TopY - 0.34f;
            float cyBot = BottomY + 0.34f;
            // 「+」的横竖两笔都要以 (cx, cy) 为中心，各自从半格处起画，否则只会画出一个 L
            Strip("sign_h", new Vector2(cx - 0.15f, cyTop), new Vector2(0.30f, 0f), 0.055f, plus, -0.30f);
            Strip("sign_v", new Vector2(cx, cyTop - 0.15f), new Vector2(0f, 0.30f), 0.055f, plus, -0.30f);
            Strip("sign_m", new Vector2(cx - 0.15f, cyBot), new Vector2(0.30f, 0f), 0.055f, minus, -0.30f);
        }
    }

    void BuildFieldArrows()
    {
        // 场线箭头由 + 板指向 − 板（向下），跟电子流方向相反 —— 正是要学生看出来的那件事
        var mat = Mat("arrow", new Color(0.42f, 0.58f, 0.82f, 0.55f));
        for (int i = 0; i < 7; i++)
        {
            float x = -2.85f + i * 0.95f;
            float tip = BottomY + 0.55f;
            Strip("arrow_v", new Vector2(x, TopY - 0.55f), new Vector2(0f, -(Gap - 1.10f)), 0.022f, mat, -0.18f);
            // 两条斜线都从箭头尖出发、往上收：合成一个向下的 V 字箭头
            Strip("arrow_l", new Vector2(x, tip), new Vector2(0.20f, 0.24f), 0.022f, mat, -0.18f);
            Strip("arrow_r", new Vector2(x, tip), new Vector2(-0.20f, 0.24f), 0.022f, mat, -0.18f);
        }
    }

    void BuildPads()
    {
        // 两个固定的琥珀色「别的位置」，让学生知道靶心之外还有别的落点颜色。
        // 注意 z 要放在极板前表面（z=-0.15）之外，否则整条靶标会被极板挡掉。
        _padLeft = Quad("pad_left", -2.35f, BottomY + 0.03f, 0.85f, 0.075f, new Color(0.80f, 0.52f, 0.15f, 1f), -0.22f).transform;
        _padRight = Quad("pad_right", 2.35f, BottomY + 0.03f, 0.85f, 0.075f, new Color(0.80f, 0.52f, 0.15f, 1f), -0.22f).transform;

        _targetPad = Quad("pad_target", 0f, BottomY + 0.05f, TargetHalf * 2f, 0.10f,
            new Color(0.20f, 0.78f, 0.42f, 1f), -0.21f).transform;

        // 靶心竖线：告诉学生「打中的是这条线上」
        Strip("pad_mark", new Vector2(0f, BottomY), new Vector2(0f, 0.42f), 0.03f,
            Mat("mark", new Color(0.30f, 0.90f, 0.50f, 1f)), -0.20f);
    }

    void PlaceTarget()
    {
        if (_targetPad == null) return;
        _targetPad.position = new Vector3(_targetX, BottomY + 0.05f, -0.21f);

        var mark = _root.Find("pad_mark");
        if (mark != null) mark.position = new Vector3(_targetX, BottomY + 0.21f, -0.20f);
    }

    void BuildLauncher()
    {
        Box("launcher", new Vector3(LaunchX - 0.34f, StartY, 0f),
            new Vector3(0.68f, 0.30f, 0.30f), Glow(new Color(0.55f, 0.60f, 0.68f)));
        Strip("launcher_arrow", new Vector2(LaunchX - 0.05f, StartY), new Vector2(0.55f, 0f), 0.045f,
            Mat("larrow", new Color(0.85f, 0.88f, 0.95f, 0.8f)), -0.40f);
    }

    void BuildElectrons()
    {
        var mat = Mat("electron", new Color(0.35f, 0.85f, 0.98f, 1f));
        for (int i = 0; i < 64; i++)
        {
            var e = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            e.name = "electron_" + i;
            var col = e.GetComponent<Collider>();
            if (col != null) Destroy(col);
            e.transform.SetParent(_root, false);
            e.transform.localScale = Vector3.one * 0.055f;
            e.GetComponent<Renderer>().sharedMaterial = mat;
            _electrons.Add(e.transform);
        }
    }

    void AnimateElectrons()
    {
        // 电子流要「看得出在流」，不能是一堆随机乱点。
        // 做法：60 个电子分成 6 列（列间距和场线箭头错开半格，免得看着像沿着箭头走），
        // 每列 10 个按固定相位差从下板往上板排成一串，整列一起匀速上移。
        _electronPhase += Time.deltaTime * 0.36f;

        const int columns = 6;
        for (int i = 0; i < _electrons.Count; i++)
        {
            int col = i % columns;
            int lane = i / columns;

            float x = -2.375f + col * 0.95f;             // 箭头在 -2.85 + n*0.95，这里错开半格
            float u = Mathf.Repeat(lane * 0.105f + _electronPhase, 1f);
            float y = BottomY + 0.08f + u * (Gap - 0.16f);
            // 两头淡出：电子不该在下板「长出来」、也不该穿过上板
            float fade = Mathf.Sin(u * Mathf.PI);

            _electrons[i].position = new Vector3(x, y, -0.25f);
            _electrons[i].localScale = Vector3.one * (0.028f + 0.040f * fade);
        }
    }

    void BuildBall()
    {
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "plate_ball";
        var col = ball.GetComponent<Collider>();
        if (col != null) Destroy(col);
        ball.transform.SetParent(_root, false);
        ball.transform.localScale = Vector3.one * (BallRadius * 2f);
        ball.GetComponent<Renderer>().sharedMaterial = Mat("ball", new Color(1.00f, 0.76f, 0.28f, 1f));
        _ball = ball.transform;

        var ghost = new GameObject("trail_ghost");
        ghost.transform.SetParent(_root, false);
        _ghost = ghost.AddComponent<LineRenderer>();
        // 0.16 在显示器上看得见，投到教室幕布上就基本没了；提到 0.26
        SetupLine(_ghost, new Color(1f, 1f, 1f, 0.26f), 0.034f);

        var trail = new GameObject("trail_live");
        trail.transform.SetParent(_root, false);
        _trail = trail.AddComponent<LineRenderer>();
        SetupLine(_trail, new Color(1f, 0.80f, 0.35f, 0.95f), 0.055f);
    }

    void SetupLine(LineRenderer line, Color color, float width)
    {
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = 4;
        line.material = LineMat();
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
    }

    // ---------- 场景小工具 ----------

    Material Glow(Color color)
    {
        var shader = Resources.Load<Shader>("Shaders/Glow");
        if (shader == null) shader = Shader.Find("ChargeLab/Glow");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        var m = new Material(shader) { name = "plate_glow" };
        m.SetColor("_Color", color);
        m.SetFloat("_Shade", 0.22f);
        return m;
    }

    Material LineMat()
    {
        var shader = Resources.Load<Shader>("Shaders/LineColor");
        if (shader == null) shader = Shader.Find("ChargeLab/LineColor");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        return new Material(shader) { name = "plate_line" };
    }

    Material Mat(string key, Color color)
    {
        Material m;
        if (_mats.TryGetValue(key, out m) && m != null) return m;
        m = Glow(color);
        _mats[key] = m;
        return m;
    }

    GameObject Box(string name, Vector3 pos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(_root, false);
        go.transform.position = pos;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    GameObject Quad(string name, float x, float y, float w, float h, Color color, float z)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(x, y, z);
        go.transform.localScale = new Vector3(w, h, 1f);
        var mat = Glow(color);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    // 用一条细长方块代替 LineRenderer：网格线有上百条，LineRenderer 每条一个 draw call 太浪费
    void Strip(string name, Vector2 center, Vector2 dir, float width, Material mat, float z)
    {
        float len = dir.magnitude;
        if (len < 0.0001f) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(center.x + dir.x * 0.5f, center.y + dir.y * 0.5f, z);
        go.transform.localScale = new Vector3(len, width, 0.02f);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ---------- OnGUI：参数表 ----------

    void OnGUI()
    {
        if (!Active) return;
        GUI.depth = -10;
        EnsureStyles();

        var size = UIScale.BeginGUI();
        float W = size.x, H = size.y;

        DrawLegend(40f, H - 210f);
        DrawBanner(W, H);
        DrawPanel(W, 40f);
    }

    void DrawPanel(float W, float top)
    {
        float w = 560f, x = W - w - 40f, y = top;
        if (x < 40f) { w = Mathf.Min(560f, W - 80f); x = 40f; }

        var rect = new Rect(x, y, w, 528f);
        UIScale.Card(rect);

        _title.normal.textColor = UITheme.TextPrimary;
        GUI.Label(new Rect(x + 20f, y + 16f, w - 40f, 34f), UILang.T("plate.title"), _title);
        GUI.Label(new Rect(x + 20f, y + 52f, w - 40f, 26f), UILang.T("plate.subtitle"), _muted);

        float cy = y + 92f;
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("plate.u"), ref Voltage, 0f, 80f, "0");
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("plate.q"), ref Charge, -3f, 3f, "+0.0;-0.0");
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("plate.m"), ref Mass, 0.5f, 5f, "0.0");
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("plate.v0"), ref Speed, 1.5f, 10f, "0.0");

        cy += 6f;
        UIScale.Fill(new Rect(x + 20f, cy, w - 40f, 1f), UITheme.Divider);
        cy += 10f;

        string accel = _flight.Acceleration.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        string field = _flight.Field.ToString("0.00", CultureInfo.InvariantCulture);
        string pred = _flight.Outcome == FlightOutcome.Bottom || _flight.Outcome == FlightOutcome.Top
            ? _flight.ImpactX.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)
            : UILang.T("plate.escapedShort");

        _value.normal.textColor = UITheme.TextPrimary;
        GUI.Label(new Rect(x + 20f, cy, w - 40f, 28f),
            UILang.F("plate.formula", field, accel), _text);
        cy += 30f;
        GUI.Label(new Rect(x + 20f, cy, w - 40f, 28f), UILang.F("plate.predicted", pred), _value);
        cy += 34f;

        GUI.Label(new Rect(x + 20f, cy, w - 40f, 26f),
            UILang.F("plate.score", _scoreHit, _scoreTries), _muted);
        cy += 34f;

        if (GUI.Button(new Rect(x + 20f, cy, 200f, 44f), UILang.T("plate.launch"), _btn)) Launch();
        if (GUI.Button(new Rect(x + 232f, cy, 150f, 44f), UILang.T("plate.reset"), _btn)) ResetShot();
        if (GUI.Button(new Rect(x + 394f, cy, 146f, 44f), UILang.T("plate.newTarget"), _btn)) RandomTarget();

        cy += 54f;
        GUI.Label(new Rect(x + 20f, cy, w - 40f, 52f), UILang.T("plate.hint"), _muted);

        // 返回按钮放在面板里而不是沿用大厅那个：大厅的返回按钮压在左下角，
        // 正好和图例重叠，而且它的绘制被 PlateFieldModule.Active 关掉了。
        cy += 54f;
        if (GUI.Button(new Rect(x + 20f, cy, w - 40f, 40f), UILang.T("hub.back"), _btn)) BackToHub();
    }

    void BackToHub()
    {
        var hub = FindObjectOfType<ChargeLabHub>();
        if (hub != null) { hub.ReturnToHub(); return; }
        // 兜底：没有大厅（例如 -nohub 单独起这个模块）就直接收起来
        Active = false;
        if (_root != null) _root.gameObject.SetActive(false);
    }

    float Slider(float x, float y, float w, string label, ref float value, float min, float max, string fmt)
    {
        GUI.Label(new Rect(x, y, 210f, 26f), label, _text);
        float v = GUI.HorizontalSlider(new Rect(x + 210f, y + 9f, w - 320f, 22f), value, min, max);
        if (!Mathf.Approximately(v, value))
        {
            value = v;
            // 拖参数时立刻重算预测轨迹：学生要看到「改一个数，落点就变」，不能等按发射
            _flight = Solve();
            _elapsed = _flight.TotalTime;
            _verdict = -1;
            DrawTrajectory();
        }
        GUI.Label(new Rect(x + w - 100f, y, 100f, 26f), value.ToString(fmt, CultureInfo.InvariantCulture), _value);
        return y + 40f;
    }

    void DrawLegend(float x, float y)
    {
        var rect = new Rect(x, y, 400f, 168f);
        UIScale.Card(rect);
        _title.normal.textColor = UITheme.TextPrimary;
        GUI.Label(new Rect(x + 18f, y + 12f, 360f, 30f), UILang.T("plate.legend.title"), _title);

        float ly = y + 50f;
        LegendRow(x + 18f, ly, new Color(0.86f, 0.30f, 0.26f), UILang.T("plate.legend.top")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.22f, 0.47f, 0.86f), UILang.T("plate.legend.bottom")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.35f, 0.85f, 0.98f), UILang.T("plate.legend.flow")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.20f, 0.78f, 0.42f), UILang.T("plate.legend.target"));
    }

    void LegendRow(float x, float y, Color color, string text)
    {
        UIScale.Fill(new Rect(x, y + 6f, 16f, 10f), color);
        GUI.Label(new Rect(x + 26f, y, 340f, 26f), text, _text);
    }

    void DrawBanner(float W, float H)
    {
        if (_running || _verdict < 0) return;

        string title, sub;
        Color color;
        var outcome = (FlightOutcome)_verdict;
        float dist = Mathf.Abs(_flight.ImpactX - _targetX);
        string distStr = dist.ToString("0.00", CultureInfo.InvariantCulture);

        if (outcome == FlightOutcome.Bottom)
        {
            if (dist <= TargetHalf)
            {
                title = UILang.T("plate.judge.hit");
                sub = UILang.F("plate.judge.hit.sub", _scoreHit, _scoreTries);
                color = new Color(0.24f, 0.84f, 0.48f);
            }
            else if (dist <= AmberHalf)
            {
                title = UILang.F("plate.judge.near", distStr);
                sub = UILang.T("plate.judge.near.sub");
                color = new Color(0.95f, 0.70f, 0.25f);
            }
            else
            {
                bool right = _flight.ImpactX > _targetX;
                title = UILang.F("plate.judge.miss", right ? UILang.T("plate.right") : UILang.T("plate.left"), distStr);
                // 建议必须按方向给：偏右是「太快没掉下来」，偏左是「偏转过头」，两句话正好相反
                sub = UILang.T(right ? "plate.judge.miss.subR" : "plate.judge.miss.subL");
                color = new Color(0.93f, 0.42f, 0.36f);
            }
        }
        else if (outcome == FlightOutcome.Top)
        {
            title = UILang.T("plate.judge.top");
            sub = UILang.T("plate.judge.top.sub");
            color = new Color(0.93f, 0.42f, 0.36f);
        }
        else
        {
            title = UILang.T("plate.judge.escape");
            sub = UILang.T("plate.judge.escape.sub");
            color = new Color(0.93f, 0.42f, 0.36f);
        }

        // 放左上角：右上角是参数表，居中会和它压在一起（1366 宽以下必定重叠）。
        // 卡片高度按副行实测高度走 —— 中文一行、英文两行时都不会被裁掉。
        const float cardW = 660f;
        float innerW = cardW - 48f;
        _bannerSub.normal.textColor = UITheme.TextPrimary;
        float subH = Mathf.Max(28f, _bannerSub.CalcHeight(new GUIContent(sub), innerW));

        // 标题太长就自动缩号（英文「1.58 m right of centre — off target」在 30pt 会溢出）
        _banner.fontSize = 30;
        while (_banner.fontSize > 20 && _banner.CalcSize(new GUIContent(title)).x > innerW)
            _banner.fontSize -= 2;

        float cardH = 52f + subH + 14f;
        var rect = new Rect(40f, 40f, cardW, cardH);
        UIScale.Card(rect);
        UIScale.Fill(new Rect(rect.x, rect.y, 7f, rect.height), color);
        _banner.normal.textColor = color;
        GUI.Label(new Rect(rect.x + 26f, rect.y + 6f, innerW, 44f), title, _banner);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 52f, innerW, subH), sub, _bannerSub);
    }

    void EnsureStyles()
    {
        if (_styles) return;
        _styles = true;

        _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _text = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.UpperLeft };
        _text.normal.textColor = UITheme.TextPrimary;
        _muted = new GUIStyle(_text) { fontSize = 17, wordWrap = true };
        _muted.normal.textColor = UITheme.TextMuted;
        _value = new GUIStyle(_text) { fontSize = 20, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _btn = new GUIStyle(GUI.skin.button) { fontSize = 19 };
        _banner = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _bannerSub = new GUIStyle(_text) { fontSize = 19, wordWrap = true, alignment = TextAnchor.UpperLeft };
        _bannerSub.normal.textColor = UITheme.TextPrimary;
    }
}
