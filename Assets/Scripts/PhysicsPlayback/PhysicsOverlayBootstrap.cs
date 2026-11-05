using UnityEngine;

// 自动装配：进入 Play 时，如果场景里有回放组件但还没挂这些显示层，
// 就在这里补一个 PhysicsOverlay 物体把网格 / 速度标签 / 参数面板 / AI 助教挂上去。
//
// 为什么用代码装配而不是写进 Main.unity：
//   1. 场景文件按 GUID 引用脚本，手工改容易把 m_Script 弄丢；
//   2. 多人同时改同一个 .unity 必然冲突，而这几块又不需要任何 Inspector 设置。
// 如果你想在 Inspector 里调颜色 / 字号，自己把这些组件挂到场景物体上即可，
// 这里逐个检查、已存在就不重复创建，手动配置优先。
public static class PhysicsOverlayBootstrap
{
    public const string OverlayName = "PhysicsOverlay";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureOverlay()
    {
        // 只在有回放场景里生效，别的场景（比如以后加的标题页）不会凭空多出网格
        var binder = Object.FindObjectOfType<SceneBinder>();
        if (binder == null) return;

        var overlay = GameObject.Find(OverlayName);
        if (overlay == null) overlay = new GameObject(OverlayName);

        // 逐个补挂：以前只要 PhysicsGrid 存在就整体跳过，导致手动挂过网格的机器上，
        // 后加的 AI 面板永远不会被创建。这里改成按组件判断。
        bool created = false;
        if (Object.FindObjectOfType<PhysicsGrid>() == null) { overlay.AddComponent<PhysicsGrid>(); created = true; }
        if (Object.FindObjectOfType<SpeedLabels>() == null) { overlay.AddComponent<SpeedLabels>(); created = true; }
        if (Object.FindObjectOfType<ParameterPanel>() == null) { overlay.AddComponent<ParameterPanel>(); created = true; }
        if (Object.FindObjectOfType<AIChatPanel>() == null) { overlay.AddComponent<AIChatPanel>(); created = true; }

        if (created) Debug.Log("[PhysicsOverlay] 已自动创建：网格 / 速度标签 / 参数面板 / AI 助教（按 C 打开）");
    }
}
