using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public enum UILangId { Zh = 0, En = 1 }

// 界面语言（中文 / English）。
//
// 为什么做成静态类：这个项目有两条完全独立的绘制路径 ——
//   · UGUI（Canvas 里烘好的卡片和按钮，OnGUI 管不到）
//   · OnGUI（参数面板、AI 面板每帧现画）
// 静态类两边都能直接读，不用互相拿引用。切换时广播 Changed，
// 挂着的 LocalizedText 收到后把 UGUI 的文字重刷一遍。
//
// 词条按「key → { 中文, English }」成对存放。加新文案时两边都写，
// 缺一边会在 T() 里回落到 key 本身，肉眼一看就知道漏了。
public static class UILang
{
    const string PrefKey = "chargelab.lang";

    static bool _loaded;
    static UILangId _current = UILangId.Zh;

    public static event Action Changed;

    static readonly Dictionary<string, string[]> Table =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // ---------- 左上实时数据卡 ----------
        { "hud.title",       new[] { "实时数据",   "Live data" } },
        { "hud.time",        new[] { "时间",       "Time" } },
        { "hud.speed",       new[] { "速率 |v|",   "Speed |v|" } },
        { "hud.energy",      new[] { "动能",       "Kinetic E" } },
        { "hud.field",       new[] { "磁场 B",     "Field B" } },
        { "hud.conservation",new[] { "能量守恒",   "E conserved" } },
        { "hud.pass",        new[] { "✓ 通过",     "✓ Pass" } },
        { "hud.na",          new[] { "不适用",     "n/a" } },
        { "hud.loadFailed",  new[] { "加载失败",   "Load failed" } },
        { "hud.seeConsole",  new[] { "看控制台",   "See console" } },

        // ---------- 磁场图例（左下角） ----------
        { "field.legend.out", new[] { "B = {0} T　⊙ 磁场垂直纸面向外", "B = {0} T  ⊙ field out of page" } },
        { "field.legend.in",  new[] { "B = {0} T　⊗ 磁场垂直纸面向里", "B = {0} T  ⊗ field into page" } },

        // ---------- 底部控制条 ----------
        { "bar.play",        new[] { "播放",       "Play" } },
        { "bar.pause",       new[] { "暂停",       "Pause" } },
        { "bar.step",        new[] { "单步",       "Step" } },
        { "bar.reset",       new[] { "重置",       "Reset" } },
        // 按钮上写的是「点了会变成什么」，所以中文模式下显示 EN
        { "bar.lang",        new[] { "EN",         "中文" } },

        // ---------- 右上实验参数面板 ----------
        { "panel.title",     new[] { "实验参数",   "Experiment setup" } },
        { "panel.hintEdit",  new[] { "[P] 改参数", "[P] edit" } },
        { "panel.hintClose", new[] { "[P] 收起",   "[P] close" } },
        { "panel.tipEdit",   new[] { "改完点「重算并播放」",
                                     "Edit, then click Recompute" } },
        { "panel.noSetup",   new[] { "这份数据缺少初始参数，无法编辑",
                                     "This dataset has no initial setup to edit" } },
        { "panel.recompute", new[] { "重算并播放",  "Recompute" } },
        { "panel.restore",   new[] { "恢复原案例",  "Restore original" } },

        { "panel.err.charge",new[] { "{0} 的电荷量不是有效数字",
                                     "{0}: charge is not a valid number" } },
        { "panel.err.mass",  new[] { "{0} 的质量不是有效数字",
                                     "{0}: mass is not a valid number" } },
        { "panel.err.vx",    new[] { "{0} 的 vx 不是有效数字",
                                     "{0}: vx is not a valid number" } },
        { "panel.err.vy",    new[] { "{0} 的 vy 不是有效数字",
                                     "{0}: vy is not a valid number" } },
        { "panel.err.x0",    new[] { "{0} 的 x0 不是有效数字",
                                     "{0}: x0 is not a valid number" } },
        { "panel.err.y0",    new[] { "{0} 的 y0 不是有效数字",
                                     "{0}: y0 is not a valid number" } },
        { "panel.err.bz",    new[] { "磁场强度不是有效数字",
                                     "Field strength is not a valid number" } },
        { "panel.err.dur",   new[] { "实验时长不是有效数字",
                                     "Duration is not a valid number" } },
        { "panel.err.run",   new[] { "重算失败，详情见 Console",
                                     "Recompute failed — see Console" } },
        { "panel.ok.run",    new[] { "已重算：{0} 个电荷，{1} s",
                                     "Recomputed: {0} charges, {1} s" } },
        { "panel.ok.restore",new[] { "已恢复原始案例",
                                     "Restored the original dataset" } },

        { "panel.dt",        new[] { "步长",       "dt" } },
        { "panel.duration",  new[] { "时长",       "duration" } },
        { "panel.bounds",    new[] { "区域",       "bounds" } },
        { "panel.fieldB",    new[] { "磁场 B",     "Field B" } },
        { "panel.fieldOut",  new[] { "（垂直纸面向外 ⊙）", "(out of page ⊙)" } },
        { "panel.fieldIn",   new[] { "（垂直纸面向里 ⊗）", "(into page ⊗)" } },
        { "panel.fieldNone", new[] { "（无磁场：电荷走直线）", "(no field: straight line)" } },
        { "panel.radius",    new[] { "半径",       "radius" } },
        { "panel.period",    new[] { "周期",       "period" } },

        // ---------- AI 助教 ----------
        { "ai.button",       new[] { "AI 助教 [{0}]", "AI Tutor [{0}]" } },
        { "ai.title",        new[] { "AI 实验助教　拖动标题可移开",
                                     "AI Lab Tutor  ·  drag to move" } },
        { "ai.clear",        new[] { "清空对话",   "Clear" } },
        { "ai.close",        new[] { "收起",       "Close" } },
        { "ai.send",         new[] { "发送",       "Send" } },
        { "ai.waiting",      new[] { "等待…",      "Wait…" } },
        { "ai.thinking",     new[] { "助教｜思考中…", "Tutor | thinking…" } },
        { "ai.prefixTutor",  new[] { "助教｜",     "Tutor | " } },
        { "ai.prefixMe",     new[] { "我｜",       "Me | " } },
        { "ai.hint",         new[] { "点下面的问题开始，也可以直接打字。\n我只看着你当前的实验数据说话，不会凭空编数字。",
                                     "Pick a question below, or just type.\nI only speak from your current experiment data — no invented numbers." } },
        { "ai.noKey",        new[] { "还没有可用的 API key。\n粘贴一个 sk- 开头的 key 只存在本机；也可以把 ai_config.json 放到 exe 同目录。",
                                     "No API key yet.\nA pasted sk- key is stored on this machine only; you can also drop ai_config.json next to the exe." } },
        { "ai.save",         new[] { "保存并启用", "Save & enable" } },
        { "ai.pasteFirst",   new[] { "先粘贴 key 再点保存",
                                     "Paste a key before saving" } },
        { "ai.saved",        new[] { "已保存到本机：{0}", "Saved locally: {0}" } },
        { "ai.reload",       new[] { "重读配置文件", "Reload config" } },
        { "ai.gotKey",       new[] { "已读到 key：{0}", "Loaded key: {0}" } },
        { "ai.noneFound",    new[] { "仍然没读到可用的 key",
                                     "Still no usable key" } },
        { "ai.lastSource",   new[] { "上次来源：{0}", "Last source: {0}" } },
        { "ai.needKey",      new[] { "还没配置 API key，先填一个再发",
                                     "Configure an API key first" } },
        { "ai.requesting",   new[] { "正在请求 {0} …", "Requesting {0} …" } },
        { "ai.answered",     new[] { "回答已收到（{0} 字）",
                                     "Answer received ({0} chars)" } },
        { "ai.sourceRuntime",new[] { "程序内重算的参数",
                                     "parameters recomputed in app" } },

        // ---------- 实验室大厅（ChargeLabHub） ----------
        { "hub.hint",        new[] { "WASD 走动 · 右键拖动环视 · 左键点实验舱进入实验",
                                     "WASD move  ·  RMB drag to look  ·  LMB click a chamber to enter" } },
        { "hub.back",        new[] { "← 返回实验室", "← Back to lab" } },

        // 三台舱 = 三个课时。标题写实验做什么，副标题写这是第几课时、
        // 学生在这一站要产出的东西（归纳 / 找规律 / 判断）。
        { "hub.st1.title",   new[] { "① 转向与半径",   "① Direction & radius" } },
        { "hub.st1.sub",     new[] { "课时 1 · 找出转向和半径的规律",
                                     "Lesson 1 · find the rules" } },
        { "hub.st2.title",   new[] { "② 控制变量",     "② One variable at a time" } },
        { "hub.st2.sub",     new[] { "课时 2 · 一次只改一个量",
                                     "Lesson 2 · change one thing" } },
        { "hub.st3.title",   new[] { "③ 未知粒子",     "③ Unknown particle" } },
        { "hub.st3.sub",     new[] { "课时 3 · 测出它的荷质比",
                                     "Lesson 3 · find its q/m" } },
        { "hub.st4.title",   new[] { "④ 电场偏转靶场", "④ Deflection range" } },
        { "hub.st4.sub",     new[] { "课时 4 · 调参数把球打进靶心",
                                     "Lesson 4 · tune it onto the target" } },
        { "hub.lesson",      new[] { "课时 {0}", "Lesson {0}" } },

        // ---------- 第四模块：平行板电场偏转靶场（PlateFieldModule）----------
        { "plate.title",     new[] { "平行板电场 · 偏转靶场",
                                     "Parallel plates · deflection range" } },
        { "plate.subtitle",  new[] { "上板 + · 下板 − · 电子流被电场顶成由下往上",
                                     "Top + · bottom − · electrons pushed upward by E" } },
        { "plate.u",         new[] { "电压 U",         "Voltage U" } },
        { "plate.q",         new[] { "电荷量 q",       "Charge q" } },
        { "plate.m",         new[] { "质量 m",         "Mass m" } },
        { "plate.v0",        new[] { "初速度 v₀",      "Launch speed v₀" } },
        { "plate.formula",   new[] { "E = U/d = {0}　　a = qE/m = {1}",
                                     "E = U/d = {0}    a = qE/m = {1}" } },
        { "plate.predicted", new[] { "预测落点 x = {0}", "Predicted impact  x = {0}" } },
        { "plate.escapedShort", new[] { "飞出画面（没打到板）", "flies past — no impact" } },
        { "plate.score",     new[] { "命中 {0} / 试射 {1}", "hits {0} / tries {1}" } },
        { "plate.launch",    new[] { "发射 [空格]",    "Launch [Space]" } },
        { "plate.reset",     new[] { "复位 [R]",       "Reset [R]" } },
        { "plate.newTarget", new[] { "换靶 [N]",       "New target [N]" } },
        { "plate.hint",      new[] { "改任一参数，灰色预测线立刻跟着走；绿段是靶心，琥珀点是别的位置。",
                                     "Change any slider and the grey prediction line follows at once. Green is the target; amber are decoys." } },
        { "plate.legend.title", new[] { "图例", "Legend" } },
        { "plate.legend.top",    new[] { "上极板：正极",       "Top plate: positive" } },
        { "plate.legend.bottom", new[] { "下极板：负极",       "Bottom plate: negative" } },
        { "plate.legend.flow",   new[] { "电子流（与电场反向）", "Electron flow (against E)" } },
        { "plate.legend.target", new[] { "靶心",               "Target" } },
        { "plate.judge.hit",     new[] { "命中靶心",           "On target" } },
        { "plate.judge.hit.sub", new[] { "命中 {0} 次 / 试射 {1} 次 · 按 N 换一个新靶继续",
                                         "{0} hits in {1} tries · press N for a new target" } },
        { "plate.judge.near",    new[] { "擦边：离靶心 {0} m",  "Close: {0} m off centre" } },
        { "plate.judge.near.sub", new[] { "就差一点，把 v₀ 或 U 再挪一格",
                                          "So close — nudge v₀ or U by one step" } },
        { "plate.judge.miss",    new[] { "偏{0} {1} m，没进绿区",
                                         "{1} m {0} of centre — off target" } },
        { "plate.judge.miss.subR", new[] { "偏右 = 球太快，还没落下就飞过去了。把 v₀ 调小，或把 U / q 调大",
                                           "Too fast — it crossed before falling far enough. Lower v₀, or raise U or q" } },
        { "plate.judge.miss.subL", new[] { "偏左 = 偏转过头了。把 v₀ 调大，或把 U / q 调小",
                                           "Over-deflected. Raise v₀, or lower U or q" } },
        { "plate.judge.top",     new[] { "撞到上极板了",       "Hit the top plate" } },
        { "plate.judge.top.sub", new[] { "偏转太强：把 U 或 q 调小，或把 v₀ 调大，再试一次",
                                         "Too much deflection: lower U or q, or raise v₀, and retry" } },
        { "plate.judge.escape",  new[] { "从右侧飞出去了，没打到板",
                                         "Flew out to the right — no impact" } },
        { "plate.judge.escape.sub", new[] { "偏转太弱：把 U 或 q 调大，或把 v₀ 调小，再试一次",
                                            "Too little deflection: raise U or q, or lower v₀, and retry" } },
        { "plate.left",          new[] { "左", "left" } },
        { "plate.right",         new[] { "右", "right" } },

        // ---------- 未知粒子挑战卡 ----------
        { "hub.mys.title",   new[] { "这颗粒子是什么？", "What is this particle?" } },
        { "hub.mys.tip",     new[] { "从网格量出半径，用 r = mv/(qB) 逐个试算。",
                                     "Measure r on the grid, then test each with r = mv/(qB)." } },
        { "hub.mys.a",       new[] { "q=+1  m=1", "q=+1  m=1" } },
        { "hub.mys.b",       new[] { "q=+2  m=1", "q=+2  m=1" } },
        { "hub.mys.c",       new[] { "q=+1  m=2", "q=+1  m=2" } },
        { "hub.mys.d",       new[] { "q=−1  m=1", "q=−1  m=1" } },
        { "hub.mys.right",   new[] { "对了。r = 4.0 m，v = 2 m/s，B = 1 T → q/m = 0.5。",
                                     "Correct. r = 4.0 m, v = 2 m/s, B = 1 T → q/m = 0.5." } },
        { "hub.mys.wrong",   new[] { "再量一次：半径是 4 米，转向呢？",
                                     "Measure again: r is 4 m — and which way does it turn?" } },
        { "hub.mys.note",    new[] { "注意：轨迹只能定出 q/m 的比值，分不出 q 和 m 各是多少。",
                                     "Note: the path only fixes q/m — it cannot separate q from m." } },
        { "hub.mys.retry",   new[] { "再量一次", "Measure again" } },
    };

    public static UILangId Current
    {
        get { Load(); return _current; }
    }

    public static bool IsEnglish
    {
        get { return Current == UILangId.En; }
    }

    // persist=false 用于自动截图这类"只为出图"的场合：切过去但不记住，
    // 免得一次截图把用户下次打开时的默认语言改掉。
    public static void Set(UILangId id, bool persist = true)
    {
        Load();
        if (_current == id) return;

        _current = id;
        if (persist)
        {
            PlayerPrefs.SetInt(PrefKey, (int)id);
            PlayerPrefs.Save();
        }
        if (Changed != null) Changed();
    }

    public static void Toggle()
    {
        Set(IsEnglish ? UILangId.Zh : UILangId.En);
    }

    public static string T(string key)
    {
        string[] pair;
        if (!string.IsNullOrEmpty(key) && Table.TryGetValue(key, out pair))
            return IsEnglish ? pair[1] : pair[0];

        // 找不到就原样返回，免得界面上出现空白让人以为控件坏了
        return key;
    }

    public static string F(string key, params object[] args)
    {
        return string.Format(CultureInfo.InvariantCulture, T(key), args);
    }

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        int saved = PlayerPrefs.GetInt(PrefKey, (int)UILangId.Zh);
        _current = saved == (int)UILangId.En ? UILangId.En : UILangId.Zh;
    }
}
