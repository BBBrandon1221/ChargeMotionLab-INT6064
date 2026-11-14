using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// 第四站：粒子导航员 —— 把带电粒子送进接收器（2D）
//
// 和前三站同源：匀强磁场 B = Bz·ẑ，洛伦兹力只改方向、不做功，速率全程不变，
// 轨迹是精确的圆周（r = mv/(|q|B)）。单步推进直接复用 SimulationSolver.BorisStep，
// 所以这里画出来的弧线与九个磁场算例、参数面板重算用的是同一个积分器。
//
// 和第四站（平行板电场，PlateFieldModule）刻意形成对照：
//   电场会做功、速率一路变；磁场不做功、速率恒定 —— 这是本关要学生亲眼看出来的。
//
// 三关递进（对应课程 PPT 的「第四台挑战」）：
//   第一关 选方向：速度与发射角锁死，只让学生选磁场向里(⊗)还是向外(⊙)。
//                 两边的弧线互为镜像，只有正确的一面会扫到接收器。
//   第二关 调半径：方向锁死，只能改初速 v 或磁场强度 B。目标是把半径调到
//                 刚好扫过更窄的接收器（判定位收窄，对应「第一关基础上缩小范围」）。
//   第三关 路径规划：场地沿 x 分成三段「拼接磁场」（方向交替、强度不同），学生调初速 v 与
//                 发射角，让粒子依次穿过 ①②③ 三段，最后落入底部边缘的接收器；命中后可换解。
//
// 预测后发射：灰色线永远画着「按当前参数会怎么飞」，老师可按 G 关掉让学生先猜。
// 前后轨迹对比：上一次的弧线用细彩线留着（绿=上次命中，红=上次失败），一发一对比。
//
// 退出时只关根节点、清运行态；静态 Active 立刻归 false，重进就是全新的一局。
public sealed class ParticleNavigatorModule : MonoBehaviour
{
    public static ParticleNavigatorModule Instance { get; private set; }
    public static bool Active { get; private set; }

    // ---------- 场地几何（全部躺 z=0 平面）----------
    const float Left = -9f, Right = 9f, Bottom = -5f, Top = 5f;
    const float WallThick = 0.18f;
    const float LaunchX = -8f, LaunchY = 0f;
    const float BallRadius = 0.16f;

    // 粒子固定为 +1 电荷、1 质量，半径 r = v/|B|，学生只需理解「v 越大弧越大、B 越大弧越紧」。
    const float Mass = 1f, Charge = 1f;

    // 接收器半宽：第一关宽、第二关窄、第三关中等
    const float WideHalf = 0.80f;
    const float NarrowHalf = 0.28f;
    const float MediumHalf = 0.45f;

    // 第三关（路径规划）：场地沿 x 分成三段拼接磁场。bz 的符号=方向(+ 向外⊙ / - 向里⊗)，
    // 大小=强度。三段方向交替、强度不同，粒子每穿过一段都会拐一次，最后落入底部墙壁的接收器。
    // 分界放在 x=-5 与 x=-1（都在左侧可见区，避开右侧 UI 面板）。
    const float BandLeft = -5f, BandRight = -1f;
    const float RegionBz0 = 1.1f, RegionBz1 = -1.0f, RegionBz2 = 1.2f;
    const float PathHalf = 0.50f;        // 第三关接收器半宽（沿底壁的 x 范围）

    // 墙体编号
    const int WallBottom = 0, WallTop = 1, WallLeft = 2, WallRight = 3;

    // 积分步长：0.004 在 v∈[2,7] 时每步 ≤3cm，撞墙插值后误差 <2cm
    const float StepDt = 0.004f;

    // ---------- 学生可改的参数 ----------
    public int Level = 1;
    public float Speed = 3f;        // v
    public float BField = 1f;       // |B|
    public bool FieldOut = false;   // true = ⊙ 垂直纸面向外；false = ⊗ 向里
    public float AimDeg = 0f;       // 第三关的发射角（第一/二关锁 0°）
    public bool ShowPredict = true;
    public bool ShowCompare = true;

    // ---------- 接收器 ----------
    int _targetWall = WallBottom;
    float _targetX = -5.76f;
    float _receiverHalf = WideHalf;

    // ---------- 试射状态 ----------
    enum Verdict { None, Running, Hit, MissSameWall, MissWrongWall, Orbit, MissRoute }

    Flight _preview;                 // 当前参数下的预测
    Flight _flight;                  // 正在播放 / 刚结束的一发
    Flight _prevFlight;              // 上一发（用来画对比线）
    Flight _lastFlight;              // 最近结束的一发
    bool _hasPrev, _hasLast;
    bool _prevHit, _lastHit;

    bool _running;
    float _elapsed;
    Verdict _verdict = Verdict.None;
    int _hits, _tries;

    static bool _fireOnEnter;
    float _autoFireAt;

    // ---------- 场景对象 ----------
    Camera _cam;
    Transform _root;
    Transform _ball;
    Transform _receiverBar;
    LineRenderer _receiverRing, _impactRing;
    LineRenderer _ghost, _live, _prevLine;
    Transform _fieldOutRoot, _fieldInRoot;
    Transform[] _regionRoots;        // 第三关：三段各自的磁场符号组
    Transform _regionEdgeRoot;       // 第三关：段与段之间的分界线
    LineRenderer[] _checkpoints;     // 第三关：三个「按顺序经过」的检查点环

    struct WorldLabel { public Vector3 pos; public string text; public int size; public Color color; public bool pathOnly; }
    readonly List<WorldLabel> _worldLabels = new List<WorldLabel>();

    readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
    Material _lineMat;
    GUIStyle _title, _text, _muted, _value, _btn, _banner, _bannerSub, _objective;
    bool _styles;

    // ---------- 轨迹数据 ----------

    struct Flight
    {
        public Vector2[] Points;
        public float Dt;
        public int HitWall;        // -1 = 一段时间内没碰到墙
        public Vector2 Impact;
        public float TotalTime;
        public float MaxX;         // 第三关：整条轨迹到达过的最大 x（判断是否穿到第三块）
        public bool Valid;
    }

    // 用现有 Boris 积分器走一条弧线，直到碰到场地墙（或走满两圈）。
    // 不在每步调用 Solve()：一次 Solve 会生成几千帧对象，拖滑块时会明显掉帧。
    Flight ComputeFlight()
    {
        bool pathMode = Level >= 3;
        float bz = (FieldOut ? 1f : -1f) * Mathf.Max(0.05f, BField);
        float theta = (Level >= 3 ? AimDeg : 0f) * Mathf.Deg2Rad;
        float v = Mathf.Max(0.2f, Speed);

        double x = LaunchX, y = LaunchY;
        double vx = v * Mathf.Cos(theta);
        double vy = v * Mathf.Sin(theta);
        double maxX = x;   // 记录轨迹能到达的最大 x

        // 最多走两圈：小半径时圆整个缩在场内，两圈足够让观众看清「一直在打转」
        // 第三关要横穿整块场地穿过三段磁场，给足 15 秒。
        float maxTime = pathMode
            ? 15f
            : Mathf.Min(7f, 2f * Mathf.PI / Mathf.Max(0.10f, Mathf.Abs(bz)));
        int maxSteps = Mathf.CeilToInt(maxTime / StepDt);

        var pts = new List<Vector2>(maxSteps + 2) { new Vector2((float)x, (float)y) };
        int hitWall = -1;
        Vector2 impact = Vector2.zero;
        float totalTime = 0f;

        for (int i = 0; i < maxSteps; i++)
        {
            double px = x, py = y;
            float bzStep = pathMode ? RegionFieldAt(x) : bz;
            SimulationSolver.BorisStep(Charge, Mass, bzStep, StepDt, ref x, ref y, ref vx, ref vy);
            if (x > maxX) maxX = x;

            int side; float frac;
            if (WallCrossing(px, py, x, y, out side, out frac))
            {
                float hx = (float)(px + (x - px) * frac);
                float hy = (float)(py + (y - py) * frac);
                if (hx > maxX) maxX = hx;
                pts.Add(new Vector2(hx, hy));
                impact = new Vector2(hx, hy);
                hitWall = side;
                totalTime = (i + frac) * StepDt;
                break;
            }

            pts.Add(new Vector2((float)x, (float)y));
            totalTime = (i + 1) * StepDt;
        }

        return new Flight
        {
            Points = pts.ToArray(),
            Dt = StepDt,
            HitWall = hitWall,
            Impact = impact,
            TotalTime = totalTime,
            MaxX = (float)maxX,
            Valid = true,
        };
    }

    // 第三关：取粒子当前所处那一段拼接磁场的 bz。
    static float RegionFieldAt(double x)
    {
        if (x < BandLeft) return RegionBz0;
        if (x < BandRight) return RegionBz1;
        return RegionBz2;
    }

    // 线段 (x0,y0)-(x1,y1) 是否穿过四条边界中的一条；返回最早的那次及插值比例。
    static bool WallCrossing(double x0, double y0, double x1, double y1, out int side, out float frac)
    {
        side = -1;
        frac = float.MaxValue;

        TryWall(ref side, ref frac, y0, y1, Bottom, x0, x1, WallBottom, true);
        TryWall(ref side, ref frac, y0, y1, Top, x0, x1, WallTop, false);
        TryWall(ref side, ref frac, x0, x1, Left, y0, y1, WallLeft, true);
        TryWall(ref side, ref frac, x0, x1, Right, y0, y1, WallRight, false);

        if (side < 0) return false;
        if (frac < 0f) frac = 0f;
        if (frac > 1f) frac = 1f;
        return true;
    }

    // pos/end 是沿运动方向的坐标；wallCoord 是墙所在的固定坐标；across* 用来确认交点落在墙段内。
    static void TryWall(ref int side, ref float frac, double pos, double end, float wallCoord,
        double acrossStart, double acrossEnd, int wallId, bool decreasing)
    {
        bool crosses = decreasing ? (pos > wallCoord && end <= wallCoord)
                                  : (pos < wallCoord && end >= wallCoord);
        if (!crosses) return;

        double denom = end - pos;
        if (Math.Abs(denom) < 1e-12) return;
        double f = (wallCoord - pos) / denom;
        if (f < 0.0 || f > 1.0) return;

        // 交点要在墙段以内；场地是矩形、起点在内部，第一次出界必然落在某条墙段上。
        double across = acrossStart + (acrossEnd - acrossStart) * f;
        float lo = wallId == WallBottom || wallId == WallTop ? Left : Bottom;
        float hi = wallId == WallBottom || wallId == WallTop ? Right : Top;
        if (across < lo - 1e-6 || across > hi + 1e-6) return;

        if ((float)f < frac)
        {
            frac = (float)f;
            side = wallId;
        }
    }

    // ---------- 命中判定 ----------

    float Along(int wall, Vector2 p)
    {
        return (wall == WallBottom || wall == WallTop) ? p.x : p.y;
    }

    Verdict Evaluate(Flight f, out float missDistance)
    {
        missDistance = -1f;
        if (!f.Valid || f.HitWall < 0) return Verdict.Orbit;

        // 第三关：「按顺序穿过每一块磁场」——若整条轨迹都没进到第 3 块，
        // 就算打到了某面墙，也按「路线不完整」处理，提示学生要依次穿过 1→2→3。
        if (Level >= 3 && f.MaxX <= BandRight + 0.01f) return Verdict.MissRoute;

        if (f.HitWall != _targetWall) return Verdict.MissWrongWall;

        missDistance = Mathf.Abs(Along(f.HitWall, f.Impact) - _targetX);
        return missDistance <= _receiverHalf ? Verdict.Hit : Verdict.MissSameWall;
    }

    // ---------- 关卡配置 ----------

    void ConfigureLevel(int level)
    {
        Level = Mathf.Clamp(level, 1, 3);
        _hits = 0;
        _tries = 0;
        _verdict = Verdict.None;
        _running = false;
        _hasPrev = false;
        _hasLast = false;

        if (Level == 1)
        {
            // 速度、角度、|B| 全锁死；默认磁场「向里」是错的 —— 第一次发射必定打在另一面墙上，
            // 学生才会主动去点「向外」。这正是「先失败、再修正」的设计。
            Speed = 3f;
            BField = 1f;
            AimDeg = 0f;
            FieldOut = false;
            _targetWall = WallBottom;
            _receiverHalf = WideHalf;
            _targetX = ImpactXFor(3f);
        }
        else if (Level == 2)
        {
            // 方向已对，问题在半径：v=3 落在 -5.76，接收器在 -4.13，必须把 v 调到 4
            // （或把 B 调到 0.75）才能进窄区。
            Speed = 3f;
            BField = 1f;
            AimDeg = 0f;
            FieldOut = true;
            _targetWall = WallBottom;
            _receiverHalf = NarrowHalf;
            _targetX = ImpactXFor(4f);
        }
        else
        {
            AimDeg = 0f;
            _receiverHalf = MediumHalf;
            RandomizeLevel3();
        }

        if (_root == null) EnsureBuilt(_cam);
        PlaceReceiver();
        UpdateFieldVisual();
        RecomputePreview();
        DrawPrev();
    }

    // 第三关（路径规划）：三段拼接磁场固定；接收器是「底部墙壁」随机一个 x，
    // 学生要设计路线依次穿过 ①②③ 三段，最后落入底部接收器。
    void RandomizeLevel3()
    {
        _targetWall = WallBottom;
        _receiverHalf = PathHalf;
        _targetX = UnityEngine.Random.Range(-0.8f, 0.7f);   // 底壁上的目标 x，[-0.8,0.7] 内都有解
        Speed = 2.6f;   // 故意偏小：默认会先撞左壁，逼学生自己规划路线
        AimDeg = 0f;
        BField = 1f;
        FieldOut = true;
    }

    // 发射角 0°、磁场向外时，半径 r 的弧线打在 y=-5 的落点 x。
    // 圆：(x+8)² + (y+r)² = r²，代入 y=-5 → (x+8)² = 10r-25。
    static float ImpactXFor(float r)
    {
        float inside = 10f * r - 25f;
        if (inside <= 0f) return Left;   // 半径太小，圆碰不到下壁
        return -8f + Mathf.Sqrt(inside);
    }

    // ---------- 入口 / 退出 ----------

    public static void ShowModule(Camera camera)
    {
        if (Instance == null)
        {
            var go = new GameObject("ParticleNavigatorModule");
            Instance = go.AddComponent<ParticleNavigatorModule>();
        }

        Instance.EnsureBuilt(camera);
        Instance._root.gameObject.SetActive(true);
        Active = true;
        Instance.FrameCamera();
        Instance.ResetStateFromCli();
        if (_fireOnEnter) Instance._autoFireAt = Time.realtimeSinceStartup + 1.2f;
    }

    public static void HideModule()
    {
        Active = false;
        if (Instance == null) return;
        Instance._running = false;
        Instance._autoFireAt = 0f;
        if (Instance._root != null) Instance._root.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Active = false;
    }

    // ---------- 命令行覆盖（出验收图 / 演示用）----------
    void ResetStateFromCli()
    {
        int level = 1;
        float? speed = null, bmag = null, angle = null, target = null;
        bool? outField = null;
        _fireOnEnter = false;

        var argv = Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "-navfire") { _fireOnEnter = true; continue; }
            if (i + 1 >= argv.Length) continue;

            switch (argv[i])
            {
                case "-navlevel":
                    int.TryParse(argv[i + 1], out level);
                    break;

                case "-navparams":
                    var p = argv[i + 1].Split(',');
                    float tmp;
                    if (p.Length >= 1 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) speed = tmp;
                    if (p.Length >= 2 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) bmag = tmp;
                    if (p.Length >= 3) outField = p[2].Trim() == "1" || p[2].Trim().ToLowerInvariant() == "out";
                    if (p.Length >= 4 && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp)) angle = tmp;
                    break;

                case "-navtarget":
                    float tx;
                    if (float.TryParse(argv[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out tx)) target = tx;
                    break;
            }
        }

        ConfigureLevel(level);
        if (speed.HasValue) Speed = speed.Value;
        if (bmag.HasValue) BField = bmag.Value;
        if (outField.HasValue) FieldOut = outField.Value;
        if (angle.HasValue) AimDeg = angle.Value;
        if (target.HasValue) _targetX = target.Value;

        _hits = 0;
        _tries = 0;
        RecomputePreview();
        PlaceReceiver();
        UpdateFieldVisual();
    }

    // ---------- 相机 ----------

    void FrameCamera()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        // 和 SceneBinder / PlateFieldModule 同样的坑：只给位置不给朝向，
        // 从大厅带过来的斜朝向会让正交画面歪掉，必须先把 rotation 归零。
        _cam.orthographic = true;
        _cam.transform.rotation = Quaternion.identity;
        _cam.transform.position = new Vector3(-1.2f, 0f, -12f);

        float aspect = _cam.aspect <= 0f ? 1.7778f : _cam.aspect;
        _cam.orthographicSize = Mathf.Max(5.7f, 9.9f / aspect);
    }

    void Reframe()
    {
        if (_cam == null || !_cam.orthographic) return;
        float aspect = _cam.aspect <= 0f ? 1.7778f : _cam.aspect;
        float size = Mathf.Max(5.7f, 9.9f / aspect);
        if (!Mathf.Approximately(_cam.orthographicSize, size)) _cam.orthographicSize = size;
    }

    // ---------- 帧循环 ----------

    void Update()
    {
        if (!Active) return;
        if (_cam == null) _cam = Camera.main;
        Reframe();

        if (Input.GetKeyDown(KeyCode.Space)) Launch();
        if (Input.GetKeyDown(KeyCode.R)) ResetShot();
        if (Input.GetKeyDown(KeyCode.N) && Level == 3) NewTarget();
        if (Input.GetKeyDown(KeyCode.G)) { ShowPredict = !ShowPredict; DrawGhost(); }
        if (Input.GetKeyDown(KeyCode.V)) { ShowCompare = !ShowCompare; DrawPrev(); }
        if (Input.GetKeyDown(KeyCode.Alpha1)) ConfigureLevel(1);
        if (Input.GetKeyDown(KeyCode.Alpha2)) ConfigureLevel(2);
        if (Input.GetKeyDown(KeyCode.Alpha3)) ConfigureLevel(3);
        if (Input.GetKeyDown(KeyCode.B) && Level == 1)
        {
            FieldOut = !FieldOut;
            OnParamChanged();
        }

        if (_autoFireAt > 0f && Time.realtimeSinceStartup >= _autoFireAt)
        {
            _autoFireAt = 0f;
            if (!_running && _verdict != Verdict.Hit) Launch();
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
            ApplyBall(_elapsed);
            if (Level >= 3) LightCheckpoints(SamplePosition(_flight, _elapsed).x);
        }
    }

    // ---------- 试射 ----------

    void OnParamChanged()
    {
        Speed = Mathf.Clamp(Speed, 2f, 7f);
        BField = Mathf.Clamp(BField, 0.6f, 1.6f);
        AimDeg = Mathf.Clamp(AimDeg, -30f, 30f);
        UpdateFieldVisual();      // 第一关切换方向时，场里的 ⊙/⊗ 也要跟着换
        RecomputePreview();
        DrawPrev();
    }

    void RecomputePreview()
    {
        _preview = ComputeFlight();
        _flight = _preview;
        _elapsed = 0f;
        _running = false;
        _verdict = Verdict.None;

        if (_live != null) _live.positionCount = 0;
        if (_impactRing != null) _impactRing.enabled = false;

        ApplyBall(0f);
        DrawGhost();
    }

    void Launch()
    {
        // 上一次的轨迹在发射这一刻冻结成「对比线」：新的一发跑起来时，
        // 观众能同时看到旧弧线（细彩线）和新弧线（黄色）的差别。
        if (_hasLast)
        {
            _prevFlight = _lastFlight;
            _prevHit = _lastHit;
            _hasPrev = true;
        }

        _flight = _preview.Valid ? _preview : ComputeFlight();
        _elapsed = 0f;
        _running = true;
        _verdict = Verdict.Running;
        _tries++;

        if (_impactRing != null) _impactRing.enabled = false;
        ApplyBall(0f);
        DrawGhost();
        DrawPrev();
    }

    void ResetShot()
    {
        RecomputePreview();
        DrawPrev();
    }

    void NewTarget()
    {
        RandomizeLevel3();
        PlaceReceiver();
        UpdateFieldVisual();
        RecomputePreview();
        DrawPrev();
    }

    void Settle()
    {
        float dist;
        _verdict = Evaluate(_flight, out dist);

        _lastFlight = _flight;
        _lastHit = _verdict == Verdict.Hit;
        _hasLast = true;
        if (_lastHit) _hits++;

        UpdateImpactMarker();
        DrawPrev();
    }

    // ---------- 球 / 轨迹的显示 ----------

    static Vector2 SamplePosition(Flight f, float t)
    {
        if (f.Points == null || f.Points.Length == 0) return new Vector2(LaunchX, LaunchY);
        if (t <= 0f) return f.Points[0];

        int idx = Mathf.Clamp(Mathf.FloorToInt(t / Mathf.Max(0.0001f, f.Dt)), 0, f.Points.Length - 1);
        if (idx >= f.Points.Length - 1) return f.Points[f.Points.Length - 1];

        float frac = Mathf.Clamp01((t - idx * f.Dt) / Mathf.Max(0.0001f, f.Dt));
        return Vector2.Lerp(f.Points[idx], f.Points[idx + 1], frac);
    }

    void ApplyBall(float t)
    {
        if (_ball == null || _flight.Points == null) return;

        var p = SamplePosition(_flight, t);
        _ball.position = new Vector3(p.x, p.y, -0.45f);

        if (_live == null) return;
        int count = Mathf.Clamp(Mathf.CeilToInt(t / Mathf.Max(0.0001f, _flight.Dt)) + 1,
            1, _flight.Points.Length);
        _live.positionCount = count;
        for (int i = 0; i < count; i++)
            _live.SetPosition(i, new Vector3(_flight.Points[i].x, _flight.Points[i].y, -0.36f));
    }

    void DrawGhost()
    {
        if (_ghost == null) return;

        bool show = ShowPredict && _preview.Valid && _preview.Points != null;
        _ghost.enabled = show;
        UpdateCheckpoints();
        if (!show) return;

        _ghost.positionCount = _preview.Points.Length;
        for (int i = 0; i < _preview.Points.Length; i++)
            _ghost.SetPosition(i, new Vector3(_preview.Points[i].x, _preview.Points[i].y, -0.38f));
    }

    // 第三关：预测轨迹每横穿到下一段，就把对应的检查点环点亮，提示「已/将经过这一段」。
    void UpdateCheckpoints()
    {
        if (_checkpoints == null) return;

        float maxX = -1000f;
        if (_preview.Valid && _preview.Points != null)
            foreach (var p in _preview.Points)
                if (p.x > maxX) maxX = p.x;

        LightCheckpoints(maxX);
    }

    // reachX = 粒子此刻已到达过的最大 x。段1始终亮；越过 BandLeft 亮段2；越过 BandRight 亮段3。
    // 飞行过程中用真实 x 调用它，就能看到检查点按 1→2→3 依次点亮。
    void LightCheckpoints(float reachX)
    {
        if (_checkpoints == null) return;
        float[] thresh = { -1000f, BandLeft, BandRight };
        for (int b = 0; b < _checkpoints.Length; b++)
        {
            var cp = _checkpoints[b];
            if (cp == null) continue;
            bool lit = reachX >= thresh[b] - 0.01f;
            var col = lit
                ? new Color(0.30f, 0.95f, 0.55f, 0.95f)
                : new Color(0.50f, 0.58f, 0.68f, 0.60f);
            cp.startColor = col;
            cp.endColor = col;
        }
    }

    void DrawPrev()
    {
        if (_prevLine == null) return;

        bool show = ShowCompare && _hasPrev && _prevFlight.Points != null;
        _prevLine.enabled = show;
        if (!show) return;

        _prevLine.positionCount = _prevFlight.Points.Length;
        for (int i = 0; i < _prevFlight.Points.Length; i++)
            _prevLine.SetPosition(i, new Vector3(_prevFlight.Points[i].x, _prevFlight.Points[i].y, -0.40f));

        var color = _prevHit
            ? new Color(0.24f, 0.84f, 0.48f, 0.85f)
            : new Color(0.95f, 0.42f, 0.34f, 0.85f);
        _prevLine.startColor = color;
        _prevLine.endColor = color;
    }

    void UpdateImpactMarker()
    {
        if (_impactRing == null) return;
        if (_flight.HitWall < 0)
        {
            _impactRing.enabled = false;
            return;
        }

        _impactRing.enabled = true;
        _impactRing.transform.position = new Vector3(_flight.Impact.x, _flight.Impact.y, -0.23f);
        var color = _verdict == Verdict.Hit
            ? new Color(0.30f, 0.95f, 0.55f, 0.95f)
            : new Color(0.98f, 0.50f, 0.40f, 0.95f);
        _impactRing.startColor = color;
        _impactRing.endColor = color;
    }

    // ---------- 场景构造 ----------

    void EnsureBuilt(Camera camera)
    {
        _cam = camera != null ? camera : Camera.main;
        if (_root != null) return;

        _root = new GameObject("particle_navigator_root").transform;
        _root.SetParent(transform, false);

        BuildBackdrop();
        BuildArena();
        BuildFieldSymbols();
        BuildPathRegions();
        BuildTicks();
        BuildLauncher();
        BuildReceiver();
        BuildBall();
        BuildLines();
        UpdateFieldVisual();
    }

    void BuildBackdrop()
    {
        // 相机在 z=-12 朝 +z 看：z 越大越远。背景必须比场地更远，否则整屏只剩一块深板。
        // 尺寸取大一些：窗口比例极端（超宽 / 竖条）时也不会露出场景的浅色 clear color。
        Quad("nav_bg", -1f, 0f, 40f, 30f, new Color(0.055f, 0.075f, 0.11f, 1f), 0.60f);

        var grid = Mat("grid", new Color(0.15f, 0.20f, 0.28f, 1f));
        for (int i = -12; i <= 12; i++)
            Strip("nav_grid_v", new Vector2(i, -6f), new Vector2(0f, 12f), 0.012f, grid, 0.55f);
        for (int j = -6; j <= 6; j++)
            Strip("nav_grid_h", new Vector2(-12f, j), new Vector2(24f, 0f), 0.012f, grid, 0.55f);
        // 注意：Glow shader 是不透明队列，不能拿它画半透明「场区底色」——
        // 那样会盖住网格。匀强场的暗示交给下面一排 ⊙/⊗ 符号表达。
    }

    void BuildArena()
    {
        var wallMat = Mat("wall", new Color(0.30f, 0.34f, 0.42f, 1f));
        var edgeMat = Mat("edge", new Color(0.18f, 0.55f, 0.70f, 0.9f));

        // 四面墙：内表面正好压在 ±9 / ±5 上
        Box("nav_wall_bottom", new Vector3(0f, Bottom - WallThick * 0.5f, 0f),
            new Vector3(Right - Left + WallThick * 2f, WallThick, 0.32f), wallMat);
        Box("nav_wall_top", new Vector3(0f, Top + WallThick * 0.5f, 0f),
            new Vector3(Right - Left + WallThick * 2f, WallThick, 0.32f), wallMat);
        Box("nav_wall_left", new Vector3(Left - WallThick * 0.5f, 0f, 0f),
            new Vector3(WallThick, Top - Bottom + WallThick * 2f, 0.32f), wallMat);
        Box("nav_wall_right", new Vector3(Right + WallThick * 0.5f, 0f, 0f),
            new Vector3(WallThick, Top - Bottom + WallThick * 2f, 0.32f), wallMat);

        // 内缘亮线：把可活动范围画清楚。墙体前表面在 z=-0.16，亮线再往前一点避免 z-fighting。
        Strip("nav_edge_b", new Vector2(Left, Bottom), new Vector2(Right - Left, 0f), 0.03f, edgeMat, -0.20f);
        Strip("nav_edge_t", new Vector2(Left, Top), new Vector2(Right - Left, 0f), 0.03f, edgeMat, -0.20f);
        Strip("nav_edge_l", new Vector2(Left, Bottom), new Vector2(0f, Top - Bottom), 0.03f, edgeMat, -0.20f);
        Strip("nav_edge_r", new Vector2(Right, Bottom), new Vector2(0f, Top - Bottom), 0.03f, edgeMat, -0.20f);
    }

    void BuildFieldSymbols()
    {
        _fieldOutRoot = new GameObject("nav_field_out").transform;
        _fieldOutRoot.SetParent(_root, false);
        _fieldInRoot = new GameObject("nav_field_in").transform;
        _fieldInRoot.SetParent(_root, false);

        var outMat = Mat("sym_out", new Color(0.55f, 0.85f, 0.98f, 0.55f));
        var inMat = Mat("sym_in", new Color(0.55f, 0.85f, 0.98f, 0.55f));

        for (int ix = 0; ix < 4; ix++)
        {
            for (int iy = 0; iy < 4; iy++)
            {
                float x = -6f + ix * 2.6f;
                float y = -3f + iy * 2f;

                // ⊙ 向外：圆圈 + 圆心点
                Ring("sym_o", _fieldOutRoot, new Vector3(x, y, 0.30f), 0.13f,
                    new Color(0.55f, 0.85f, 0.98f, 0.50f), 0.018f, 20);
                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dot.name = "sym_o_dot";
                var dc = dot.GetComponent<Collider>();
                if (dc != null) Destroy(dc);
                dot.transform.SetParent(_fieldOutRoot, false);
                dot.transform.position = new Vector3(x, y, 0.29f);
                dot.transform.localScale = Vector3.one * 0.055f;
                dot.GetComponent<Renderer>().sharedMaterial = outMat;

                // ⊗ 向里：圆圈 + X
                Ring("sym_i", _fieldInRoot, new Vector3(x, y, 0.30f), 0.13f,
                    new Color(0.55f, 0.85f, 0.98f, 0.50f), 0.018f, 20);
                Strip("sym_x", new Vector2(x - 0.085f, y - 0.085f), new Vector2(0.17f, 0.17f), 0.018f, inMat, 0.29f, _fieldInRoot);
                Strip("sym_x", new Vector2(x - 0.085f, y + 0.085f), new Vector2(0.17f, -0.17f), 0.018f, inMat, 0.29f, _fieldInRoot);
            }
        }
    }

    // ---------- 第三关：三段拼接磁场 / 检查点 / 刻度 ----------

    void BuildPathRegions()
    {
        _regionEdgeRoot = new GameObject("nav_region_edges").transform;
        _regionEdgeRoot.SetParent(_root, false);

        var edgeMat = Mat("region_edge", new Color(0.30f, 0.70f, 0.95f, 0.60f));
        Strip("nav_region_edge", new Vector2(BandLeft, Bottom), new Vector2(0f, Top - Bottom), 0.035f, edgeMat, 0.24f, _regionEdgeRoot);
        Strip("nav_region_edge", new Vector2(BandRight, Bottom), new Vector2(0f, Top - Bottom), 0.035f, edgeMat, 0.24f, _regionEdgeRoot);

        float[] bz = { RegionBz0, RegionBz1, RegionBz2 };
        float[] lx = { Left, BandLeft, BandRight };
        float[] rx = { BandLeft, BandRight, Right };

        _regionRoots = new Transform[3];
        for (int b = 0; b < 3; b++)
        {
            var root = new GameObject("nav_region" + b).transform;
            root.SetParent(_root, false);
            _regionRoots[b] = root;

            bool outward = bz[b] > 0f;
            var col = outward
                ? new Color(0.58f, 0.87f, 1.00f, 0.78f)
                : new Color(0.88f, 0.64f, 1.00f, 0.80f);

            for (int ix = 0; ix < 2; ix++)
            {
                for (int iy = 0; iy < 4; iy++)
                {
                    float x = Mathf.Lerp(lx[b] + 1.4f, rx[b] - 1.4f, ix);
                    float y = -3.3f + iy * 2.2f;
                    AddFieldSymbol(root, x, y, outward, col);
                }
            }

            // 段号（1/2/3）标在每段上方（夹到左侧可见区，避免被右侧面板挡住）
            float labelX = Mathf.Clamp((lx[b] + rx[b]) * 0.5f, Left + 1.2f, 1.0f);
            AddWorldLabel(labelX, 4.2f, (b + 1).ToString(), 22, new Color(0.80f, 0.88f, 0.98f, 0.95f), true);
            // 每块磁场的类型 + 强度（⊙向外 / ⊗向里），让「几块不同磁场」一眼可辨
            string spec = (outward ? "⊙ " : "⊗ ") + Mathf.Abs(bz[b]).ToString("0.0", CultureInfo.InvariantCulture);
            AddWorldLabel(labelX, 3.55f, spec, 15, new Color(0.66f, 0.86f, 0.98f, 0.92f), true);
        }

        // 三个检查点环（各段可见处、发射高度），预测轨迹进入该段时点亮
        _checkpoints = new LineRenderer[3];
        float[] cpX = { -7f, -3f, 0.0f };
        for (int b = 0; b < 3; b++)
        {
            _checkpoints[b] = Ring("nav_cp" + b, _root, new Vector3(cpX[b], 0f, 0.26f), 0.22f,
                new Color(0.50f, 0.58f, 0.68f, 0.6f), 0.03f, 20);
        }
    }

    void AddFieldSymbol(Transform parent, float x, float y, bool outward, Color col)
    {
        Ring("rsym", parent, new Vector3(x, y, 0.30f), 0.13f, col, 0.018f, 20);

        if (outward)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "rsym_dot";
            var dc = dot.GetComponent<Collider>();
            if (dc != null) Destroy(dc);
            dot.transform.SetParent(parent, false);
            dot.transform.position = new Vector3(x, y, 0.29f);
            dot.transform.localScale = Vector3.one * 0.055f;
            dot.GetComponent<Renderer>().sharedMaterial = Mat("rsym_fill", col);
        }
        else
        {
            var inMat = Mat("rsym_in", col);
            Strip("rsx", new Vector2(x - 0.085f, y - 0.085f), new Vector2(0.17f, 0.17f), 0.018f, inMat, 0.29f, parent);
            Strip("rsx", new Vector2(x - 0.085f, y + 0.085f), new Vector2(0.17f, -0.17f), 0.018f, inMat, 0.29f, parent);
        }
    }

    // 场地边缘刻度线：每 1 个单位一个小刻度，每 5 个单位一个长刻度并标数字。
    void BuildTicks()
    {
        var minor = Mat("tick", new Color(0.42f, 0.52f, 0.64f, 0.85f));
        var major = Mat("tick_major", new Color(0.62f, 0.74f, 0.90f, 1f));

        for (int x = (int)Left; x <= (int)Right; x++)
        {
            bool big = x % 5 == 0;
            float h = big ? 0.30f : 0.15f;
            float w = big ? 0.032f : 0.02f;
            Strip("nav_tick_b", new Vector2(x, Bottom), new Vector2(0f, h), w, big ? major : minor, -0.19f);
            if (big) AddWorldLabel(x, Bottom + h + 0.30f, x.ToString(), 16, new Color(0.70f, 0.80f, 0.92f, 0.95f), false);
        }
        for (int y = (int)Bottom; y <= (int)Top; y++)
        {
            bool big = y % 5 == 0;
            float h = big ? 0.30f : 0.15f;
            float w = big ? 0.032f : 0.02f;
            Strip("nav_tick_l", new Vector2(Left, y), new Vector2(h, 0f), w, big ? major : minor, -0.19f);
            if (big) AddWorldLabel(Left + h + 0.42f, y, y.ToString(), 16, new Color(0.70f, 0.80f, 0.92f, 0.95f), false);
        }
    }

    // 世界坐标文字：先登记世界点，OnGUI 里投影成屏幕坐标再画（字体清晰、随窗口缩放、打包也稳）。
    void AddWorldLabel(float x, float y, string text, int size, Color color, bool pathOnly)
    {
        _worldLabels.Add(new WorldLabel { pos = new Vector3(x, y, 0f), text = text, size = size, color = color, pathOnly = pathOnly });
    }

    void DrawWorldLabels()
    {
        if (_cam == null || _worldLabels.Count == 0) return;
        float f = UIScale.Factor > 0f ? UIScale.Factor : 1f;

        for (int i = 0; i < _worldLabels.Count; i++)
        {
            var lb = _worldLabels[i];
            if (lb.pathOnly && Level < 3) continue;

            Vector3 sp = _cam.WorldToScreenPoint(lb.pos);
            if (sp.z < 0f) continue;

            float vx = sp.x / f;
            float vy = (Screen.height - sp.y) / f;

            var st = new GUIStyle(_muted)
            {
                fontSize = lb.size,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
            st.normal.textColor = lb.color;
            GUI.Label(new Rect(vx - 30f, vy - 13f, 60f, 26f), lb.text, st);
        }
    }

    void UpdateFieldVisual()
    {
        bool path = Level >= 3;
        if (_fieldOutRoot != null) _fieldOutRoot.gameObject.SetActive(!path && FieldOut);
        if (_fieldInRoot != null) _fieldInRoot.gameObject.SetActive(!path && !FieldOut);

        if (_regionRoots != null)
            foreach (var r in _regionRoots) if (r != null) r.gameObject.SetActive(path);
        if (_regionEdgeRoot != null) _regionEdgeRoot.gameObject.SetActive(path);
        if (_checkpoints != null)
            foreach (var c in _checkpoints) if (c != null) c.enabled = path;
    }

    void BuildLauncher()
    {
        Box("nav_launcher", new Vector3(LaunchX - 0.42f, LaunchY, 0f),
            new Vector3(0.62f, 0.30f, 0.34f), Mat("launcher", new Color(0.52f, 0.58f, 0.66f, 1f)));
        Strip("nav_launch_arrow", new Vector2(LaunchX - 0.08f, LaunchY), new Vector2(0.62f, 0f),
            0.05f, Mat("arrow", new Color(0.88f, 0.92f, 0.98f, 0.85f)), -0.30f);
    }

    void BuildReceiver()
    {
        // 接收器 = 贴在目标墙内表面上的一段亮绿色判定区
        var bar = Quad("nav_receiver", 0f, 0f, 1f, 0.18f, new Color(0.20f, 0.82f, 0.45f, 1f), -0.22f);
        _receiverBar = bar.transform;

        _receiverRing = Ring("nav_receiver_ring", _root, Vector3.zero, 0.30f,
            new Color(0.35f, 0.95f, 0.55f, 0.95f), 0.035f, 28);

        PlaceReceiver();
    }

    void PlaceReceiver()
    {
        if (_receiverBar == null) return;

        float half = Mathf.Max(0.05f, _receiverHalf);
        bool verticalWall = _targetWall == WallLeft || _targetWall == WallRight;

        if (verticalWall)
        {
            // 贴在左/右壁内表面：旋转 90°，让判定条沿墙竖直铺开（长度 = 2*half）
            bool left = _targetWall == WallLeft;
            float x = left ? Left + 0.09f : Right - 0.09f;
            _receiverBar.position = new Vector3(x, _targetX, -0.22f);
            _receiverBar.localScale = new Vector3(half * 2f, 0.18f, 1f);
            _receiverBar.localRotation = Quaternion.Euler(0f, 0f, 90f);
            if (_receiverRing != null)
                _receiverRing.transform.position = new Vector3(left ? Left : Right, _targetX, -0.21f);
        }
        else
        {
            bool bottom = _targetWall == WallBottom;
            float y = bottom ? Bottom + 0.09f : Top - 0.09f;
            _receiverBar.position = new Vector3(_targetX, y, -0.22f);
            _receiverBar.localScale = new Vector3(half * 2f, 0.18f, 1f);
            _receiverBar.localRotation = Quaternion.identity;
            if (_receiverRing != null)
                _receiverRing.transform.position = new Vector3(_targetX, bottom ? Bottom : Top, -0.21f);
        }

        if (_receiverRing != null) _receiverRing.enabled = true;
    }

    void BuildBall()
    {
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "nav_ball";
        var col = ball.GetComponent<Collider>();
        if (col != null) Destroy(col);
        ball.transform.SetParent(_root, false);
        ball.transform.position = new Vector3(LaunchX, LaunchY, -0.45f);
        ball.transform.localScale = Vector3.one * (BallRadius * 2f);
        ball.GetComponent<Renderer>().sharedMaterial = Glow(new Color(1.00f, 0.78f, 0.30f, 1f));
        _ball = ball.transform;

        _impactRing = Ring("nav_impact", _root, Vector3.zero, 0.26f,
            new Color(0.30f, 0.95f, 0.55f, 0.95f), 0.030f, 24);
        _impactRing.enabled = false;
    }

    void BuildLines()
    {
        var ghost = new GameObject("nav_ghost");
        ghost.transform.SetParent(_root, false);
        _ghost = ghost.AddComponent<LineRenderer>();
        SetupLine(_ghost, new Color(1f, 1f, 1f, 0.28f), 0.032f);

        var live = new GameObject("nav_live");
        live.transform.SetParent(_root, false);
        _live = live.AddComponent<LineRenderer>();
        SetupLine(_live, new Color(1.00f, 0.80f, 0.34f, 0.96f), 0.055f);

        var prev = new GameObject("nav_prev");
        prev.transform.SetParent(_root, false);
        _prevLine = prev.AddComponent<LineRenderer>();
        SetupLine(_prevLine, new Color(0.95f, 0.42f, 0.34f, 0.85f), 0.034f);
        _prevLine.enabled = false;
    }

    void SetupLine(LineRenderer line, Color color, float width)
    {
        line.useWorldSpace = true;
        line.positionCount = 0;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = 4;
        line.sharedMaterial = LineMat();
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
    }

    // ---------- 场景小工具 ----------

    Material Glow(Color color)
    {
        var shader = Resources.Load<Shader>("Shaders/Glow");
        if (shader == null) shader = Shader.Find("ChargeLab/Glow");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        var m = new Material(shader) { name = "nav_glow" };
        m.SetColor("_Color", color);
        m.SetFloat("_Shade", 0.22f);
        return m;
    }

    Material LineMat()
    {
        if (_lineMat != null) return _lineMat;
        var shader = Resources.Load<Shader>("Shaders/LineColor");
        if (shader == null) shader = Shader.Find("ChargeLab/LineColor");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        _lineMat = new Material(shader) { name = "nav_line" };
        return _lineMat;
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
        go.GetComponent<Renderer>().sharedMaterial = Glow(color);
        return go;
    }

    void Strip(string name, Vector2 center, Vector2 dir, float width, Material mat, float z, Transform parent = null)
    {
        float len = dir.magnitude;
        if (len < 0.0001f) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(parent != null ? parent : _root, false);
        go.transform.position = new Vector3(center.x + dir.x * 0.5f, center.y + dir.y * 0.5f, z);
        go.transform.localScale = new Vector3(len, width, 0.02f);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    LineRenderer Ring(string name, Transform parent, Vector3 center, float radius,
        Color color, float width, int segments)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = center;

        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = segments;
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.PI * 2f * i / segments;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
        }
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.alignment = LineAlignment.View;
        line.numCapVertices = 2;
        line.sharedMaterial = LineMat();
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    // ---------- OnGUI ----------

    void OnGUI()
    {
        if (!Active) return;
        GUI.depth = -10;
        EnsureStyles();

        var size = UIScale.BeginGUI();
        float W = size.x, H = size.y;

        // 场地刻度/分区数字（投影到屏幕；面板随后绘制会盖住右侧被挡的部分）
        DrawWorldLabels();

        // 图例放右下（面板下方）：接收器常在左/下方的墙上，放左下会压住它。
        DrawLegend(W - 470f, H - 232f);
        DrawBanner(W);
        DrawPanel(W, 40f);
    }

    void DrawPanel(float W, float top)
    {
        float w = 600f, x = W - w - 40f, y = top;
        if (x < 40f) { w = Mathf.Min(600f, W - 80f); x = 40f; }

        var rect = new Rect(x, y, w, 800f);
        UIScale.Card(rect);

        _title.normal.textColor = UITheme.TextPrimary;
        GUI.Label(new Rect(x + 20f, y + 14f, w - 40f, 34f), UILang.T("nav.title"), _title);
        GUI.Label(new Rect(x + 20f, y + 50f, w - 40f, 24f), UILang.T("nav.subtitle"), _muted);

        // 三关按钮
        float cy = y + 86f;
        float gap = 8f;
        float bw = (w - 40f - gap * 2f) / 3f;
        for (int i = 1; i <= 3; i++)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = Level == i ? new Color(0.96f, 0.66f, 0.26f) : Color.white;
            if (GUI.Button(new Rect(x + 20f + (i - 1) * (bw + gap), cy, bw, 44f), UILang.T("nav.level" + i), _btn))
                ConfigureLevel(i);
            GUI.backgroundColor = old;
        }
        cy += 54f;

        string objective;
        if (Level == 1) objective = UILang.T("nav.obj1");
        else if (Level == 2) objective = UILang.T("nav.obj2");
        else objective = UILang.IsEnglish
            ? "Path planning: the board is split into three joined magnet regions — (1)⊙B=1.1, (2)⊗B=1.0, (3)⊙B=1.2 (different direction & strength). Plan the shot so the particle sweeps through all three in order, then drops into the green receiver on the bottom edge. Tune v and the firing angle."
            : "路径规划：场地由三段拼接磁场组成——(1)⊙B=1.1、(2)⊗B=1.0、(3)⊙B=1.2（方向与强度都不同）。让粒子依次穿过三段，最后落入底部边缘的绿色接收器。调初速 v 与发射角。";
        float objH = Mathf.Max(48f, _objective.CalcHeight(new GUIContent(objective), w - 40f));
        GUI.Label(new Rect(x + 20f, cy, w - 40f, objH), objective, _objective);
        cy += objH + 8f;

        // 磁场方向：第一关可以点，后两关锁死
        if (Level == 1)
        {
            float half = (w - 40f - 10f) * 0.5f;
            var old = GUI.backgroundColor;
            GUI.backgroundColor = FieldOut ? new Color(0.96f, 0.66f, 0.26f) : Color.white;
            if (GUI.Button(new Rect(x + 20f, cy, half, 42f), UILang.T("nav.fieldOut"), _btn))
            { FieldOut = true; OnParamChanged(); }
            GUI.backgroundColor = !FieldOut ? new Color(0.96f, 0.66f, 0.26f) : Color.white;
            if (GUI.Button(new Rect(x + 20f + half + 10f, cy, half, 42f), UILang.T("nav.fieldIn"), _btn))
            { FieldOut = false; OnParamChanged(); }
            GUI.backgroundColor = old;
            cy += 48f;
            GUI.Label(new Rect(x + 20f, cy, w - 40f, 24f),
                UILang.T(FieldOut ? "nav.fieldOutHint" : "nav.fieldInHint"), _muted);
            cy += 30f;
        }
        else
        {
            string fieldNote = Level >= 3
                ? (UILang.IsEnglish ? "Field: mixed per region (see below)" : "磁场：三段各不相同（见下）")
                : UILang.F("nav.fieldPreset", UILang.T(FieldOut ? "nav.fieldOut" : "nav.fieldIn"));
            GUI.Label(new Rect(x + 20f, cy, w - 40f, 26f), fieldNote, _text);
            cy += 34f;
        }

        bool speedOn = Level >= 2;
        bool bOn = Level == 2;
        bool angleOn = Level >= 3;

        cy = Slider(x + 20f, cy, w - 40f, UILang.T("nav.speed"), ref Speed, 2f, 7f, "0.0", speedOn);
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("nav.bmag"), ref BField, 0.6f, 1.6f, "0.00", bOn);
        cy = Slider(x + 20f, cy, w - 40f, UILang.T("nav.angle"), ref AimDeg, -30f, 30f, "0.0", angleOn);

        cy += 4f;
        UIScale.Fill(new Rect(x + 20f, cy, w - 40f, 1f), UITheme.Divider);
        cy += 10f;

        _value.normal.textColor = UITheme.TextPrimary;
        string infoLine = Level >= 3
            ? (UILang.IsEnglish
                ? "Regions (left→right): (1) B=1.1, (2) B=1.0, (3) B=1.2  — directions alternate"
                : "磁场分区（左→右）：(1) B=1.1、(2) B=1.0、(3) B=1.2 —— 方向交替")
            : UILang.F("nav.formula", (Speed / Mathf.Max(0.05f, BField)).ToString("0.00", CultureInfo.InvariantCulture));
        GUI.Label(new Rect(x + 20f, cy, w - 40f, 26f), infoLine, _text);
        cy += 28f;

        GUI.Label(new Rect(x + 20f, cy, w - 40f, 24f),
            UILang.F("nav.target", WallName(_targetWall),
                _targetX.ToString("0.00", CultureInfo.InvariantCulture),
                _receiverHalf.ToString("0.00", CultureInfo.InvariantCulture)), _muted);
        cy += 26f;

        GUI.Label(new Rect(x + 20f, cy, w - 40f, 24f), PredictedText(), _value);
        cy += 26f;

        GUI.Label(new Rect(x + 20f, cy, w - 40f, 24f),
            UILang.F("nav.score", _hits, _tries), _muted);
        cy += 32f;

        if (GUI.Button(new Rect(x + 20f, cy, 190f, 44f), UILang.T("nav.launch"), _btn)) Launch();
        if (GUI.Button(new Rect(x + 222f, cy, 150f, 44f), UILang.T("nav.reset"), _btn)) ResetShot();

        var oldBg = GUI.backgroundColor;
        GUI.backgroundColor = ShowPredict ? new Color(0.96f, 0.66f, 0.26f) : Color.white;
        if (GUI.Button(new Rect(x + 384f, cy, w - 404f, 44f), UILang.T("nav.predict"), _btn))
        { ShowPredict = !ShowPredict; DrawGhost(); }
        GUI.backgroundColor = oldBg;
        cy += 52f;

        if (GUI.Button(new Rect(x + 20f, cy, 190f, 40f), UILang.T("nav.compare"), _btn))
        { ShowCompare = !ShowCompare; DrawPrev(); }

        if (Level < 3)
        {
            // 命中后才亮出「下一关」，把三关串成一条递进路径
            if (_verdict == Verdict.Hit &&
                GUI.Button(new Rect(x + 222f, cy, 190f, 40f), UILang.T("nav.nextlevel"), _btn))
                ConfigureLevel(Level + 1);
        }
        else if (GUI.Button(new Rect(x + 222f, cy, 190f, 40f), UILang.T("nav.newtarget"), _btn))
        {
            NewTarget();
        }
        cy += 50f;

        GUI.Label(new Rect(x + 20f, cy, w - 40f, 52f), UILang.T("nav.hint"), _muted);
        cy += 56f;

        // 底部一行：返回实验室 + 语言切换（第四站面板里就能直接切中/英，不用退回课时再点 EN）
        const float langW = 96f;
        float backW = w - 40f - langW - 10f;
        if (GUI.Button(new Rect(x + 20f, cy, backW, 40f), UILang.T("hub.back"), _btn)) BackToHub();
        if (GUI.Button(new Rect(x + 20f + backW + 10f, cy, langW, 40f),
                UILang.IsEnglish ? "中文" : "EN", _btn))
            UILang.Toggle();
    }

    float Slider(float x, float y, float w, string label, ref float value, float min, float max, string fmt, bool enabled)
    {
        GUI.Label(new Rect(x, y, 220f, 26f), label, enabled ? _text : _muted);

        bool prev = GUI.enabled;
        GUI.enabled = enabled;
        float v = GUI.HorizontalSlider(new Rect(x + 210f, y + 9f, w - 330f, 22f), value, min, max);
        GUI.enabled = prev;

        if (enabled && !Mathf.Approximately(v, value))
        {
            value = v;
            // 拖滑块时立刻重算预测弧线：学生要看到「改一个数，落点就变」，不用等发射
            OnParamChanged();
        }

        GUI.Label(new Rect(x + w - 110f, y, 110f, 26f),
            value.ToString(fmt, CultureInfo.InvariantCulture), enabled ? _value : _muted);
        return y + 38f;
    }

    string PredictedText()
    {
        if (_preview.Valid && _preview.HitWall >= 0)
        {
            float along = Along(_preview.HitWall, _preview.Impact);
            string coord = (_preview.HitWall == WallBottom || _preview.HitWall == WallTop)
                ? "x = " + along.ToString("0.00", CultureInfo.InvariantCulture)
                : "y = " + along.ToString("0.00", CultureInfo.InvariantCulture);
            return UILang.F("nav.predicted", WallName(_preview.HitWall), coord);
        }
        return UILang.T("nav.predictedNone");
    }

    void DrawLegend(float x, float y)
    {
        var rect = new Rect(x, y, 430f, 192f);
        UIScale.Card(rect);
        _title.normal.textColor = UITheme.TextPrimary;
        GUI.Label(new Rect(x + 18f, y + 12f, 390f, 30f), UILang.T("nav.legend.title"), _title);

        float ly = y + 50f;
        LegendRow(x + 18f, ly, new Color(1.00f, 0.78f, 0.30f), UILang.T("nav.legend.ball")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.55f, 0.85f, 0.98f), UILang.T("nav.legend.field")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.20f, 0.82f, 0.45f), UILang.T("nav.legend.receiver")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(1f, 1f, 1f, 0.55f), UILang.T("nav.legend.predict")); ly += 26f;
        LegendRow(x + 18f, ly, new Color(0.95f, 0.42f, 0.34f), UILang.T("nav.legend.prev"));
    }

    void LegendRow(float x, float y, Color color, string text)
    {
        UIScale.Fill(new Rect(x, y + 6f, 16f, 10f), color);
        GUI.Label(new Rect(x + 26f, y, 380f, 26f), text, _text);
    }

    void DrawBanner(float W)
    {
        if (_running || _verdict == Verdict.None) return;

        string title, sub;
        Color color;

        switch (_verdict)
        {
            case Verdict.Hit:
                title = UILang.T("nav.hit");
                sub = UILang.F("nav.hit.sub", _hits, _tries);
                color = new Color(0.24f, 0.84f, 0.48f);
                break;

            case Verdict.MissSameWall:
            {
                float dist = Mathf.Abs(Along(_flight.HitWall, _flight.Impact) - _targetX);
                title = UILang.F("nav.miss.same", dist.ToString("0.00", CultureInfo.InvariantCulture));
                sub = UILang.T("nav.miss.same.sub");
                color = new Color(0.95f, 0.70f, 0.25f);
                break;
            }

            case Verdict.MissWrongWall:
                title = UILang.F("nav.miss.wrong", WallName(_flight.HitWall), WallName(_targetWall));
                sub = Level == 1 ? UILang.T("nav.miss.wrong.sub1")
                    : Level >= 3
                        ? (UILang.IsEnglish
                            ? "The particle left the board before sweeping through all three field regions — plan a route that reaches the receiver on the bottom edge."
                            : "粒子还没依次穿过三段磁场就撞出了边界——规划一条能到达底部接收器的路线。")
                        : UILang.T("nav.miss.wrong.sub2");
                color = new Color(0.93f, 0.42f, 0.36f);
                break;

            case Verdict.MissRoute:
                title = UILang.IsEnglish ? "Route incomplete" : "路线不完整";
                sub = UILang.IsEnglish
                    ? "The particle left the board before sweeping all three regions — the receiver is in region (3), so pass 1→2→3 in order."
                    : "粒子还没依次穿过三段磁场就出界了——接收器在第三段，必须按 1→2→3 依次穿过。";
                color = new Color(0.95f, 0.70f, 0.25f);
                break;

            default:
                title = UILang.T("nav.orbit");
                sub = UILang.T("nav.orbit.sub");
                color = new Color(0.93f, 0.42f, 0.36f);
                break;
        }

        // 横幅收窄到 520：第三关接收器可能在上壁（屏幕 x≈600 起），
        // 横幅再宽就会在结算后盖住它；520 的右缘 x≈560，正好让开。
        const float cardW = 520f;
        float innerW = cardW - 48f;
        _bannerSub.normal.textColor = UITheme.TextPrimary;
        float subH = Mathf.Max(28f, _bannerSub.CalcHeight(new GUIContent(sub), innerW));

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

    void BackToHub()
    {
        var hub = FindObjectOfType<ChargeLabHub>();
        if (hub != null) { hub.ReturnToHub(); return; }

        // 兜底：没有大厅（例如 -nohub 单独起这个模块）就直接收起来
        Active = false;
        if (_root != null) _root.gameObject.SetActive(false);
    }

    string WallName(int wall)
    {
        switch (wall)
        {
            case WallBottom: return UILang.T("nav.wall.bottom");
            case WallTop: return UILang.T("nav.wall.top");
            case WallLeft: return UILang.T("nav.wall.left");
            case WallRight: return UILang.T("nav.wall.right");
            default: return "?";
        }
    }

    void EnsureStyles()
    {
        if (_styles) return;
        _styles = true;

        _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _text = new GUIStyle(GUI.skin.label) { fontSize = 19, alignment = TextAnchor.UpperLeft };
        _text.normal.textColor = UITheme.TextPrimary;
        _muted = new GUIStyle(_text) { fontSize = 16, wordWrap = true };
        _muted.normal.textColor = UITheme.TextMuted;
        _value = new GUIStyle(_text) { fontSize = 19, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _btn = new GUIStyle(GUI.skin.button) { fontSize = 18 };
        _objective = new GUIStyle(_text) { fontSize = 17, wordWrap = true };
        _banner = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _bannerSub = new GUIStyle(_text) { fontSize = 19, wordWrap = true, alignment = TextAnchor.UpperLeft };
        _bannerSub.normal.textColor = UITheme.TextPrimary;
    }

    // ---------- 自检 ----------

    static string SideName(int wall)
    {
        switch (wall)
        {
            case WallBottom: return "bottom";
            case WallTop: return "top";
            case WallLeft: return "left";
            case WallRight: return "right";
            default: return "none";
        }
    }

    // 打进 -autotest 的自检：解析落点 vs Boris 数值落点、方向翻转、半径单调、能量守恒、三关配置。
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var go = new GameObject("nav_selftest");
        var probe = go.AddComponent<ParticleNavigatorModule>();
        probe.Level = 2;
        probe.FieldOut = true;
        probe.AimDeg = 0f;
        probe.BField = 1f;

        // 1) 解析落点 vs Boris 数值落点
        foreach (float v in new[] { 3f, 4f, 5f })
        {
            probe.Speed = v;
            var f = probe.ComputeFlight();
            float expected = -8f + Mathf.Sqrt(10f * v - 25f);
            float got = f.HitWall >= 0 ? f.Impact.x : float.NaN;
            float mm = Mathf.Abs(got - expected) * 1000f;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "r={0:0.0}  解析落点 x={1:0.000}  数值 x={2:0.000}  差 {3:0.0} mm  {4}",
                v, expected, got, mm, (f.HitWall == WallBottom && mm < 25f) ? "OK" : "!! 偏差过大"));
        }

        // 2) 方向翻转：向外打下壁、向里打上壁
        probe.Speed = 3f;
        probe.FieldOut = true; var outHit = probe.ComputeFlight();
        probe.FieldOut = false; var inHit = probe.ComputeFlight();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "方向翻转：向外→{0}  向里→{1}  {2}",
            SideName(outHit.HitWall), SideName(inHit.HitWall),
            (outHit.HitWall == WallBottom && inHit.HitWall == WallTop) ? "OK" : "!! 方向没反"));

        // 3) 半径随速度变大，落点右移（B 固定）
        probe.FieldOut = true;
        probe.Speed = 3f; var f3 = probe.ComputeFlight();
        probe.Speed = 6f; var f6 = probe.ComputeFlight();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "半径单调：v=3 落点 {0:0.00} → v=6 落点 {1:0.00}  {2}",
            f3.Impact.x, f6.Impact.x, f6.Impact.x > f3.Impact.x ? "OK 半径变大、落点右移" : "!! 不单调"));

        // 4) 能量守恒：Boris 纯磁场只旋转速度，|v| 不变
        double x = LaunchX, y = LaunchY, vx = 3.0, vy = 0.0;
        for (int i = 0; i < 5000; i++) SimulationSolver.BorisStep(Charge, Mass, 1.0, StepDt, ref x, ref y, ref vx, ref vy);
        double speed = Math.Sqrt(vx * vx + vy * vy);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "能量守恒：|v| 3 → {0:0.000000}  漂移 {1:0.0e0}  {2}",
            speed, Math.Abs(speed - 3.0), Math.Abs(speed - 3.0) < 1e-6 ? "OK 磁场力不做功" : "!! 速率变了"));

        // 5) 三关配置（第三关固定种子，输出可复现）
        probe.ConfigureLevel(1);
        sb.AppendLine(ConfigLine("第一关", probe));
        probe.ConfigureLevel(2);
        sb.AppendLine(ConfigLine("第二关", probe));
        UnityEngine.Random.InitState(6064);
        probe.ConfigureLevel(3);
        sb.AppendLine(ConfigLine("第三关", probe));

        // 6) 第三关路径可解：在 (v,θ) 网格里搜一条「依次穿过三段磁场并落入底壁接收器」的解
        probe._targetWall = WallBottom;
        probe._targetX = 0.0f;
        probe._receiverHalf = PathHalf;
        bool solved = false; float sv = 0f, sa = 0f;
        for (float v = 2f; v <= 7.001f && !solved; v += 0.2f)
        {
            for (float a = -30f; a <= 30.001f; a += 1f)
            {
                probe.Speed = v; probe.AimDeg = a;
                var pf = probe.ComputeFlight();
                float dist;
                if (probe.Evaluate(pf, out dist) == Verdict.Hit)
                { solved = true; sv = v; sa = a; break; }
            }
        }
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "第三关路径可解：找解 v={0:0.0} θ={1:0}° → 底壁接收器  {2}",
            sv, sa, solved ? "OK 三段拼接磁场可走通" : "!! 找不到可行路线"));

        // 第三关：不规划、直接默认射击（v=2.6, θ=0°）应当被判为「路线不完整」而非命中
        probe.Speed = 2.6f; probe.AimDeg = 0f;
        var missFlight = probe.ComputeFlight(); float missDist;
        var missVerdict = probe.Evaluate(missFlight, out missDist);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "第三关默认射击：maxX={0:0.00} 判定={1}  {2}",
            missFlight.MaxX, missVerdict,
            missVerdict != Verdict.Hit ? "OK 需要自己规划路线" : "!! 默认不应命中"));

        UnityEngine.Object.Destroy(go);
        return sb.ToString();
    }

    static string ConfigLine(string name, ParticleNavigatorModule p)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "{0}：接收器 {1} x={2:0.00}（半宽 {3:0.00}）  v={4:0.0}  B={5:0.00}  向外={6}",
            name, SideName(p._targetWall), p._targetX, p._receiverHalf, p.Speed, p.BField, p.FieldOut);
    }
}
