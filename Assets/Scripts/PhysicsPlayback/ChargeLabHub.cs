using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 周三 ChargeMotionLab 的「实验室大厅」——进门那一层。
//
// 房间本身由 LabRoomController 搭（地面 / 后墙 / 侧墙 / 实验台 / 吊柜 / 灯带），
// 这里只做三件事：
//   1. 第一人称漫游：WASD 沿视线走，右键拖动环视，Q/E 升降；
//   2. 房间中央摆一台「电子轨迹舱」（实验箱），舱内悬浮一颗电子沿圆轨道转；
//   3. 左键点舱体 → 自动走到观察位 → 镜头被「吸」到玻璃前（推进 + 收 FOV）→
//      黑场 → 切回原本的 2.5D 正交实验画面。实验里按 Esc 回大厅。
//
// 为什么实验本体不用 3D：2.5D 正交投影不引入近大远小，半径和速度读数是 trustworthy 的；
// 3D 只负责「进门之前」的在场感。两套相机切换的握手靠 SceneBinder 的两个静态开关：
//   SuppressAutoFrame —— 大厅在场时 SceneBinder 不取景，否则 FrameCamera 会把镜头拽走；
//   InputLocked       —— 大厅要吃掉 WASD / 数字键，实验的快捷键此时不该响应。
//
// 尺寸全部取自 LabRoomController 的公开常量，两边共用一套房间数据，
// 不会出现「改了地板高度，人却还站在旧高度浮空」。
//
// 命令行：
//   默认手动启动进大厅；-nohub 直接看实验；
//   -autotest / -autoshot 自动化默认跳过大厅（验收剧本保持原样），拍大厅图时加 -starthub；
//   -hubpos x,z 指定出生点、-hubyaw deg 指定初始朝向（调机位出图用）。
public sealed class ChargeLabHub : MonoBehaviour
{
    public static bool InHub { get; private set; }

    // ---------- 相机与走动 ----------
    const float EyeH = 1.62f;
    const float WalkSpeed = 3.4f;
    const float CamFov = 58f;
    const float MouseSens = 3.0f;

    // ---------- 实验舱（局部坐标从舱底起算，root 落在地板上）----------
    const float GlassBottom = 0.98f;     // 玻璃罩底（= 基座顶面）
    const float GlassHeight = 1.86f;
    const float GlassDiameter = 1.62f;
    const float OrbitRadius = 0.50f;
    const float OrbitPeriod = 3.6f;      // 电子转一圈的秒数，慢一点才看得清
    const float EnterDist = 1.05f;       // 被吸进去时停在玻璃前多远
    const float ViewDist = 2.80f;        // 观察位到舱面的距离

    // 四台舱 = 四个课时。沿房间纵轴由入口往里走依次是 1 → 2 → 3 → 4，
    // x 左右错开呈之字形：四台在出生点都能看到大致方位，走起来也像一条参观动线。
    // 站1 不要摆得离出生点太近 —— 3m 高的机柜 4m 内看会顶破画幅，边缘被裁掉很脏。
    static readonly Vector3[] StationPos =
    {
        new Vector3(-3.0f, LabRoomController.FloorY, -8.60f),   // 课时 1 转向与半径
        new Vector3(3.2f, LabRoomController.FloorY, -4.40f),    // 课时 2 控制变量
        new Vector3(-3.4f, LabRoomController.FloorY, 1.80f),    // 课时 3 未知粒子
        // 课时 4 电场偏转：往后退需要 1.08 的半径，z 取 4.4 时北边缘 5.48 < 后墙 6.0
        new Vector3(3.6f, LabRoomController.FloorY, 4.40f),
    };

    // 每台舱能切的数据集：进站后数字键 1~N 在站内切换。
    // 分组本身就是教学结构——站1 看现象、站2 做控制变量、站3 只有一道题。
    // 站4 不走数据文件：它自己解析求解（匀强场，见 PlateFieldModule），所以是空表。
    static readonly string[][] StationDatasets =
    {
        new[] { "SimData/same_q.json", "SimData/opposite_q.json", "SimData/different_qm.json" },
        new[]
        {
            "SimData/speed_diff.json", "SimData/field_weak.json", "SimData/field_strong.json",
            "SimData/mass_diff.json", "SimData/no_field.json",
        },
        new[] { "SimData/mystery.json" },
        new string[0],
    };

    static readonly string[] StationTitleKeys =
        { "hub.st1.title", "hub.st2.title", "hub.st3.title", "hub.st4.title" };
    static readonly string[] StationSubKeys =
        { "hub.st1.sub", "hub.st2.sub", "hub.st3.sub", "hub.st4.sub" };

    // 未知粒子站的正确选项（0=A 1=B 2=C 3=D），与 UILang 的 hub.mys.a~d 一一对应
    const int MysteryAnswer = 2;

    // 一台舱的运行时对象。三台共用同一套几何构造，只有位置 / 数据集 / 铭牌不同。
    sealed class Station
    {
        public int Index;                 // 1..4
        public Vector3 Pos;
        public string[] Datasets;
        public bool Mystery;
        public bool Plate;                // 第四台：进站后交给 PlateFieldModule，不走数据文件

        public Transform Root;
        public Transform Electron;
        // 会随悬停 / 呼吸变色的三块自发光，各持一个材质实例
        public Material HaloTopMat, HaloFloorMat, ElectronMat;
        public TextMesh Title, Sub;
        public bool Hover;
        public float Angle;

        public string HitName { get { return "chamber_hit_" + Index; } }

        public Vector3 StandPoint
        {
            get { return new Vector3(Pos.x, LabRoomController.FloorY + EyeH, Pos.z - ViewDist); }
        }

        // 视线落点：玻璃罩中部（电子轨道所在平面）
        public Vector3 Aim
        {
            get { return Pos + new Vector3(0f, GlassBottom + GlassHeight * 0.5f, 0f); }
        }
    }

    Vector3 SpawnPoint
    {
        get { return new Vector3(0f, LabRoomController.FloorY + EyeH, -13.6f); }
    }

    // ---------- 状态 ----------
    bool _enabled;
    bool _transitioning;
    float _fade = 1f;                    // 启动即黑场，场景就绪后淡入，避免闪一帧实验画面
    float _yaw, _pitch;
    Vector3? _moveTarget;
    Station _pendingStation;             // 走到观察位之后要进的那台舱

    Transform _root;
    Camera _cam;
    SceneBinder _binder;
    PlaybackController _player;
    Canvas _canvas;
    PhysicsGrid _grid;
    SpeedLabels _speed;
    ParameterPanel _param;
    AIChatPanel _ai;
    TrajectoryOverlay _trail;

    // 三台舱：全部由 BuildChambers() 建，尺寸共用一套常量
    readonly List<Station> _stations = new List<Station>();
    Station _current;                    // 正在参观 / 刚出来的那台
    Station _hoverStation;               // 准星指着的那台（悬停高亮用）
    float _pulse;

    // 未知粒子站的作答状态
    int _answer = -1;
    bool _answerRight;

    Vector2? _spawnOverride;
    float? _yawOverride;

    readonly Dictionary<string, Material> _matCache = new Dictionary<string, Material>();
    readonly List<TextMesh> _texts = new List<TextMesh>();
    readonly Dictionary<TextMesh, float> _textMaxWidth = new Dictionary<TextMesh, float>();
    Font _font;
    Material _lineMat;

    GUIStyle _hintStyle, _backStyle, _cardTitleStyle, _cardStyle, _choiceStyle;
    bool _stylesBuilt;

    static readonly Color AccentCyan = new Color(0.16f, 0.78f, 0.92f);
    static readonly Color AccentHot = new Color(0.45f, 0.95f, 1.00f);
    static readonly Color Steel = new Color(0.640f, 0.665f, 0.700f);
    static readonly Color DarkMetal = new Color(0.150f, 0.175f, 0.215f);

    // ---------- 自动装配 ----------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstall()
    {
        // 只在有回放组件的场景里生效
        if (Object.FindObjectOfType<SceneBinder>() == null) return;

        var go = new GameObject("ChargeLabHub");
        go.AddComponent<ChargeLabHub>();
    }

    void Awake()
    {
        var args = System.Environment.GetCommandLineArgs();
        bool startHub = HasArg(args, "-starthub");
        bool noHub = HasArg(args, "-nohub");
        bool automated = HasArg(args, "-autotest") || HasArg(args, "-autoshot");

        // 验收 / 截图脚本默认不进大厅，保持原有行为；要看大厅显式加 -starthub
        _enabled = !noHub && (startHub || !automated);

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-hubpos")
            {
                var parts = args[i + 1].Split(',');
                float x, z;
                if (parts.Length == 2 && float.TryParse(parts[0], out x) && float.TryParse(parts[1], out z))
                    _spawnOverride = new Vector2(x, z);
            }
            else if (args[i] == "-hubyaw")
            {
                float y;
                if (float.TryParse(args[i + 1], out y)) _yawOverride = y;
            }
        }
    }

    static bool HasArg(string[] args, string name)
    {
        foreach (var a in args) if (a == name) return true;
        return false;
    }

    void Start()
    {
        _binder = FindObjectOfType<SceneBinder>();
        _player = _binder != null ? _binder.player : null;
        if (_player == null) _enabled = false;

        if (!_enabled)
        {
            InHub = false;
            SceneBinder.SuppressAutoFrame = false;
            SceneBinder.InputLocked = false;
            _fade = 0f;
            return;
        }

        SceneBinder.SuppressAutoFrame = true;
        SceneBinder.InputLocked = true;
        InHub = true;

        var camObj = GameObject.Find("Main Camera");
        _cam = camObj != null ? camObj.GetComponent<Camera>() : Camera.main;

        BuildChambers();

        UILang.Changed += RefreshTexts;
        RefreshTexts();

        StartCoroutine(StartupRoutine());

        // -enterlab：进场 1.5s 后自动走一遍「走近 → 被吸进舱体」，用来给入场链路出验收图。
        // -roundtrip：再等 3s 自动 Esc 回大厅，验证往返后相机和 2.5D 取景都能恢复。
        // -enterstation N：指定进第几台舱（默认 1，共四台），给每台舱分别出图用。
        // 人手点一次复现不了这么精确的时机，自动化又需要一条能跑通的链路。
        var argv = System.Environment.GetCommandLineArgs();
        bool roundTrip = HasArg(argv, "-roundtrip");
        if (roundTrip || HasArg(argv, "-enterlab"))
        {
            int want = 1;
            for (int i = 0; i < argv.Length - 1; i++)
                if (argv[i] == "-enterstation") int.TryParse(argv[i + 1], out want);

            var target = _stations.Count > 0 ? _stations[Mathf.Clamp(want, 1, _stations.Count) - 1] : null;
            if (target != null) StartCoroutine(DelayedEnter(roundTrip, target));
        }
    }

    void OnDestroy()
    {
        UILang.Changed -= RefreshTexts;
        // 不打回原状会留下「别取景」的残留状态：关掉 Play 再进，实验画面就不取景了
        SceneBinder.SuppressAutoFrame = false;
        SceneBinder.InputLocked = false;
        InHub = false;
    }

    // ---------- 启动 ----------

    IEnumerator StartupRoutine()
    {
        float timeout = 6f;
        while (_player.Loader.Output == null && timeout > 0f)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        _player.Pause();
        HideExperiment();

        if (_cam != null)
        {
            _cam.orthographic = false;      // SceneBinder 已经把它切成正交并取好景了，这里抢回来
            _cam.fieldOfView = CamFov;
            _cam.nearClipPlane = 0.05f;

            var spawn = _spawnOverride ?? new Vector2(SpawnPoint.x, SpawnPoint.z);
            _cam.transform.position = new Vector3(spawn.x, LabRoomController.FloorY + EyeH, spawn.y);
            _yaw = _yawOverride ?? 0f;
            _pitch = 2f;
            _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        Debug.Log($"[ChargeLabHub] cam={_cam.transform.position} rot={_cam.transform.eulerAngles} " +
                  $"stations={_stations.Count} spawnOverride={_spawnOverride} yawOverride={_yawOverride}");

        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.55f)
        {
            _fade = 1f - t;
            yield return null;
        }
        _fade = 0f;
    }

    IEnumerator DelayedEnter(bool roundTrip, Station station)
    {
        yield return new WaitForSeconds(1.5f);
        yield return EnterRoutine(station);

        if (!roundTrip) yield break;
        yield return new WaitForSeconds(3.0f);
        yield return ReturnRoutine();
    }

    // ---------- 实验画面的显示 / 隐藏 ----------

    void HideExperiment()
    {
        if (_binder != null && _binder.bodiesRoot != null) _binder.bodiesRoot.gameObject.SetActive(false);
        if (_canvas == null) _canvas = FindObjectOfType<Canvas>();
        if (_canvas != null) _canvas.enabled = false;
        if (_grid == null) _grid = FindObjectOfType<PhysicsGrid>();
        if (_grid != null) _grid.enabled = false;
        if (_speed == null) _speed = FindObjectOfType<SpeedLabels>();
        if (_speed != null) _speed.enabled = false;
        if (_param == null) _param = FindObjectOfType<ParameterPanel>();
        if (_param != null) _param.enabled = false;
        if (_ai == null) _ai = FindObjectOfType<AIChatPanel>();
        if (_ai != null) _ai.enabled = false;
        if (_trail == null) _trail = FindObjectOfType<TrajectoryOverlay>();
        if (_trail != null) _trail.SetVisible(false);
    }

    void ShowExperiment()
    {
        if (_binder != null && _binder.bodiesRoot != null) _binder.bodiesRoot.gameObject.SetActive(true);
        if (_canvas == null) _canvas = FindObjectOfType<Canvas>();
        if (_canvas != null) _canvas.enabled = true;
        if (_grid == null) _grid = FindObjectOfType<PhysicsGrid>();
        if (_grid != null) _grid.enabled = true;
        if (_speed == null) _speed = FindObjectOfType<SpeedLabels>();
        if (_speed != null) _speed.enabled = true;
        if (_param == null) _param = FindObjectOfType<ParameterPanel>();
        if (_param != null) _param.enabled = true;
        if (_ai == null) _ai = FindObjectOfType<AIChatPanel>();
        if (_ai != null) _ai.enabled = true;
        if (_trail == null) _trail = FindObjectOfType<TrajectoryOverlay>();
        if (_trail != null) _trail.SetVisible(true);
    }

    // ---------- 帧循环 ----------

    void Update()
    {
        if (!_enabled) return;

        // 电子沿轨道转 + 光环呼吸：静止的箱子和「活的实验装置」观感差很多
        AnimateChamber();

        if (InHub)
        {
            if (!_transitioning) HubInput();
        }
        else if (!_transitioning)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) ReturnToHub();
        }
    }

    void AnimateChamber()
    {
        float dt = Time.unscaledDeltaTime;
        _pulse = 0.72f + Mathf.Sin(Time.unscaledTime * 2.2f) * 0.18f;

        for (int i = 0; i < _stations.Count; i++)
        {
            var s = _stations[i];
            if (s.Electron == null) continue;

            // 三台错开相位：同时转会显得像同一台机器复制了三份
            s.Angle += dt * (Mathf.PI * 2f / OrbitPeriod);
            float a = s.Angle + i * 2.1f;
            s.Electron.localPosition = new Vector3(
                Mathf.Cos(a) * OrbitRadius,
                GlassBottom + GlassHeight * 0.5f + Mathf.Sin(a) * OrbitRadius,
                0f);

            ApplyHalo(s, _pulse);
        }
    }

    // ---------- 第一人称输入 ----------

    void HubInput()
    {
        if (_cam == null) return;

        // 右键拖动环视。松开右键光标回来，才能左键点舱体 / 点地面
        if (Input.GetMouseButton(1))
        {
            _yaw += Input.GetAxis("Mouse X") * MouseSens;
            _pitch -= Input.GetAxis("Mouse Y") * MouseSens;
            _pitch = Mathf.Clamp(_pitch, -78f, 78f);
            _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            _moveTarget = null;
            _pendingStation = null;
        }

        var pos = _cam.transform.position;
        float move = WalkSpeed * Time.deltaTime;
        float f = 0f, r = 0f, up = 0f;
        if (Input.GetKey(KeyCode.W)) f += 1f;
        if (Input.GetKey(KeyCode.S)) f -= 1f;
        if (Input.GetKey(KeyCode.D)) r += 1f;
        if (Input.GetKey(KeyCode.A)) r -= 1f;
        if (Input.GetKey(KeyCode.E)) up += 1f;
        if (Input.GetKey(KeyCode.Q)) up -= 1f;

        bool keyed = Mathf.Abs(f) > 0.01f || Mathf.Abs(r) > 0.01f || Mathf.Abs(up) > 0.01f;
        if (keyed)
        {
            var fwd = _cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = _cam.transform.right;
            right.y = 0f;
            right.Normalize();

            pos += (fwd * f + right * r).normalized * (move * Mathf.Max(1f, Mathf.Abs(f) + Mathf.Abs(r)) * 0.7f);
            pos.y += up * move;
            _moveTarget = null;
            _pendingStation = null;
            _cam.transform.position = ClampToRoom(pos);
        }

        if (Input.GetMouseButtonDown(0)) HandleClick();
        HandleArrival();

        var hovered = _moveTarget == null ? PickChamber() : null;
        if (hovered != _hoverStation)
        {
            // 只重刷发生变化的两台：悬停态是材质颜色，全量刷等于每帧给三台都算一遍
            if (_hoverStation != null) { _hoverStation.Hover = false; ApplyHover(_hoverStation); }
            _hoverStation = hovered;
            if (_hoverStation != null) { _hoverStation.Hover = true; ApplyHover(_hoverStation); }
        }
    }

    void HandleClick()
    {
        var ray = _cam.ScreenPointToRay(Input.mousePosition);
        var hits = Physics.RaycastAll(ray, 60f);
        if (hits == null || hits.Length == 0) return;

        Station hitStation = null;
        Vector3? floorPoint = null;
        float bestFloor = float.MaxValue;

        foreach (var hit in hits)
        {
            var s = StationOf(hit.collider.name);
            if (s != null) { hitStation = s; break; }
            if (hit.collider.name == "hub_walkfloor" && hit.distance < bestFloor)
            {
                bestFloor = hit.distance;
                floorPoint = hit.point;
            }
        }

        if (hitStation != null)
        {
            _pendingStation = hitStation;
            _moveTarget = hitStation.StandPoint;
            return;
        }

        if (floorPoint.HasValue)
        {
            _pendingStation = null;
            _moveTarget = ClampToRoom(floorPoint.Value);
        }
    }

    // 命中体名字 → 舱。三台各有一个 chamber_hit_N，靠后缀区分是哪一台。
    Station StationOf(string colliderName)
    {
        foreach (var s in _stations)
            if (s.HitName == colliderName) return s;
        return null;
    }

    void HandleArrival()
    {
        if (!_moveTarget.HasValue || _cam == null) return;

        var target = _moveTarget.Value;
        var pos = _cam.transform.position;
        var flat = new Vector3(target.x - pos.x, 0f, target.z - pos.z);

        if (flat.magnitude <= WalkSpeed * Time.deltaTime)
        {
            pos.x = target.x;
            pos.z = target.z;
            _cam.transform.position = ClampToRoom(pos);
            _moveTarget = null;

            if (_pendingStation != null)
            {
                var s = _pendingStation;
                _pendingStation = null;
                StartCoroutine(EnterRoutine(s));
            }
            return;
        }

        pos += flat.normalized * (WalkSpeed * Time.deltaTime);
        _cam.transform.position = ClampToRoom(pos);
    }

    // 房间是个矩形盒子：只在室内活动，不穿后墙/侧墙，也走不进实验台里面
    Vector3 ClampToRoom(Vector3 pos)
    {
        pos.x = Mathf.Clamp(pos.x, -LabRoomController.HalfX + 1.6f, LabRoomController.HalfX - 1.6f);
        // 实验台前沿在 z≈3.5，止步于 3.2，走到那也撞不进台体
        pos.z = Mathf.Clamp(pos.z, LabRoomController.FrontZ + 2.0f, LabRoomController.BenchZ - 1.0f);
        pos.y = Mathf.Clamp(pos.y, LabRoomController.FloorY + 0.55f, LabRoomController.FloorY + 4.20f);
        return pos;
    }

    Station PickChamber()
    {
        var ray = _cam.ScreenPointToRay(Input.mousePosition);
        var hits = Physics.RaycastAll(ray, 60f);
        if (hits == null) return null;
        foreach (var hit in hits)
        {
            var s = StationOf(hit.collider.name);
            if (s != null) return s;
        }
        return null;
    }

    // ---------- 进 / 出实验 ----------

    IEnumerator EnterRoutine(Station station)
    {
        _transitioning = true;
        _moveTarget = null;
        _pendingStation = null;
        _current = station;

        // 站内数据集：进站前先换掉 SceneBinder 的切换表，
        // 学生进去按数字键只在这台舱的算例里循环，不会串到别的课时。
        // 偏转靶场不用数据文件，保留原表即可（空表会让 LoadFile 无法兜底）。
        if (station.Datasets.Length > 0) _binder.datasetFiles = station.Datasets;
        // 未知粒子站抹掉 q / m / r / T，退出时在 ReturnRoutine 里复位
        ParameterPanel.MaskSolution = station.Mystery;
        // 每次进站都从「未作答」开始，重进就是重做一遍
        _answer = -1;
        _answerRight = false;

        var standV = station.StandPoint;
        var aimV = station.Aim;

        // 1) 走到观察位（已经很近就跳过）。边走边把视线转到舱体，避免「面朝空气平移」
        var stand = new Vector3(standV.x, _cam.transform.position.y, standV.z);
        float dist = Vector3.Distance(
            new Vector3(_cam.transform.position.x, 0f, _cam.transform.position.z),
            new Vector3(stand.x, 0f, stand.z));

        if (dist > 0.30f)
        {
            var from = _cam.transform.position;
            float dur = Mathf.Clamp(dist / WalkSpeed, 0.25f, 1.5f);
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / dur)
            {
                float e = Smooth(t);
                _cam.transform.position = Vector3.Lerp(from, stand, e);
                AimAt(aimV, e * 1.4f);
                yield return null;
            }
            _cam.transform.position = stand;
        }

        // 2) 对准舱心
        yield return FaceRoutine(aimV, 0.30f);

        // 3) 被「吸」过去：镜头推进到玻璃前，FOV 同步收窄。
        //    这一步是整个入场的关键——不是切镜头，是让观众感觉被装置吸进去。
        var p0 = _cam.transform.position;
        var p1 = aimV - _cam.transform.forward * EnterDist;
        float f0 = _cam.fieldOfView;

        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 1.10f)
        {
            float e = Smooth(t);
            _cam.transform.position = Vector3.Lerp(p0, p1, e);
            _cam.fieldOfView = Mathf.Lerp(f0, 34f, e);
            yield return null;
        }

        // 4) 黑场 → 交还给 2.5D 实验画面
        yield return FadeTo(1f, 0.28f);

        InHub = false;

        if (_hoverStation != null) { _hoverStation.Hover = false; ApplyHover(_hoverStation); }
        _hoverStation = null;

        if (_root != null) _root.gameObject.SetActive(false);

        if (station.Plate)
        {
            // 第四台舱：整个画面交给偏转靶场自己画（它自带取景、参数表和判定横幅）。
            // 这里不能放开 SceneBinder —— 它一拿到相机就会按 json 里那两个点重新取景，
            // 把靶场的 2D 视口整个顶掉。两条 InputLocked / SuppressAutoFrame 全程保持 true。
            HideExperiment();
            PlateFieldModule.ShowModule(_cam);
        }
        else
        {
            SceneBinder.SuppressAutoFrame = false;
            SceneBinder.InputLocked = false;
            ShowExperiment();
            _binder.LoadFile(station.Datasets[0]);
        }

        yield return new WaitForSeconds(0.18f);

        yield return FadeTo(0f, 0.40f);
        _transitioning = false;
    }

    public void ReturnToHub()
    {
        if (!_enabled || _transitioning || InHub) return;
        StartCoroutine(ReturnRoutine());
    }

    IEnumerator ReturnRoutine()
    {
        _transitioning = true;

        yield return FadeTo(1f, 0.25f);

        // 偏转靶场是自绘全屏模块，拖到全黑之后再收，
        // 否则它的画面会盖在大厅的黑场之上，退出去看着像没反应。
        PlateFieldModule.HideModule();

        _player.Pause();
        InHub = true;
        SceneBinder.SuppressAutoFrame = true;
        SceneBinder.InputLocked = true;
        ParameterPanel.MaskSolution = false;   // 下一站不一定是未知粒子，先复位

        if (_root != null) _root.gameObject.SetActive(true);
        HideExperiment();

        if (_cam != null && _current != null)
        {
            // 退出来就站在观察位、正对舱体 —— 比退回「点击前站的地方」更像从实验里退出
            var stand = _current.StandPoint;
            var aim = _current.Aim;
            _cam.transform.position = stand;
            var to = aim - stand;
            if (to.sqrMagnitude > 0.0001f) _cam.transform.rotation = Quaternion.LookRotation(to);
            _yaw = ChamberWorldYaw(_current);
            _pitch = Mathf.DeltaAngle(0f, _cam.transform.rotation.eulerAngles.x);

            _cam.orthographic = false;      // FrameCamera 进实验时切成了正交，这里切回来
            _cam.fieldOfView = CamFov;
        }

        yield return FadeTo(0f, 0.40f);
        _transitioning = false;
    }

    void AimAt(Vector3 worldTarget, float weight)
    {
        var to = worldTarget - _cam.transform.position;
        if (to.sqrMagnitude < 0.0001f) return;
        var want = Quaternion.LookRotation(to);
        _cam.transform.rotation = Quaternion.Slerp(_cam.transform.rotation, want, Mathf.Clamp01(weight));
        _yaw = _cam.transform.rotation.eulerAngles.y;
        _pitch = Mathf.DeltaAngle(0f, _cam.transform.rotation.eulerAngles.x);
    }

    IEnumerator FaceRoutine(Vector3 worldTarget, float duration)
    {
        if (_cam == null) yield break;

        var startRot = _cam.transform.rotation;
        var to = worldTarget - _cam.transform.position;
        if (to.sqrMagnitude < 0.0001f) yield break;
        var want = Quaternion.LookRotation(to);

        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / Mathf.Max(0.05f, duration))
        {
            _cam.transform.rotation = Quaternion.Slerp(startRot, want, Smooth(t));
            yield return null;
        }
        _cam.transform.rotation = want;
        _yaw = want.eulerAngles.y;
        _pitch = Mathf.DeltaAngle(0f, want.eulerAngles.x);
    }

    IEnumerator FadeTo(float target, float duration)
    {
        float start = _fade;
        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / Mathf.Max(0.02f, duration))
        {
            _fade = Mathf.Lerp(start, target, Smooth(t));
            yield return null;
        }
        _fade = target;
    }

    // 正对舱体的水平朝向（返回后要用它同步 _yaw，否则下一次拖动会「跳」一下）
    float ChamberWorldYaw(Station s)
    {
        var to = s.Aim - s.StandPoint;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return 0f;
        return Quaternion.LookRotation(to).eulerAngles.y;
    }

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // ---------- 搭「电子轨迹舱」 ----------

    void BuildRoot()
    {
        if (_root != null) return;
        _root = new GameObject("ChargeLabHubRoot").transform;

        // 走动用的地面命中体：房间地板本身为了省开销没留碰撞体，
        // 这里补一块纯碰撞的薄板，点地面就能走过去。
        var floor = new GameObject("hub_walkfloor");
        floor.transform.SetParent(_root, false);
        floor.transform.localPosition = new Vector3(
            0f, LabRoomController.FloorY - 0.05f,
            (LabRoomController.BackZ + LabRoomController.FrontZ) * 0.5f);
        var fc = floor.AddComponent<BoxCollider>();
        fc.size = new Vector3(LabRoomController.HalfX * 2f, 0.10f,
            LabRoomController.BackZ - LabRoomController.FrontZ);
    }

    void BuildChambers()
    {
        BuildRoot();

        for (int i = 0; i < StationPos.Length; i++)
        {
            var s = new Station
            {
                Index = i + 1,
                Pos = StationPos[i],
                Datasets = StationDatasets[i],
                // 只有一台是「未知粒子」挑战：它只有一份数据，进去就是要答案
                Mystery = StationDatasets[i].Length == 1,
                // 没有数据文件的那台就是自己求解的偏转靶场
                Plate = StationDatasets[i].Length == 0,
            };
            BuildOneChamber(s);
            _stations.Add(s);
        }

        BuildGuide();
    }

    // 参观动线：入口 → 站1 → 站2 → 站3 的地面折线。
    // 地面是一整片灰砖，没有这条线画面下半截就是空的，第一次进来的人也不知道该往哪儿走。
    void BuildGuide()
    {
        var mat = Solid(AccentCyan, 0.55f, AccentCyan * 0.85f);
        var pts = new List<Vector3> { new Vector3(SpawnPoint.x, 0f, SpawnPoint.z) };
        foreach (var s in _stations) pts.Add(new Vector3(s.StandPoint.x, 0f, s.StandPoint.z));

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var dir = new Vector3(b.x - a.x, 0f, b.z - a.z);
            float len = dir.magnitude;
            if (len < 0.05f) continue;

            var seg = Box("chamber_guide",
                new Vector3((a.x + b.x) * 0.5f, 0.016f, (a.z + b.z) * 0.5f),
                new Vector3(0.14f, 0.020f, len), mat, _root);
            // 盒子默认沿 z 轴拉长，转到线段方向即可
            seg.transform.localRotation = Quaternion.LookRotation(dir, Vector3.up);
        }
    }

    void BuildOneChamber(Station s)
    {
        var root = new GameObject("lab_chamber_" + s.Index);
        root.transform.SetParent(_root, false);
        root.transform.localPosition = s.Pos;
        s.Root = root.transform;

        var t = s.Root;

        // 底座：地台 + 机柜 + 前面板
        Box("chamber_plinth", new Vector3(0f, 0.07f, 0f), new Vector3(2.16f, 0.14f, 2.16f),
            Solid(new Color(0.235f, 0.255f, 0.290f), 0.30f), t);
        Box("chamber_base", new Vector3(0f, 0.56f, 0f), new Vector3(1.86f, 0.84f, 1.86f),
            Solid(new Color(0.300f, 0.325f, 0.365f), 0.45f), t);
        Box("chamber_panel", new Vector3(0f, 0.56f, -0.945f), new Vector3(1.58f, 0.46f, 0.04f),
            Solid(DarkMetal, 0.55f), t);

        // 前面板指示灯：右三颗常亮，左两颗慢闪（用自发光，暗房间里也点得亮）
        for (int i = 0; i < 3; i++)
        {
            var led = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            led.name = "chamber_led";
            Destroy(led.GetComponent<Collider>());
            led.transform.SetParent(t, false);
            led.transform.localPosition = new Vector3(0.30f + i * 0.16f, 0.62f, -0.97f);
            led.transform.localScale = Vector3.one * 0.055f;
            Paint(led.GetComponent<MeshRenderer>(),
                Emissive(i == 0 ? new Color(1f, 0.58f, 0.18f) : AccentCyan, 1.0f));
        }

        // 通风格栅
        for (int i = 0; i < 4; i++)
            Box("chamber_grill", new Vector3(-0.28f, 0.74f - i * 0.09f, -0.965f),
                new Vector3(0.44f, 0.028f, 0.03f), Solid(DarkMetal, 0.5f), t);

        // 玻璃罩（走自带的 ChamberGlass：Standard 的透明变体打包后会被剥离）
        var glass = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        glass.name = "chamber_glass";
        Destroy(glass.GetComponent<Collider>());
        glass.transform.SetParent(t, false);
        glass.transform.localPosition = new Vector3(0f, GlassBottom + GlassHeight * 0.5f, 0f);
        glass.transform.localScale = new Vector3(GlassDiameter, GlassHeight * 0.5f, GlassDiameter);
        Paint(glass.GetComponent<MeshRenderer>(), GlassMaterial(new Color(0.55f, 0.80f, 0.90f), 0.14f));

        // 玻璃底座法兰
        var flange = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        flange.name = "chamber_flange";
        Destroy(flange.GetComponent<Collider>());
        flange.transform.SetParent(t, false);
        flange.transform.localPosition = new Vector3(0f, GlassBottom + 0.03f, 0f);
        flange.transform.localScale = new Vector3(GlassDiameter + 0.10f, 0.035f, GlassDiameter + 0.10f);
        Paint(flange.GetComponent<MeshRenderer>(), Solid(new Color(0.520f, 0.552f, 0.595f), 0.72f));

        // 罩内底盘：法兰顶面在光下很白，不压一块深色板，玻璃罩看着像洗菜盆
        var deck = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        deck.name = "chamber_deck";
        Destroy(deck.GetComponent<Collider>());
        deck.transform.SetParent(t, false);
        deck.transform.localPosition = new Vector3(0f, GlassBottom + 0.07f, 0f);
        deck.transform.localScale = new Vector3(GlassDiameter - 0.06f, 0.035f, GlassDiameter - 0.06f);
        Paint(deck.GetComponent<MeshRenderer>(), Solid(new Color(0.175f, 0.195f, 0.230f), 0.60f));

        // 顶盖 + 顶盖光环
        var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cap.name = "chamber_cap";
        Destroy(cap.GetComponent<Collider>());
        cap.transform.SetParent(t, false);
        cap.transform.localPosition = new Vector3(0f, GlassBottom + GlassHeight + 0.09f, 0f);
        cap.transform.localScale = new Vector3(GlassDiameter + 0.24f, 0.09f, GlassDiameter + 0.24f);
        Paint(cap.GetComponent<MeshRenderer>(), Solid(new Color(0.335f, 0.360f, 0.400f), 0.62f));

        var haloTop = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        haloTop.name = "chamber_halo_top";
        Destroy(haloTop.GetComponent<Collider>());
        haloTop.transform.SetParent(t, false);
        // 贴在顶盖外缘（直径略大于顶盖），从下方仰视时才看得到这一圈光
        haloTop.transform.localPosition = new Vector3(0f, GlassBottom + GlassHeight + 0.055f, 0f);
        haloTop.transform.localScale = new Vector3(GlassDiameter + 0.30f, 0.014f, GlassDiameter + 0.30f);
        s.HaloTopMat = Emissive(AccentCyan, 0.95f);
        Paint(haloTop.GetComponent<MeshRenderer>(), s.HaloTopMat);

        // 舱内：中心靶 + 电子轨道 + 电子
        var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        core.name = "chamber_core";
        Destroy(core.GetComponent<Collider>());
        core.transform.SetParent(t, false);
        core.transform.localPosition = new Vector3(0f, GlassBottom + GlassHeight * 0.5f, 0f);
        core.transform.localScale = Vector3.one * 0.13f;
        Paint(core.GetComponent<MeshRenderer>(), Solid(new Color(0.28f, 0.31f, 0.36f), 0.85f));

        Ring("chamber_orbit", t,
            new Vector3(0f, GlassBottom + GlassHeight * 0.5f, 0f), OrbitRadius,
            new Color(0.60f, 0.88f, 1.00f, 0.75f), 0.019f, 48, Quaternion.identity);

        var electron = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        electron.name = "chamber_electron";
        Destroy(electron.GetComponent<Collider>());
        electron.transform.SetParent(t, false);
        electron.transform.localPosition = new Vector3(OrbitRadius, GlassBottom + GlassHeight * 0.5f, 0f);
        electron.transform.localScale = Vector3.one * 0.17f;
        s.Electron = electron.transform;
        s.ElectronMat = Emissive(new Color(0.42f, 0.80f, 1.00f), 1.00f);
        Paint(electron.GetComponent<MeshRenderer>(), s.ElectronMat);

        // 两侧磁场线圈：一圈铜管箍在玻璃罩外，别做太大 —— 直径超过罩子就是两个呼啦圈，
        // 会把「实验舱」读成「陀螺仪」。
        CoilRing("chamber_coil_l", t,
            new Vector3(-(GlassDiameter * 0.5f + 0.10f), GlassBottom + GlassHeight * 0.5f, 0f),
            0.52f, 28, 0.038f, new Color(0.78f, 0.50f, 0.20f), Quaternion.Euler(0f, 90f, 0f));
        CoilRing("chamber_coil_r", t,
            new Vector3(GlassDiameter * 0.5f + 0.10f, GlassBottom + GlassHeight * 0.5f, 0f),
            0.52f, 28, 0.038f, new Color(0.78f, 0.50f, 0.20f), Quaternion.Euler(0f, 90f, 0f));

        // 线圈支架：从机柜顶撑到线圈底部（x 跟着线圈走，别留在老位置）
        foreach (int side in new[] { -1, 1 })
        {
            Box("chamber_brace", new Vector3(side * (GlassDiameter * 0.5f + 0.10f), GlassBottom - 0.10f, 0f),
                new Vector3(0.09f, 0.26f, 0.09f), Solid(Steel, 0.6f), t);
        }

        // 铭牌：横跨在顶盖前方
        var plate = new GameObject("chamber_plate_" + s.Index);
        plate.transform.SetParent(t, false);
        plate.transform.localPosition = new Vector3(0f, GlassBottom + GlassHeight - 0.20f, -(GlassDiameter * 0.5f + 0.03f));
        Box("chamber_plate_bg", Vector3.zero, new Vector3(1.34f, 0.30f, 0.035f),
            Solid(new Color(0.115f, 0.135f, 0.170f), 0.55f), plate.transform);

        // 名字必须带序号：RefreshTexts 靠 GameObject 名查词条，三台同名会全刷成同一句
        s.Title = MakeText("chamber_title_" + s.Index, StationTitleKeys[s.Index - 1],
            new Vector3(0f, 0.055f, -0.03f),
            64, 0.026f, new Color(0.72f, 0.92f, 1.00f), TextAnchor.MiddleCenter, plate.transform);
        LimitWidth(s.Title, 1.10f);

        s.Sub = MakeText("chamber_sub_" + s.Index, StationSubKeys[s.Index - 1],
            new Vector3(0f, -0.075f, -0.03f),
            56, 0.019f, new Color(0.60f, 0.78f, 0.90f), TextAnchor.MiddleCenter, plate.transform);
        LimitWidth(s.Sub, 1.10f);

        // 地面光环：站上去的地方。悬停时整体变亮，等于一个「可以点」的地面提示
        var halo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        halo.name = "chamber_halo_floor";
        Destroy(halo.GetComponent<Collider>());
        halo.transform.SetParent(t, false);
        halo.transform.localPosition = new Vector3(0f, 0.012f, -1.62f);
        halo.transform.localScale = new Vector3(1.30f, 0.012f, 1.30f);
        s.HaloFloorMat = Emissive(new Color(0.11f, 0.58f, 0.74f), 0.90f);
        Paint(halo.GetComponent<MeshRenderer>(), s.HaloFloorMat);

        // 点击命中体：覆盖基座 + 玻璃罩，比逐部件挂碰撞体省事，也不会被内部小球挡住。
        // 名字带序号，点哪一台进哪一台。
        var hit = new GameObject(s.HitName);
        hit.transform.SetParent(t, false);
        hit.transform.localPosition = new Vector3(0f, 1.75f, 0f);
        var bc = hit.AddComponent<BoxCollider>();
        bc.size = new Vector3(2.05f, 3.50f, 2.05f);
    }

    void ApplyHover(Station s)
    {
        bool hot = s.Hover;
        SetEmission(s.HaloTopMat, hot ? AccentHot : AccentCyan, hot ? 1.35f : 0.95f);
        SetEmission(s.HaloFloorMat, hot ? AccentHot : new Color(0.11f, 0.58f, 0.74f), hot ? 1.30f : 0.90f);
        SetEmission(s.ElectronMat, hot ? new Color(0.80f, 0.97f, 1.00f) : new Color(0.42f, 0.80f, 1.00f),
            hot ? 1.25f : 1.00f);
    }

    void ApplyHalo(Station s, float pulse)
    {
        // 呼吸只作用在自发光强度上，颜色由悬停决定，两者互不覆盖
        SetEmission(s.HaloTopMat, s.Hover ? AccentHot : AccentCyan, (s.Hover ? 1.35f : 0.95f) * pulse);
    }

    // 改已存在材质实例的颜色，不新建材质
    static void SetEmission(Material m, Color c, float intensity)
    {
        if (m == null) return;
        var col = Scale(c, intensity);
        m.color = col;
        if (m.HasProperty("_Color")) m.SetColor("_Color", col);
    }

    // ---------- 建几何的小工具 ----------

    GameObject Box(string name, Vector3 pos, Vector3 size, Material mat, Transform parent)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        var col = cube.GetComponent<Collider>();
        if (col != null) Destroy(col);          // 布景不参与射线，只有 chamber_hit / hub_walkfloor 能命中
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = pos;
        cube.transform.localScale = size;
        Paint(cube.GetComponent<MeshRenderer>(), mat);
        return cube;
    }

    // LineRenderer 画环（玻璃罩里的电子轨道：本来就该正对相机，alignment=View 正合适）
    Transform Ring(string name, Transform parent, Vector3 center, float radius, Color color,
        float width, int segments, Quaternion rot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localRotation = rot;

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
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = LineMaterial();
        return go.transform;
    }

    // 实体线圈：把环切成 segments 段短方管首尾相接。
    // 比 LineRenderer 贵一点（24 个 cube / 环），但斜着看也是真的环，不会变成贴在屏幕上的圆。
    void CoilRing(string name, Transform parent, Vector3 center, float radius, int segments,
        float thickness, Color color, Quaternion rot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localRotation = rot;

        var mat = Solid(color, 0.55f);
        float step = Mathf.PI * 2f / segments;
        // 弦长略放长 8%，段与段之间不留缝；转 45° 后长度按 x 轴算
        float segLen = 2f * radius * Mathf.Sin(step * 0.5f) * 1.08f;

        for (int i = 0; i < segments; i++)
        {
            float a = step * i;
            var seg = Box(name + "_seg",
                new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f),
                new Vector3(segLen, thickness, thickness), mat, go.transform);
            // 方管的长轴在 x，切线方向要再转 90°
            seg.transform.localRotation = Quaternion.Euler(0f, 0f, a * Mathf.Rad2Deg + 90f);
        }
    }

    Material LineMaterial()
    {
        if (_lineMat != null) return _lineMat;
        // LineRenderer 不吃材质的颜色，必须用吃顶点色的着色器。
        // Sprites/Default 在没引用 Sprite 的工程里会被剥离，兜底到 Unlit/Color 后
        // 顶点色失效、线全变白 —— 所以固定用 Resources 里的 ChargeLab/LineColor。
        var shader = Resources.Load<Shader>("Shaders/LineColor");
        if (shader == null) shader = Shader.Find("ChargeLab/LineColor");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        _lineMat = new Material(shader);
        _lineMat.name = "Hub_Line";
        return _lineMat;
    }

    Material Solid(Color color, float smoothness = 0.2f, Color? emission = null)
    {
        string key = "s|" + Key(color) + "|" + smoothness + "|" + (emission.HasValue ? Key(emission.Value) : "-");
        Material m;
        if (_matCache.TryGetValue(key, out m) && m != null) return m;

        var shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
        m = new Material(shader);
        m.name = "Hub_Solid";
        m.color = color;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        if (emission.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", emission.Value);
        }
        _matCache[key] = m;
        return m;
    }

    // 纯自发光：走 Resources 里的 ChargeLab/Glow（Unlit，颜色即最终颜色）。
    // Standard 的 _EMISSION 在打包后可能被剥离，运行时才发现「灯不亮」就太晚了。
    // 颜色超过 1 会被截断，所以 intensity 只做轻微提亮，明暗差靠色相拉开。
    static Material Emissive(Color color, float intensity)
    {
        var shader = Resources.Load<Shader>("Shaders/Glow");
        if (shader == null) shader = Shader.Find("ChargeLab/Glow");
        if (shader == null) shader = Shader.Find("Standard");

        var m = new Material(shader);
        m.name = "Hub_Glow";
        var c = Scale(color, intensity);
        m.color = c;
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    static Color Scale(Color c, float intensity)
    {
        return new Color(Mathf.Clamp01(c.r * intensity), Mathf.Clamp01(c.g * intensity),
            Mathf.Clamp01(c.b * intensity), c.a);
    }

    // 半透明玻璃：走 Resources 里自带的 ChargeLab/ChamberGlass。
    // 用 Standard 手动切透明在打包后可能静默退化成不透明（变体被剥离），
    // 玻璃罩又是必须能看穿的部件，所以这里固定用自己的 shader。
    static Material GlassMaterial(Color tint, float alpha)
    {
        var shader = Resources.Load<Shader>("Shaders/ChamberGlass");
        if (shader == null) shader = Shader.Find("ChargeLab/ChamberGlass");
        if (shader == null) shader = Shader.Find("Standard");

        var m = new Material(shader);
        m.name = "Hub_Glass";
        m.color = new Color(tint.r, tint.g, tint.b, alpha);
        if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(tint.r, tint.g, tint.b, alpha));
        m.renderQueue = 3000;
        return m;
    }

    static string Key(Color c)
    {
        return Mathf.RoundToInt(c.r * 255f) + "_" + Mathf.RoundToInt(c.g * 255f) + "_" +
               Mathf.RoundToInt(c.b * 255f) + "_" + Mathf.RoundToInt(c.a * 255f);
    }

    static void Paint(Renderer r, Material m)
    {
        if (r != null && m != null) r.sharedMaterial = m;
    }

    // ---------- 文字 / 双语 ----------

    TextMesh MakeText(string name, string key, Vector3 pos, int fontSize, float charSize,
        Color color, TextAnchor anchor, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;

        var tm = go.AddComponent<TextMesh>();
        tm.font = BuiltInFont();
        tm.text = UILang.T(key);
        tm.fontSize = fontSize;
        tm.characterSize = charSize;
        tm.anchor = anchor;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        tm.richText = false;

        var renderer = go.GetComponent<MeshRenderer>();
        if (tm.font != null) renderer.sharedMaterial = tm.font.material;
        _texts.Add(tm);
        return tm;
    }

    Font BuiltInFont()
    {
        if (_font != null) return _font;
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return _font;
    }

    void LimitWidth(TextMesh tm, float maxW)
    {
        if (tm != null) _textMaxWidth[tm] = maxW;
    }

    void RefreshTexts()
    {
        foreach (var tm in _texts)
        {
            if (tm == null) continue;
            tm.text = UILang.T(KeyOf(tm));
            FitTextWidth(tm);
        }
    }

    // 名字 → 词条 key。名字是唯一的锚，换语言时靠它回查，不依赖排版顺序。
    // 三台舱的铭牌名字带序号（chamber_title_1..3），按后缀拼回 hub.stN.title。
    static string KeyOf(TextMesh tm)
    {
        var n = tm.gameObject.name;
        const string titlePrefix = "chamber_title_";
        const string subPrefix = "chamber_sub_";
        if (n.StartsWith(titlePrefix)) return "hub.st" + n.Substring(titlePrefix.Length) + ".title";
        if (n.StartsWith(subPrefix)) return "hub.st" + n.Substring(subPrefix.Length) + ".sub";
        return n;
    }

    // 英文比中文长得多，同字号会顶出铭牌；量实际渲染宽度按比例缩回去
    void FitTextWidth(TextMesh tm)
    {
        float maxW;
        if (!_textMaxWidth.TryGetValue(tm, out maxW)) return;

        float baseSize = tm.gameObject.name.StartsWith("chamber_title") ? 0.026f : 0.019f;
        tm.characterSize = baseSize;

        var r = tm.GetComponent<MeshRenderer>();
        if (r == null) return;
        float w = r.bounds.size.x;
        if (w > 0.0001f && w > maxW) tm.characterSize = baseSize * (maxW / w);
    }

    // ---------- OnGUI：提示 / 准星 / 返回按钮 / 黑场 ----------

    void OnGUI()
    {
        if (!_enabled) return;

        var virt = UIScale.BeginGUI();
        BuildStyles();

        if (!_transitioning && InHub)
        {
            float cx = virt.x * 0.5f;
            float cy = virt.y * 0.5f;
            // 准星：悬停在舱体上时变亮变粗，等于「这里能点」
            var dotColor = _hoverStation != null
                ? new Color(0.55f, 0.95f, 1f, 0.95f)
                : new Color(1f, 1f, 1f, 0.5f);
            float d = _hoverStation != null ? 7f : 5f;
            UIScale.Fill(new Rect(cx - d, cy - d, d * 2f, d * 2f), dotColor);

            var text = UILang.T("hub.hint");
            var size = _hintStyle.CalcSize(new GUIContent(text));
            var rect = new Rect((virt.x - size.x) * 0.5f, 14f, size.x + 44f, 46f);
            UIScale.Fill(new Rect(rect.x, rect.y + 3f, rect.width, rect.height), new Color(0.05f, 0.08f, 0.13f, 0.55f));
            UIScale.Fill(rect, new Color(0.10f, 0.14f, 0.22f, 0.88f));
            UIScale.Outline(rect, new Color(1f, 1f, 1f, 0.14f));
            GUI.Label(new Rect(rect.x, rect.y, rect.width, rect.height), text, _hintStyle);
        }
        else if (!_transitioning && !InHub && !PlateFieldModule.Active)
        {
            if (_current != null && _current.Mystery) DrawMystery(virt);

            // 往上让一截：左下角还要摆磁场图例（B = 1 T ⊙ …），原来两个叠在一起
            var rect = new Rect(24f, virt.y - 96f - 52f, 224f, 52f);
            var mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
            bool hot = rect.Contains(mouse);
            UIScale.Fill(new Rect(rect.x, rect.y + 3f, rect.width, rect.height), UITheme.CardShadow);
            UIScale.Fill(rect, hot ? UITheme.ButtonBgIdle : UITheme.ButtonBg);
            UIScale.Outline(rect, hot ? UITheme.Accent : UITheme.CardBorder);
            GUI.Label(rect, UILang.T("hub.back"), _backStyle);
            if (hot && Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                Event.current.Use();
                ReturnToHub();
            }
        }

        if (_fade > 0.001f)
            GUI.DrawTexture(new Rect(0f, 0f, virt.x, virt.y), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, _fade), 0f, 0f);
    }

    // 未知粒子站的作答卡：四个候选 + 判定 + 结论。
    // 放在左侧而不是右上角，是因为右上角被参数面板占了（那里正显示着 q ? 和 m ?）。
    void DrawMystery(Vector2 virt)
    {
        const float w = 432f;
        const float x = 24f;
        // 从 y=285 起：左上角那块「实时数据」卡画到 ~250（虚拟坐标），再往上会压住它
        const float y = 285f;
        float h = _answer < 0 ? 336f : 286f;

        UIScale.Fill(new Rect(x, y + 3f, w, h), new Color(0.02f, 0.04f, 0.06f, 0.45f));
        UIScale.Fill(new Rect(x, y, w, h), new Color(0.09f, 0.12f, 0.17f, 0.94f));
        UIScale.Outline(new Rect(x, y, w, h), new Color(1f, 1f, 1f, 0.16f));

        float cx = x + 18f;
        float cy = y + 14f;
        GUI.Label(new Rect(cx, cy, w - 36f, 30f), UILang.T("hub.mys.title"), _cardTitleStyle);
        cy += 34f;
        GUI.Label(new Rect(cx, cy, w - 36f, 46f), UILang.T("hub.mys.tip"), _cardStyle);
        cy += 54f;

        if (_answer < 0)
        {
            string[] keys = { "hub.mys.a", "hub.mys.b", "hub.mys.c", "hub.mys.d" };
            for (int i = 0; i < keys.Length; i++)
            {
                var rect = new Rect(cx, cy, w - 36f, 36f);
                if (GUI.Button(rect, UILang.T(keys[i]), _choiceStyle))
                {
                    _answer = i;
                    _answerRight = i == MysteryAnswer;
                }
                cy += 42f;
            }
            return;
        }

        // 判完之后候选按钮全部撤掉：留着还能点，这题就变成四选一的猜。
        GUI.Label(new Rect(cx, cy, w - 36f, 46f),
            UILang.T(_answerRight ? "hub.mys.right" : "hub.mys.wrong"), _cardStyle);
        cy += 50f;

        if (_answerRight)
        {
            GUI.Label(new Rect(cx, cy, w - 36f, 66f), UILang.T("hub.mys.note"), _cardStyle);
            cy += 68f;
        }

        if (GUI.Button(new Rect(cx, cy, 150f, 34f), UILang.T("hub.mys.retry"), _choiceStyle))
        {
            _answer = -1;
            _answerRight = false;
        }
    }

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            alignment = TextAnchor.MiddleCenter,
        };
        _hintStyle.normal.textColor = new Color(0.88f, 0.93f, 1.00f);

        _backStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            alignment = TextAnchor.MiddleCenter,
        };
        _backStyle.normal.textColor = new Color(0.88f, 0.93f, 1.00f);

        _cardTitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
        };
        _cardTitleStyle.normal.textColor = new Color(0.76f, 0.93f, 1.00f);

        _cardStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 17,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
        };
        _cardStyle.normal.textColor = new Color(0.84f, 0.89f, 0.95f);

        _choiceStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(14, 10, 4, 4),
        };
        // 默认 button 的文字色在深色卡片上几乎看不清，这里逐个状态写死
        _choiceStyle.normal.textColor = new Color(0.88f, 0.93f, 1.00f);
        _choiceStyle.hover.textColor = new Color(1f, 1f, 1f);
        _choiceStyle.active.textColor = new Color(1f, 1f, 1f);
    }
}
