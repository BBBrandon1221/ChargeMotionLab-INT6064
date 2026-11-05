using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 右下角的 AI 实验助教对话窗。
//
// 三个设计约束，决定了它为什么长这样：
//   1. 默认折叠，只在角落留一个小按钮——答辩时画面要干净，AI 不能挡住实验。
//   2. 每次请求都重算两条 system 消息（角色 + 当前实验数据），所以学生改完参数再问，
//      AI 看到的就是新数据，不会拿旧数字糊弄人。
//   3. 请求失败时把问题撤回输入框、不留半截对话——现场网络不可控，重试要一步到位。
//
// 全部用 OnGUI 画（与 ParameterPanel 一致），不建 Canvas、不动场景文件。
public sealed class AIChatPanel : MonoBehaviour
{
    public PlaybackController player;
    public SceneBinder binder;

    [Tooltip("按这个键展开 / 收起对话窗")]
    public KeyCode toggleKey = KeyCode.C;

    public float fontSize = 20f;
    public float panelWidth = 660f;
    public float panelHeight = 580f;
    public float margin = 30f;

    [Tooltip("最多带多少轮历史（一轮 = 问 + 答），超出的丢弃，避免 token 无限增长")]
    public int maxRounds = 6;


    readonly List<ChatMessage> _history = new List<ChatMessage>();

    AIConfigData _config;
    DeepSeekClient _client;

    bool _open;
    bool _waiting;
    bool _scrollToBottom;
    string _pendingQuestion = string.Empty;

    // 窗口左上角（1080 虚拟坐标）。默认停右下角，但允许按住标题栏拖开——
    // 窗口总会挡住一部分轨迹，能不能拖走决定了它到底是"帮助"还是"碍事"。
    Vector2 _panelPos = new Vector2(-1f, -1f);
    bool _dragging;
    Vector2 _dragGrab;

    string _input = string.Empty;
    string _keyInput = string.Empty;
    string _status = string.Empty;
    bool _statusIsError;

    Vector2 _scroll;

    GUIStyle _titleStyle;
    GUIStyle _textStyle;
    GUIStyle _hintStyle;
    GUIStyle _bubbleUser;
    GUIStyle _bubbleAi;
    GUIStyle _fieldStyle;
    GUIStyle _statusStyle;
    GUIStyle _buttonStyle;
    GUIStyle _buttonLightStyle;

    void Awake()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (binder == null) binder = FindObjectOfType<SceneBinder>();
        ReloadConfig();
    }

    void OnEnable()
    {
        if (player == null) player = FindObjectOfType<PlaybackController>();
        if (binder == null) binder = FindObjectOfType<SceneBinder>();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _open = !_open;
    }

    void ReloadConfig()
    {
        // 配置只在启动和手动"重读"时解析：每帧读文件会卡主线程。
        _config = AIConfig.Load();
        _client = _config != null ? new DeepSeekClient(_config) : null;

        if (_config == null)
        {
            Debug.Log("[AI] 没找到可用的 ai_config.json，对话窗会提示填入 key");
            return;
        }

        Debug.Log("[AI] 配置已加载：" + AIConfig.Describe(_config));
    }

    // ---------------- 绘制 ----------------

    void OnGUI()
    {
        // 比参数面板再靠上一层：两个面板可能相邻，AI 面板是用户主动唤出的，应该在最上面
        GUI.depth = -20;

        if (player == null) return;

        var virtualSize = UIScale.BeginGUI();
        float virtualWidth = virtualSize.x;
        float virtualHeight = virtualSize.y;

        EnsureStyles();

        // 折叠态固定贴右下角；展开后的位置由拖动决定，所以两者分开算
        float dockX = Mathf.Max(margin, virtualWidth - panelWidth - margin);
        // 再抬高一截：面板底边压住控制条时，最右边的语言按钮就点不到了。
        // 76 = 控制条高，20 = 它与屏幕底的距离。
        float dockY = Mathf.Max(margin, virtualHeight - panelHeight - margin - 92f);

        // 折叠态：右下角一个按钮，别让 AI 挡住实验画面
        if (!_open)
        {
            // 与底部控制条同高同基线（条高 76、距底 20），视觉上属于同一条工具带
            var buttonRect = new Rect(Mathf.Max(margin, virtualWidth - 30f - 190f), virtualHeight - 20f - 76f, 190f, 76f);
            if (DrawButton(buttonRect, UILang.F("ai.button", toggleKey), true)) _open = true;
            return;
        }

        // 首次展开停在右下角；之后每次拖动都夹回屏幕内，不会拖丢
        if (_panelPos.x < 0f || _panelPos.y < 0f) _panelPos = new Vector2(dockX, dockY);
        _panelPos.x = Mathf.Clamp(_panelPos.x, 0f, Mathf.Max(0f, virtualWidth - panelWidth));
        _panelPos.y = Mathf.Clamp(_panelPos.y, 0f, Mathf.Max(0f, virtualHeight - panelHeight));

        DrawPanel(new Rect(_panelPos.x, _panelPos.y, panelWidth, panelHeight));
    }

    // 给其它 OnGUI 层（球速标签）避让用。读到的是上一帧的位置，够用了：
    // 面板要么不动，要么被人拖着走，差一帧看不出来。
    public bool IsOpen
    {
        get { return _open; }
    }

    public Rect OccupiedRect
    {
        get
        {
            if (!_open || _panelPos.x < 0f || _panelPos.y < 0f) return new Rect();
            return new Rect(_panelPos.x, _panelPos.y, panelWidth, panelHeight);
        }
    }

    // 供自动截图用：命令行启动时直接把面板打开，方便核对文案排版
    public void OpenPanel()
    {
        _open = true;
    }

    void DrawPanel(Rect rect)
    {
        // 浅灰描边 + 近白底，与参数面板同一套配色
        Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.72f, 0.75f, 0.80f, 1f));
        Fill(rect, new Color(0.98f, 0.98f, 0.99f, 0.97f));

        float left = rect.x + 14f;
        float width = rect.width - 28f;
        float bottom = rect.y + rect.height;

        // ---- 标题栏（同时是拖动把手）----
        Fill(new Rect(rect.x, rect.y, rect.width, 34f), new Color(0.90f, 0.92f, 0.95f, 1f));
        HandleDrag(new Rect(rect.x, rect.y, rect.width - 210f, 34f));

        _titleStyle.normal.textColor = new Color(0.12f, 0.14f, 0.19f, 1f);
        GUI.Label(new Rect(left, rect.y + 6f, width - 210f, 26f), UILang.T("ai.title"), _titleStyle);

        if (DrawButton(new Rect(rect.x + rect.width - 200f, rect.y + 6f, 96f, 26f), UILang.T("ai.clear"), false))
        {
            _history.Clear();
            _status = string.Empty;
            _statusIsError = false;
        }

        if (DrawButton(new Rect(rect.x + rect.width - 98f, rect.y + 6f, 84f, 26f), UILang.T("ai.close"), false))
            _open = false;

        // ---- 纵向分区：消息区 / 预设问题（每行一个）/ 输入行 / 状态行 ----
        float statusY = bottom - 26f;
        float inputY = statusY - 40f;
        float presetHeight = ExperimentPrompts.PresetQuestions.Length * 34f;
        float presetTop = inputY - 8f - presetHeight;

        var messageArea = new Rect(left, rect.y + 42f, width, Mathf.Max(90f, presetTop - rect.y - 42f - 8f));

        if (_client != null && _client.Ready) DrawMessages(messageArea);
        else DrawKeySetup(messageArea);

        DrawPresets(new Rect(left, presetTop, width, presetHeight));

        // ---- 输入行 ----
        GUI.SetNextControlName("AIChatInput");
        _input = GUI.TextField(new Rect(left, inputY, width - 100f, 34f), _input, 400, _fieldStyle);

        bool wasWaiting = _waiting;
        if (_waiting) GUI.enabled = false;
        if (DrawButton(new Rect(left + width - 92f, inputY, 92f, 34f), UILang.T(_waiting ? "ai.waiting" : "ai.send"), true)) Send(_input);
        GUI.enabled = true;

        // 回车发送：只在焦点落在输入框时生效，避免和别的 GUI 抢按键
        var evt = Event.current;
        if (!wasWaiting &&
            evt.type == EventType.KeyDown &&
            (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) &&
            GUI.GetNameOfFocusedControl() == "AIChatInput")
        {
            evt.Use();
            Send(_input);
        }

        // ---- 状态行 ----
        if (!string.IsNullOrEmpty(_status))
        {
            _statusStyle.normal.textColor = _statusIsError
                ? new Color(0.72f, 0.16f, 0.16f, 1f)
                : new Color(0.10f, 0.45f, 0.20f, 1f);
            GUI.Label(new Rect(left, statusY, width, 22f), _status, _statusStyle);
        }
    }

    void DrawMessages(Rect area)
    {
        if (_history.Count == 0 && !_waiting)
        {
            _hintStyle.normal.textColor = new Color(0.42f, 0.45f, 0.52f, 1f);
            GUI.Label(new Rect(area.x + 4f, area.y + 2f, area.width - 8f, area.height),
                UILang.T("ai.hint"), _hintStyle);
            return;
        }

        float width = area.width - 22f;

        // 先量一遍每条的换行高度：内容矩形给不准，最后的回答就会滚不到底
        var heights = new float[_history.Count];
        float total = 0f;
        for (int i = 0; i < _history.Count; i++)
        {
            var style = StyleFor(_history[i].role);
            heights[i] = style.CalcHeight(new GUIContent(DisplayText(_history[i])), width) + 8f;
            total += heights[i];
        }

        float waitingHeight = 0f;
        if (_waiting)
        {
            waitingHeight = _bubbleAi.CalcHeight(new GUIContent(UILang.T("ai.thinking")), width) + 8f;
            total += waitingHeight;
        }

        if (_scrollToBottom)
        {
            _scroll.y = Mathf.Max(0f, total - area.height + 12f);
            _scrollToBottom = false;
        }

        var content = new Rect(0f, 0f, width + 6f, Mathf.Max(total + 6f, area.height));
        _scroll = GUI.BeginScrollView(area, _scroll, content);

        float y = 2f;
        for (int i = 0; i < _history.Count; i++)
        {
            var message = _history[i];
            var style = StyleFor(message.role);
            style.normal.textColor = message.role == "assistant"
                ? new Color(0.16f, 0.18f, 0.24f, 1f)
                : new Color(0.10f, 0.32f, 0.60f, 1f);
            GUI.Label(new Rect(4f, y, width, heights[i]), DisplayText(message), style);
            y += heights[i];
        }

        if (_waiting)
        {
            _bubbleAi.normal.textColor = new Color(0.45f, 0.48f, 0.55f, 1f);
            GUI.Label(new Rect(4f, y, width, waitingHeight), UILang.T("ai.thinking"), _bubbleAi);
        }

        GUI.EndScrollView();
    }

    void DrawKeySetup(Rect area)
    {
        _hintStyle.normal.textColor = new Color(0.35f, 0.38f, 0.45f, 1f);

        // 提示行数按语言算：中文两行、英文要三行，写死高度会把最后一行压到输入框底下
        string hintText = UILang.T("ai.noKey");
        float hintHeight = _hintStyle.CalcHeight(new GUIContent(hintText), area.width - 8f);
        GUI.Label(new Rect(area.x + 4f, area.y + 2f, area.width - 8f, hintHeight), hintText, _hintStyle);

        float rowY = area.y + 2f + hintHeight + 6f;
        _keyInput = GUI.PasswordField(new Rect(area.x + 4f, rowY, area.width - 158f, 32f), _keyInput, '*', 128, _fieldStyle);

        if (DrawButton(new Rect(area.x + area.width - 146f, rowY, 146f, 32f), UILang.T("ai.save"), true))
        {
            var key = (_keyInput ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                _status = UILang.T("ai.pasteFirst");
                _statusIsError = true;
            }
            else
            {
                _config = AIConfig.SaveToPlayerPrefs(key, null);
                _client = new DeepSeekClient(_config);
                _keyInput = string.Empty;
                _status = UILang.F("ai.saved", AIConfig.MaskKey(_config.api_key));
                _statusIsError = false;
            }
        }

        // 与输入框拉开一点：贴着输入框下沿时两个控件会被看成同一个
        rowY += 54f;
        if (DrawButton(new Rect(area.x + 4f, rowY, 130f, 28f), UILang.T("ai.reload"), false))
        {
            ReloadConfig();
            _status = _client != null && _client.Ready
                ? UILang.F("ai.gotKey", AIConfig.MaskKey(_config.api_key))
                : UILang.T("ai.noneFound");
            _statusIsError = _client == null || !_client.Ready;
        }

        if (_config != null && !string.IsNullOrEmpty(AIConfig.LastSource))
        {
            _statusStyle.normal.textColor = new Color(0.45f, 0.48f, 0.55f, 1f);
            GUI.Label(new Rect(area.x + 146f, rowY + 2f, area.width - 150f, 24f),
                UILang.F("ai.lastSource", AIConfig.LastSource), _statusStyle);
        }
    }

    void DrawPresets(Rect rect)
    {
        var questions = ExperimentPrompts.PresetQuestions;
        bool interactable = !_waiting && _client != null && _client.Ready;

        // 一行一个：挤成两列时中文被压得读不出，答辩现场点错按钮比多点两下行更糟
        for (int i = 0; i < questions.Length; i++)
        {
            bool previous = GUI.enabled;
            if (!interactable) GUI.enabled = false;
            if (DrawButton(new Rect(rect.x, rect.y + i * 34f, rect.width, 30f), questions[i], false)) Send(questions[i]);
            GUI.enabled = previous;
        }
    }

    // 按住标题栏拖动窗口：默认停在右下角会挡住右侧的小球，必须能挪开。
    void HandleDrag(Rect handle)
    {
        var evt = Event.current;
        if (evt.type == EventType.MouseDown && evt.button == 0 && handle.Contains(evt.mousePosition))
        {
            _dragging = true;
            _dragGrab = evt.mousePosition - _panelPos;
            evt.Use();
        }
        else if (_dragging && evt.type == EventType.MouseDrag && evt.button == 0)
        {
            _panelPos = evt.mousePosition - _dragGrab;
            evt.Use();
        }
        else if (_dragging && evt.type == EventType.MouseUp)
        {
            _dragging = false;
        }
    }

    // ---------------- 发送 ----------------

    void Send(string question)
    {
        if (_waiting) return;

        question = (question ?? string.Empty).Trim();
        if (question.Length == 0) return;

        if (_client == null || !_client.Ready)
        {
            _status = UILang.T("ai.needKey");
            _statusIsError = true;
            return;
        }

        _history.Add(new ChatMessage("user", question));
        TrimHistory();

        _pendingQuestion = question;
        _input = string.Empty;
        _waiting = true;
        _scrollToBottom = true;
        _status = UILang.F("ai.requesting", _client.Model);
        _statusIsError = false;

        StartCoroutine(RequestRoutine());
    }

    IEnumerator RequestRoutine()
    {
        string answer = null;
        string failure = null;

        yield return _client.Complete(BuildMessages(),
            text => answer = text,
            error => failure = error);

        _waiting = false;
        _scrollToBottom = true;

        if (failure != null)
        {
            // 失败不留半截对话：撤回问题，输入框还原，学生按一下就能重试
            RemoveLastUserMessage();
            _input = _pendingQuestion;
            _status = failure;
            _statusIsError = true;
            yield break;
        }

        if (!string.IsNullOrEmpty(answer))
        {
            _history.Add(new ChatMessage("assistant", answer));
            _status = UILang.F("ai.answered", answer.Length);
            _statusIsError = false;
        }
    }

    List<ChatMessage> BuildMessages()
    {
        var messages = new List<ChatMessage>();

        // 第一条：固定的助教角色与铁律
        messages.Add(new ChatMessage("system", ExperimentPrompts.SystemPrompt));

        // 第二条：这一次的真实实验数据（每问一次重算，学生改过参数也跟得上）
        var output = player != null ? player.Loader.Output : null;
        int frameIndex = player != null ? player.CurrentFrame : 0;
        messages.Add(new ChatMessage("system", ExperimentContext.Build(output, frameIndex, SourceName())));

        // 之后才是历史对话，只带最近几轮
        int start = Mathf.Max(0, _history.Count - Mathf.Max(1, maxRounds) * 2);
        for (int i = start; i < _history.Count; i++) messages.Add(_history[i]);

        return messages;
    }

    string SourceName()
    {
        if (binder == null || string.IsNullOrEmpty(binder.jsonFileName)) return UILang.T("ai.sourceRuntime");
        var name = binder.jsonFileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name.Substring(slash + 1) : name;
    }

    void TrimHistory()
    {
        int limit = Mathf.Max(1, maxRounds) * 2;
        while (_history.Count > limit) _history.RemoveAt(0);
    }

    void RemoveLastUserMessage()
    {
        if (_history.Count == 0) return;
        if (_history[_history.Count - 1].role == "user") _history.RemoveAt(_history.Count - 1);
    }

    // ---------------- 样式与小工具 ----------------

    GUIStyle StyleFor(string role)
    {
        return role == "assistant" ? _bubbleAi : _bubbleUser;
    }

    // AI 有时会回 markdown，普通 label 不解析，加个角色前缀至少让对话能分清谁说的
    static string DisplayText(ChatMessage message)
    {
        var prefix = UILang.T(message.role == "assistant" ? "ai.prefixTutor" : "ai.prefixMe");
        return prefix + (message.content ?? string.Empty).Trim();
    }

    void EnsureStyles()
    {
        if (_textStyle != null) return;

        _textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(fontSize),
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            richText = false,
        };
        _titleStyle = new GUIStyle(_textStyle) { fontStyle = FontStyle.Bold, wordWrap = false };
        _hintStyle = new GUIStyle(_textStyle) { fontSize = Mathf.RoundToInt(fontSize * 0.9f) };
        _statusStyle = new GUIStyle(_hintStyle) { wordWrap = false };
        _bubbleUser = new GUIStyle(_textStyle) { wordWrap = true };
        _bubbleAi = new GUIStyle(_textStyle) { wordWrap = true };
        _fieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = Mathf.RoundToInt(fontSize * 0.95f),
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(8, 8, 5, 5),
        };

        // 按钮自己画底、自己定字色：内置皮肤是深底浅字，压在浅色面板上像"禁用状态"，
        // 学生不敢点。label 派生的样式没有背景图，GUI.Button 仍能正常响应点击。
        _buttonStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(fontSize * 0.95f),
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(10, 10, 0, 0),
        };
        _buttonStyle.normal.textColor = new Color(0.12f, 0.16f, 0.24f, 1f);
        _buttonStyle.hover.textColor = new Color(0.05f, 0.09f, 0.16f, 1f);
        _buttonStyle.active.textColor = new Color(0.05f, 0.09f, 0.16f, 1f);

        _buttonLightStyle = new GUIStyle(_buttonStyle);
        _buttonLightStyle.normal.textColor = new Color(0.97f, 0.98f, 1f, 1f);
        _buttonLightStyle.hover.textColor = Color.white;
        _buttonLightStyle.active.textColor = Color.white;
    }

    static readonly Color ButtonFill = new Color(0.87f, 0.91f, 0.96f, 1f);
    static readonly Color ButtonFillHover = new Color(0.77f, 0.85f, 0.95f, 1f);
    // 与控制条 UGUI 按钮同色，让 AI 按钮看起来属于同一条工具带
    static readonly Color ButtonFillDark = UITheme.ButtonBg;
    static readonly Color ButtonFillDarkHover = UITheme.ButtonBgIdle;

    bool DrawButton(Rect rect, string text, bool dark)
    {
        // GUI.enabled=false 时 Unity 只淡化文字、不淡化我们自己画的底，所以要显式压暗
        bool active = GUI.enabled;
        bool hover = active && rect.Contains(Event.current.mousePosition);

        Color fill;
        if (!active) fill = dark ? new Color(0.55f, 0.60f, 0.68f, 1f) : new Color(0.90f, 0.91f, 0.93f, 1f);
        else if (dark) fill = hover ? ButtonFillDarkHover : ButtonFillDark;
        else fill = hover ? ButtonFillHover : ButtonFill;

        Fill(rect, fill);
        return GUI.Button(rect, text, dark ? _buttonLightStyle : _buttonStyle);
    }

    static void Fill(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
