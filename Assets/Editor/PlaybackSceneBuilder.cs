using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 一键生成回放场景：相机 + 三个球 + 回放控制条 + 实时数据卡片
// 注意：完全不使用 DefaultControls（2022.3 下它创建的 UI 不可靠），全部手工 AddComponent
//
// 布局基线 1920x1080（与 CanvasScaler referenceResolution 一致）：
//   左上 实时数据卡 372x268    右上 实验参数面板（OnGUI，见 ParameterPanel）
//   底部 控制条 900x76 居中     右下 AI 助教按钮（OnGUI，见 AIChatPanel）
public static class PlaybackSceneBuilder
{
    const string ScenePath = "Assets/Scenes/Main.unity";
    const string MaterialDir = "Assets/Materials";

    // ---- 布局常量：整体挪位置只改这里 ----
    // 英文标签（Momentum / Conserved）比四个汉字长，卡片和标签列都放宽了一点
    const float HudX = 24f, HudY = -24f, HudW = 404f, HudH = 268f;
    const float HudPadX = 16f;
    const float HudTitleH = 32f;
    const float HudRowH = 40f;
    const float HudRowTop = -56f;
    const float HudLabelW = 116f;
    const float HudValueX = 132f;
    const float HudValueW = 256f;

    const float BarW = 962f, BarH = 76f, BarBottom = 20f;
    const float BtnH = 52f, BtnY = 12f;      // 距条底 12，上下各留 12
    const float PlayW = 120f;
    const float SpeedW = 76f;
    const float PlayGap = 10f;
    const float SpeedGap = 8f;
    const float LangW = 66f;                 // 语言切换按钮
    const float BarInnerLeft = -470f;        // 940 宽内容在 962 条里居中

    // 球体高光强度：Standard shader 默认 0.5 太亮，投影时会糊成一片白
    const float BallSmoothness = 0.32f;

    static Font UIFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

    [MenuItem("Tools/01 Build Playback Scene")]
    public static void BuildScene()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 相机：正交、俯视 xy 平面
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 3f;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = UITheme.CanvasBg;

        // 光照：原来场景里一盏灯都没有，球只靠环境光照亮，看起来又暗又平。
        // 加一盏方向光让球有明暗面，再把环境光提亮，避免背面死黑。
        var lightGo = new GameObject("Directional Light");
        var dirLight = lightGo.AddComponent<Light>();
        dirLight.type = LightType.Directional;
        dirLight.intensity = 0.72f;
        dirLight.color = new Color(1f, 0.98f, 0.94f);
        lightGo.transform.rotation = Quaternion.Euler(52f, -34f, 0f);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.34f, 0.36f, 0.40f);

        // 三个球放在 Bodies 容器下：名字对应 multi2d 的 ball_1/2/3，
        // elastic / inelastic 的 cart_a/cart_b 会按顺序兜底绑定
        var bodies = new GameObject("Bodies").transform;
        bodies.position = Vector3.zero;
        CreateBall("ball_1", bodies, UITheme.Ball1, "Ball1.mat");
        CreateBall("ball_2", bodies, UITheme.Ball2, "Ball2.mat");
        CreateBall("ball_3", bodies, UITheme.Ball3, "Ball3.mat");

        // 回放组件
        var sim = new GameObject("Simulation");
        var playback = sim.AddComponent<PlaybackController>();
        var binder = sim.AddComponent<SceneBinder>();
        binder.player = playback;
        binder.bodiesRoot = bodies;
        binder.viewCamera = cam;
        binder.jsonFileName = "SimData/same_q.json";
        binder.autoPlay = true;

        // 轨迹线 + 理论参考圈：运行时按帧数据生成，换数据集自动重建
        var overlayGo = new GameObject("TrajectoryOverlay");
        var overlay = overlayGo.AddComponent<TrajectoryOverlay>();
        overlay.player = playback;

        // Canvas（AddComponent 才会自动带 RectTransform）
        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(UIScale.RefW, UIScale.RefH);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        if (Object.FindObjectOfType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        // ---- 左上：实时数据卡 ----
        var hud = canvasGo.AddComponent<HUDController>();
        hud.player = playback;

        var hudCard = CreateCard(canvasGo.transform, "HUDCard",
                                 new Vector2(0f, 1f), new Vector2(0f, 1f),
                                 new Vector2(HudX, HudY), new Vector2(HudW, HudH));

        CreateLabel(hudCard, "HUDTitle", "实时数据",
                    new Vector2(HudPadX, -12f),
                    new Vector2(HudW - HudPadX * 2f, HudTitleH),
                    22, UITheme.TextMuted, FontStyle.Bold, "hud.title");

        string[] rowLabels = { "时间", "速率 |v|", "动能", "磁场 B", "能量守恒" };
        string[] rowKeys = { "hud.time", "hud.speed", "hud.energy", "hud.field", "hud.conservation" };
        string[] rowDefaults = { "—", "—", "—", "—", "—" };
        var rowValues = new Text[rowLabels.Length];

        for (int i = 0; i < rowLabels.Length; i++)
        {
            float y = HudRowTop - i * HudRowH;

            CreateLabel(hudCard, "Row" + i + "Label", rowLabels[i],
                        new Vector2(HudPadX, y), new Vector2(HudLabelW, HudRowH - 4f),
                        22, UITheme.TextMuted, FontStyle.Normal, rowKeys[i], 19);

            rowValues[i] = CreateLabel(hudCard, ValueName(i), rowDefaults[i],
                        new Vector2(HudValueX, y), new Vector2(HudValueW, HudRowH - 4f),
                        26, UITheme.TextPrimary, FontStyle.Normal);
        }

        hud.timeText = rowValues[0];
        hud.momentumText = rowValues[1];
        hud.energyText = rowValues[2];
        hud.collisionText = rowValues[3];
        hud.statusText = rowValues[4];

        // ---- 底部：控制条（一条，居中）----
        var bar = CreateCard(canvasGo.transform, "ControlBar",
                             new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                             new Vector2(0f, BarBottom), new Vector2(BarW, BarH));

        // 播放组：第 i 个按钮中心 = 左起点 + 半个按钮 + i*(按钮+间距)
        float PlayCenter(int i) { return BarInnerLeft + PlayW * 0.5f + i * (PlayW + PlayGap); }

        var play = CreateButton(bar, "Play", "播放", PlayCenter(0), BtnY, new Vector2(PlayW, BtnH), playback.Play, "bar.play");
        var pause = CreateButton(bar, "Pause", "暂停", PlayCenter(1), BtnY, new Vector2(PlayW, BtnH), playback.Pause, "bar.pause");
        var step = CreateButton(bar, "Step", "单步", PlayCenter(2), BtnY, new Vector2(PlayW, BtnH), playback.StepForward, "bar.step");
        var reset = CreateButton(bar, "Reset", "重置", PlayCenter(3), BtnY, new Vector2(PlayW, BtnH), playback.ResetPlayback, "bar.reset");

        // 播放组与速度组之间的竖线
        float dividerX = BarInnerLeft + PlayW * 4f + PlayGap * 3f + PlayGap;
        CreateDivider(bar, "Divider", dividerX, 18f, new Vector2(2f, BarH - 36f));

        var speedCtrl = canvasGo.AddComponent<UISpeedControl>();
        speedCtrl.player = playback;

        float speedStart = dividerX + PlayGap + SpeedW * 0.5f;
        var s05 = CreateButton(bar, "0.5x", "0.5×", speedStart, BtnY, new Vector2(SpeedW, BtnH), speedCtrl.SetHalf);
        var s1 = CreateButton(bar, "1x", "1×", speedStart + (SpeedW + SpeedGap), BtnY, new Vector2(SpeedW, BtnH), speedCtrl.SetNormal);
        var s2 = CreateButton(bar, "2x", "2×", speedStart + (SpeedW + SpeedGap) * 2f, BtnY, new Vector2(SpeedW, BtnH), speedCtrl.SetFast);
        var s4 = CreateButton(bar, "4x", "4×", speedStart + (SpeedW + SpeedGap) * 3f, BtnY, new Vector2(SpeedW, BtnH), speedCtrl.SetVeryFast);

        // 语言切换：整条控制条的最右端。按钮上写的是"点了会变成什么"，
        // 中文界面显示 EN、英文界面显示"中文"，选择存在 PlayerPrefs 里，下次打开还记得。
        float lastSpeedRight = speedStart + (SpeedW + SpeedGap) * 3f + SpeedW * 0.5f;
        var langBtn = CreateButton(bar, "LangToggle", "EN", lastSpeedRight + 14f + LangW * 0.5f, BtnY,
                                   new Vector2(LangW, BtnH), null, "bar.lang");
        langBtn.GetComponent<Image>().color = UITheme.ButtonBgIdle;

        // 必须绑实例方法：静态方法没法持久化进场景，存盘后点击会变成空调用
        var langToggle = langBtn.gameObject.AddComponent<UILangButton>();
        UnityEventTools.AddPersistentListener(langBtn.onClick, langToggle.Toggle);

        // 速度档 / 播放状态高亮：否则四个速度按钮长得一样，看不出当前在哪一档
        var barState = bar.gameObject.AddComponent<ControlBarState>();
        barState.player = playback;
        barState.speedButtons = new[] { s05, s1, s2, s4 };
        barState.speeds = new[] { 0.5f, 1f, 2f, 4f };
        barState.playButton = play;
        barState.pauseButton = pause;
        barState.activeColor = UITheme.Accent;
        barState.idleColor = UITheme.ButtonBg;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        Selection.activeGameObject = canvasGo;
        Debug.Log("[PlaybackSceneBuilder] 场景已生成：" + ScenePath);
    }

    static string ValueName(int i)
    {
        switch (i)
        {
            case 0: return "TimeText";
            case 1: return "MomentumText";
            case 2: return "EnergyText";
            case 3: return "CollisionText";
            default: return "StatusText";
        }
    }

    // ---------- 基础物体 ----------

    static GameObject CreateBall(string name, Transform parent, Color color, string matFile)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localScale = Vector3.one * 0.5f;
        go.GetComponent<Renderer>().sharedMaterial = MakeMaterial(matFile, color);
        return go;
    }

    static Material MakeMaterial(string fileName, Color color)
    {
        if (!AssetDatabase.IsValidFolder(MaterialDir)) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = MaterialDir + "/" + fileName;
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            // 已经存在时也同步一次颜色/高光：否则改了 UITheme 里的球色，场景还是旧颜色，
            // 会让人以为改错了地方。
            existing.color = color;
            existing.SetFloat("_Glossiness", BallSmoothness);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        var mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.SetFloat("_Glossiness", BallSmoothness);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ---------- UI ----------

    // 卡片 = 底部淡阴影 + 近白底 Image。anchor/pivot 由调用方指定，子元素按左上角定位。
    static RectTransform CreateCard(Transform parent, string name, Vector2 anchor, Vector2 pivot,
                                    Vector2 pos, Vector2 size)
    {
        var shadow = new GameObject(name + "Shadow");
        shadow.transform.SetParent(parent, false);
        var shadowImg = shadow.AddComponent<Image>();
        shadowImg.color = UITheme.CardShadow;
        shadowImg.raycastTarget = false;
        var srt = shadow.GetComponent<RectTransform>();
        srt.anchorMin = anchor;
        srt.anchorMax = anchor;
        srt.pivot = pivot;
        srt.anchoredPosition = new Vector2(pos.x, pos.y - 3f);
        srt.sizeDelta = size;

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = UITheme.CardBg;
        img.raycastTarget = false;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    static Text CreateLabel(Transform parent, string name, string content, Vector2 pos, Vector2 size,
                            int fontSize, Color color, FontStyle style,
                            string locKey = null, int enFontSize = 0)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var text = go.AddComponent<Text>();
        text.font = UIFont;
        text.fontSize = fontSize;
        text.color = color;
        text.fontStyle = style;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = content;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        // 挂上 key 后，运行时切语言会自动换字（英文单词更长时用 enFontSize 缩一点）
        if (!string.IsNullOrEmpty(locKey))
        {
            var loc = go.AddComponent<LocalizedText>();
            loc.key = locKey;
            loc.enFontSize = enFontSize;
        }

        return text;
    }

    static void CreateDivider(Transform parent, string name, float centerX, float y, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = UITheme.Divider;
        img.raycastTarget = false;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(centerX, y);
        rt.sizeDelta = size;
    }

    static Button CreateButton(Transform parent, string name, string label, float centerX, float bottomY,
                               Vector2 size, UnityAction action, string locKey = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();    // 无 sprite 时 Image 会画纯色矩形
        img.color = UITheme.ButtonBg;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 0.93f, 0.82f);
        colors.pressedColor = new Color(0.85f, 0.78f, 0.68f);
        colors.selectedColor = Color.white;
        btn.colors = colors;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(centerX, bottomY);
        rt.sizeDelta = size;

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.AddComponent<Text>();
        text.font = UIFont;
        text.fontSize = 22;
        text.color = UITheme.TextOnDark;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.text = label;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        if (!string.IsNullOrEmpty(locKey))
        {
            var loc = textGo.AddComponent<LocalizedText>();
            loc.key = locKey;
        }

        if (action != null) UnityEventTools.AddPersistentListener(btn.onClick, action);
        return btn;
    }

    // —— 旧补丁：只给当前场景补一行 StatusText，不重建场景 ——
    [MenuItem("Tools/02 Add HUD Status Text")]
    public static void AddHudStatusText()
    {
        var hud = Object.FindObjectOfType<HUDController>();
        if (hud == null) { Debug.LogError("[Patch] 当前场景里没有 HUDController"); return; }
        if (hud.statusText != null) { Debug.Log("[Patch] StatusText 已存在，无需重复添加"); return; }

        var text = CreateLabel(hud.transform, "StatusText", "—",
                               new Vector2(HudValueX, HudRowTop - 4 * HudRowH),
                               new Vector2(HudValueW, HudRowH - 4f),
                               26, UITheme.TextPrimary, FontStyle.Normal);
        hud.statusText = text;

        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeObject = text.gameObject;
        Debug.Log("[Patch] 已在 HUD 卡片下添加 StatusText");
    }
}
