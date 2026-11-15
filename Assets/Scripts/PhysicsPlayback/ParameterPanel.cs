using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// 右上角的「本次实验用了什么参数」面板。
// 只读部分直接来自当前 Output；按 P 展开编辑区，改完点「重算并播放」由 C# 求解器重算后上屏。
// 全程 OnGUI，不建 Canvas、不动场景，和负责配色的人不会抢同一个文件。
public sealed class ParameterPanel : MonoBehaviour
{
    public PlaybackController player;
    public SceneBinder binder;

    public float fontSize = 22f;
    public float panelWidth = 520f;
    public float margin = 30f;
    public float gridCellSize = 1f;

    [Tooltip("按这个键展开 / 收起参数编辑区")]
    public KeyCode toggleEditKey = KeyCode.P;

    const float FieldHeight = 28f;
    const float FieldWidth = 74f;

    // 课时 3 的「未知粒子」站：q、m 与理论 r、T 全部抹掉，只留 v0 和 B。
    // 学生要靠网格量出半径、再用 r = mv/(qB) 反推，直接看到答案就没得推了。
    // 这是静态开关而不是场景字段：大厅进站前设一次，退出时复位。
    public static bool MaskSolution;

    // 每堂课「允许动哪个量」本身就是教学设计，所以由大厅按站设置，不放在场景里。
    // 课时 1 全锁：先看现象再归纳，一放开就变成试错。
    // 课时 2 只留速度：其余量冻结，学生物理上不可能一次改两个，控制变量由界面替他守住。
    public enum LockMode { None, All, SpeedOnly }
    public static LockMode Lock;

    // 截图脚本用：-shotparams 传进来就强制展开编辑区。
    // 人手按 P 复现不了精确时机，验收图只能靠这条开关。
    public static bool ForceEditOpen;

    // 速度的两个分量必须一起放开，否则「改速度」会偷偷把方向也改掉。
    static bool IsLocked(string field)
    {
        switch (Lock)
        {
            case LockMode.All:       return true;
            case LockMode.SpeedOnly: return field != "vx" && field != "vy";
            default:                 return false;
        }
    }

    GUIStyle _textStyle;
    GUIStyle _titleStyle;
    GUIStyle _fieldStyle;
    GUIStyle _messageStyle;
    GUIStyle _lockedStyle;     // 冻结字段的灰字
    GUIStyle _lockTipStyle;    // 面板底部那句「本关能改什么」
    SimulationOutput _cached;
    string _cachedBody;
    float _cachedWidth = -1f;
    UILangId _cachedLang = UILangId.Zh;   // 切语言时正文要重拼
    bool _cachedMask;

    // ---- 编辑态 ----
    bool _editing;
    bool _cliChecked;   // 命令行开关只查一次
    SimulationSolver.Setup _baseSetup;   // 原算例参数，编辑时以它为基准做副本
    List<Row> _rows = new List<Row>();
    string _durationText = string.Empty;
    string _bzText = string.Empty;       // 全局磁场强度，所有粒子共用
    string _message = string.Empty;
    bool _messageIsError;

    sealed class Row
    {
        public string id;
        // 电荷量：正负号决定转弯方向，是这一版最重要的可改参数。
        public string charge;
        public string mass;
        // 速度的 x / y 分量必须分开存。原来只有一个 velocity 字段，
        // 写进 body.vx 之后 vy 保持原值，改速度等于偷偷改了方向。
        public string vx;
        public string vy;
        public string x0;
        public string y0;
    }

    void Awake()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (binder == null) binder = FindObjectOfType<SceneBinder>();
    }

    void OnEnable()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (player != null) player.Loaded += OnLoaded;
    }

    void OnDisable()
    {
        if (player != null) player.Loaded -= OnLoaded;
    }

    void Update()
    {
        // 截图脚本用：面板自己再读一次命令行，不依赖大厅那边的时序。
        if (!_cliChecked)
        {
            _cliChecked = true;
            foreach (var a in System.Environment.GetCommandLineArgs())
            {
                if (a == "-shotparams" || a == "-openparams") ForceEditOpen = true;
            }
        }

        // 未知粒子站：编辑区一律不开，否则学生点 P 就能从输入框里读到 q 和 m
        if (MaskSolution) { _editing = false; return; }
        if (ForceEditOpen) { _editing = true; return; }
        if (Input.GetKeyDown(toggleEditKey)) _editing = !_editing;
    }

    // 每次加载（读文件或重算）都重建输入框，避免画面与输入框里的数字不一致。
    void OnLoaded(SimulationOutput output)
    {
        _baseSetup = SimulationSolver.Capture(output);
        RebuildRows();
    }

    void RebuildRows()
    {
        _rows.Clear();
        _message = string.Empty;
        _messageIsError = false;

        if (_baseSetup == null) return;

        _durationText = _baseSetup.duration.ToString("0.00", CultureInfo.InvariantCulture);
        _bzText = _baseSetup.Bz.ToString("0.00", CultureInfo.InvariantCulture);
        foreach (var body in _baseSetup.bodies)
        {
            _rows.Add(new Row
            {
                id = body.id,
                charge = body.charge.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
                mass = body.mass.ToString("0.00", CultureInfo.InvariantCulture),
                vx = body.vx.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
                vy = body.vy.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
                x0 = body.x.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
                y0 = body.y.ToString("+0.00;-0.00", CultureInfo.InvariantCulture),
            });
        }
    }

    void OnGUI()
    {
        // OnGUI 的绘制顺序由 GUI.depth 决定（越小越靠上）。网格刻度和球速标签都是深度 0，
        // 面板不设深度就会被它们画在身上，看起来像文字穿透卡片。
        GUI.depth = -10;

        if (player == null || player.Loader.Output == null) return;

        var output = player.Loader.Output;

        // 与 CanvasScaler（match=0.5）完全相同的换算，见 UIScale。
        // 原来写 Screen.height/1080，16:9 以外会和 UGUI 的 HUD 差一截，看着像没对齐。
        var virtualSize = UIScale.BeginGUI();
        float virtualWidth = virtualSize.x;

        EnsureStyles();

        // 参数只在换数据集时变，不必每帧拼字符串
        if (_cached != output || !Mathf.Approximately(_cachedWidth, virtualWidth) ||
            _cachedLang != UILang.Current || _cachedMask != MaskSolution)
        {
            _cached = output;
            _cachedWidth = virtualWidth;
            _cachedLang = UILang.Current;
            _cachedMask = MaskSolution;
            _cachedBody = BuildBody(output);
        }

        float lineHeight = fontSize + 8f;
        int lineCount = 1;
        foreach (char c in _cachedBody) if (c == '\n') lineCount++;
        float readOnlyHeight = lineCount * lineHeight + 24f;

        float editorHeight = _editing ? MeasureEditor(lineHeight) : 0f;
        float height = readOnlyHeight + editorHeight;

        float x = virtualWidth - panelWidth - margin;
        float y = margin;
        if (x < margin) x = margin;

        // 与 HUD 卡片、控制条同一套卡片样式（白底 + 1px 边框 + 淡阴影）
        UIScale.Card(new Rect(x, y, panelWidth, height));

        _titleStyle.normal.textColor = UITheme.TextPrimary;
        _textStyle.normal.textColor = new Color(UITheme.TextPrimary.r, UITheme.TextPrimary.g, UITheme.TextPrimary.b, 0.92f);

        string titleText = UILang.T("panel.title") + "    " +
                           UILang.T(_editing ? "panel.hintClose" : "panel.hintEdit");
        GUI.Label(new Rect(x + 14f, y + 10f, panelWidth - 28f, lineHeight), titleText, _titleStyle);
        GUI.Label(new Rect(x + 14f, y + 12f + lineHeight, panelWidth - 28f, readOnlyHeight - lineHeight - 20f),
                  _cachedBody, _textStyle);

        if (_editing) DrawEditor(x, y + readOnlyHeight, lineHeight);
    }

    void EnsureStyles()
    {
        if (_textStyle != null) return;

        _textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(fontSize),
            alignment = TextAnchor.UpperLeft,
            richText = false,
        };
        _titleStyle = new GUIStyle(_textStyle) { fontStyle = FontStyle.Bold };

        // 输入框字号小一点、右对齐，数字按小数点对齐才好读。
        _fieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = Mathf.RoundToInt(fontSize * 0.9f),
            alignment = TextAnchor.MiddleRight,
            padding = new RectOffset(4, 6, 4, 4),
        };

        _messageStyle = new GUIStyle(_textStyle) { fontSize = Mathf.RoundToInt(fontSize * 0.85f) };

        // 冻结量用灰字画在原输入框的位置上：既不消失（学生看得见数值是多少），
        // 也不像输入框（不会想去点）。
        _lockedStyle = new GUIStyle(_textStyle);
        _lockedStyle.fontSize = Mathf.RoundToInt(fontSize * 0.9f);
        _lockedStyle.alignment = TextAnchor.MiddleRight;
        _lockedStyle.normal.textColor = UITheme.TextMuted;

        _lockTipStyle = new GUIStyle(_textStyle);
        _lockTipStyle.fontSize = Mathf.RoundToInt(fontSize * 0.82f);
        _lockTipStyle.wordWrap = true;
        _lockTipStyle.normal.textColor = new Color(UITheme.TextPrimary.r, UITheme.TextPrimary.g,
                                                   UITheme.TextPrimary.b, 0.78f);
    }

    float MeasureEditor(float lineHeight)
    {
        float width = panelWidth - 28f;
        float height = 12f + 1f + 12f;                        // 分隔线 + 上下留白
        if (Lock == LockMode.None) height += TipHeight(UILang.T("panel.tipEdit"), width) + 6f;
        height += _rows.Count * (FieldHeight + 8f) * 2f;      // 每球两行：标量一行、矢量分量一行
        height += (FieldHeight + 12f) * 2f;                   // B 行 + duration 行
        // 锁定时不画按钮，改画说明。说明会换行，必须按实际行数留高度，
        // 否则卡片装不下、最后一句被切在卡片外面。
        height += Lock == LockMode.None ? 42f : LockTipHeight(width) + 20f;
        if (!string.IsNullOrEmpty(_message)) height += lineHeight * 2f;
        return height + 16f;
    }

    // 这两句说明都是长句，宽度不够就折行，所以高度只能问样式要，不能按行数拍。
    float TipHeight(string text, float width)
    {
        _messageStyle.wordWrap = true;
        return Mathf.Max(lineHeight_Px(), _messageStyle.CalcHeight(new GUIContent(text), width));
    }

    float LockTipHeight(float width)
    {
        return Mathf.Max(lineHeight_Px() * 2f,
                         _lockTipStyle.CalcHeight(new GUIContent(CurrentLockTip()), width));
    }

    string CurrentLockTip() =>
        UILang.T(Lock == LockMode.SpeedOnly ? "panel.lock.speed" : "panel.lock.all");

    float lineHeight_Px() => fontSize + 8f;

    void DrawEditor(float panelX, float top, float lineHeight)
    {
        float left = panelX + 14f;
        float width = panelWidth - 28f;
        float y = top + 12f;

        Fill(new Rect(left, y, width, 1f), new Color(0.75f, 0.78f, 0.83f, 1f));
        y += 12f;

        // 「改完点重算」这句只在能改的时候有意义；锁定时它和现状矛盾，直接不画。
        if (Lock == LockMode.None)
        {
            var tipText = UILang.T("panel.tipEdit");
            float tipH = TipHeight(tipText, width);
            GUI.Label(new Rect(left, y, width, tipH), tipText, _messageStyle);
            y += tipH + 6f;
        }

        if (_baseSetup == null || _rows.Count == 0)
        {
            GUI.Label(new Rect(left, y, width, lineHeight), UILang.T("panel.noSetup"), _messageStyle);
            return;
        }

        // 磁场版全是二维，没有 1d 分支
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            float cx = left;

            // id 宽度要够放下 "ball_1" 这种名字
            GUI.Label(new Rect(cx, y, 66f, FieldHeight), row.id, _textStyle);

            // 第一行：电荷 + 质量。电荷排最前 —— 正负号是这一版的核心变量。
            cx = left + 70f;
            cx = DrawField(cx, y, "q", ref row.charge, "q");
            DrawField(cx, y, "m", ref row.mass, "m");
            y += FieldHeight + 8f;

            cx = left + 70f;
            cx = DrawField(cx, y, "vx", ref row.vx, "vx");
            cx = DrawField(cx, y, "vy", ref row.vy, "vy");
            cx = DrawField(cx, y, "x0", ref row.x0, "x0");
            DrawField(cx, y, "y0", ref row.y0, "y0");

            y += FieldHeight + 8f;
        }

        // 全局磁场：所有粒子共用，单独一行放在粒子区下面
        GUI.Label(new Rect(left, y + 6f, 96f, FieldHeight), UILang.T("panel.fieldB"), _textStyle);
        DrawScalarField(new Rect(left + 104f, y + 6f, FieldWidth, FieldHeight), ref _bzText, "B");
        y += FieldHeight + 12f;

        // 时长决定了「播几圈」。学生一旦能改它，圈数就成了可以随手操纵的数字，
        // 「两颗球同时转完一圈」这个证据立刻失效 —— 所以它跟着锁定一起冻结。
        GUI.Label(new Rect(left, y + 6f, 96f, FieldHeight), "duration", _textStyle);
        DrawScalarField(new Rect(left + 104f, y + 6f, FieldWidth, FieldHeight), ref _durationText, "duration");
        y += FieldHeight + 12f;

        if (Lock == LockMode.None)
        {
            if (GUI.Button(new Rect(left, y, 240f, 36f), UILang.T("panel.recompute"))) ApplyEdits();
            if (GUI.Button(new Rect(left + 250f, y, 150f, 36f), UILang.T("panel.restore"))) RestoreOriginal();
            y += 42f;
        }
        else
        {
            // 锁定时把按钮换成说明：学生按下 P 第一眼要看到的就是「这关为什么不能改」。
            string tip = CurrentLockTip();
            float lockH = LockTipHeight(width);
            GUI.Label(new Rect(left, y, width, lockH), tip, _lockTipStyle);
            y += lockH + 20f;
        }

        if (!string.IsNullOrEmpty(_message))
        {
            _messageStyle.normal.textColor = _messageIsError
                ? new Color(0.72f, 0.16f, 0.16f, 1f)
                : new Color(0.10f, 0.45f, 0.20f, 1f);
            GUI.Label(new Rect(left, y, width, lineHeight * 2f), _message, _messageStyle);
        }
    }

    float DrawField(float x, float y, string label, ref string value, string field)
    {
        GUI.Label(new Rect(x, y, 26f, FieldHeight), label, _textStyle);
        DrawScalarField(new Rect(x + 26f, y, FieldWidth, FieldHeight), ref value, field);
        return x + 26f + FieldWidth + 8f;
    }

    // field 既是字段名也是锁定判据：LockMode 决定这个量这一关能不能动。
    void DrawScalarField(Rect rect, ref string value, string field)
    {
        if (!IsLocked(field))
        {
            var next = GUI.TextField(rect, value, 12, _fieldStyle);
            if (next != value) value = next;
            return;
        }

        // 冻结量照原样显示数值 —— 学生要看得见「这个数是多少，只是动不了」，
        // 变成空白或干脆消失都会被当成加载失败。
        GUI.Label(rect, value, _lockedStyle);
    }

    void ApplyEdits()
    {
        if (binder == null || _baseSetup == null) return;

        var setup = _baseSetup.Clone();

        for (int i = 0; i < _rows.Count && i < setup.bodies.Count; i++)
        {
            var row = _rows[i];
            var body = setup.bodies[i];

            if (!TryParse(row.charge, out body.charge)) { ReportError(UILang.F("panel.err.charge", row.id)); return; }
            if (!TryParse(row.mass, out body.mass)) { ReportError(UILang.F("panel.err.mass", row.id)); return; }
            if (!TryParse(row.vx, out body.vx)) { ReportError(UILang.F("panel.err.vx", row.id)); return; }
            if (!TryParse(row.x0, out body.x)) { ReportError(UILang.F("panel.err.x0", row.id)); return; }

            if (!TryParse(row.vy, out body.vy)) { ReportError(UILang.F("panel.err.vy", row.id)); return; }
            if (!TryParse(row.y0, out body.y)) { ReportError(UILang.F("panel.err.y0", row.id)); return; }
        }

        if (!TryParse(_bzText, out setup.Bz)) { ReportError(UILang.T("panel.err.bz")); return; }
        if (!TryParse(_durationText, out setup.duration)) { ReportError(UILang.T("panel.err.dur")); return; }

        // 先自校验再求解：重叠、越界这类错误在求解器里也会拦，但这里能更早给出中文提示。
        var error = SimulationSolver.Validate(setup);
        if (!string.IsNullOrEmpty(error)) { ReportError(error); return; }

        if (!binder.ApplySetup(setup)) { ReportError(UILang.T("panel.err.run")); return; }

        // 成功后才把当前值定为新基准；"恢复原算例"仍然回到 JSON 的原始数据。
        _baseSetup = setup;
        _message = UILang.F("panel.ok.run", setup.bodies.Count,
                              setup.duration.ToString("0.00", CultureInfo.InvariantCulture));
        _messageIsError = false;
    }

    void RestoreOriginal()
    {
        if (binder == null || string.IsNullOrEmpty(binder.jsonFileName)) return;

        binder.LoadFile(binder.jsonFileName);
        _message = UILang.T("panel.ok.restore");
        _messageIsError = false;
    }

    void ReportError(string message)
    {
        _message = message;
        _messageIsError = true;
    }

    // 求解器内部统一用 double，这里必须同样返回 double：out 参数要求类型精确匹配，
    // 写成 float 会让每个 Setup 字段都报 CS1503。
    static bool TryParse(string text, out double value)
    {
        // 固定 InvariantCulture：中文系统小数点也是 "."，但要防系统区域设置改成逗号。
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    static void Fill(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    string BuildBody(SimulationOutput output)
    {
        var sb = new StringBuilder();
        var scenario = output.scenario;

        if (scenario != null)
        {
            sb.Append(UILang.T("panel.dt")).Append(' ').Append(scenario.time_step.ToString("0.000")).Append(" s")
              .Append("    ").Append(UILang.T("panel.duration")).Append(' ').Append(scenario.duration.ToString("0.00")).Append(" s\n");

            if (scenario.bounds != null && scenario.bounds.min != null && scenario.bounds.max != null &&
                scenario.bounds.min.Length >= 2 && scenario.bounds.max.Length >= 2)
            {
                sb.Append(UILang.T("panel.bounds")).Append(" x [").Append(scenario.bounds.min[0].ToString("0.##"))
                  .Append(", ").Append(scenario.bounds.max[0].ToString("0.##"))
                  .Append("]   y [").Append(scenario.bounds.min[1].ToString("0.##"))
                  .Append(", ").Append(scenario.bounds.max[1].ToString("0.##")).Append("]\n");
            }

            sb.Append(UILang.T("panel.fieldB")).Append(' ')
              .Append((scenario.Bz > 0 ? "+" : "") + scenario.Bz.ToString("0.##")).Append(" T");
            // B = 0 时没有「穿出 / 穿入纸面」可言，硬写 ⊙ 会让学生以为场还在
            if (System.Math.Abs(scenario.Bz) < 1e-12)
                sb.Append(' ').Append(UILang.T("panel.fieldNone"));
            else
                sb.Append(' ').Append(UILang.T(scenario.Bz > 0 ? "panel.fieldOut" : "panel.fieldIn"));
            sb.Append('\n');
        }

        if (output.objects != null && output.objects.Length > 0)
        {
            sb.Append('\n');
            double Bz = scenario != null ? scenario.Bz : 0.0;

            foreach (var body in output.objects)
            {
                if (body == null) continue;

                sb.Append(body.id);
                if (MaskSolution)
                {
                    // 未知粒子站：只留 id 和 v0，q / m / r / T 一律不露
                    sb.Append("  q ?").Append("  m ?");
                }
                else
                {
                    sb.Append("  q ").Append((body.charge >= 0f ? "+" : "") + body.charge.ToString("0.00")).Append(" C")
                      .Append("  m ").Append(body.mass.ToString("0.00")).Append(" kg");
                }

                if (body.velocity != null && body.velocity.Length >= 2)
                {
                    sb.Append("  v0 (").Append(Signed(body.velocity[0])).Append(", ").Append(Signed(body.velocity[1])).Append(")");
                }

                // 理论圆周：r = m|v|/(|q|B)，周期 T = 2πm/(|q|B)。
                // 只读区直接给出公式结果，学生改参数后立即对照屏幕上的参考圈验证。
                double speed = 0.0;
                if (body.velocity != null && body.velocity.Length >= 2)
                    speed = System.Math.Sqrt(body.velocity[0] * (double)body.velocity[0] +
                                             body.velocity[1] * (double)body.velocity[1]);
                double qB = body.charge * Bz;
                if (!MaskSolution && System.Math.Abs(qB) > 1e-12 && speed > 1e-12)
                {
                    double r = body.mass * speed / System.Math.Abs(qB);
                    double period = 2.0 * System.Math.PI * body.mass / System.Math.Abs(qB);
                    sb.Append('\n')
                      .Append("   ").Append(UILang.T("panel.radius")).Append(" r = mv/(|q|B) = ").Append(r.ToString("0.00")).Append(" m")
                      .Append("    ").Append(UILang.T("panel.period")).Append(" T = ").Append(period.ToString("0.00")).Append(" s");
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    static string Signed(float value) => (value >= 0f ? "+" : "") + value.ToString("0.00");
}
