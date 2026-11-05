using System;

[Serializable]
public class SimObject
{
    public string id;
    public float mass;
    public float radius;

    // 电荷量（C，本模拟用缩放单位让宏观粒子在屏幕上可见）。
    // 正负号决定转弯方向：q>0 顺时针、q<0 逆时针（Bz>0 即磁场垂直纸面向外时）。
    public float charge;

    // 以下两项是 t=0 的初始条件，由 Python 生成器带过来。
    // 用途：参数面板展示 / 标签显示"初始 v"，不参与回放计算。
    // restitution 是动量版遗留字段，磁场版不用（粒子间无碰撞），保留只为旧 JSON 不炸。
    public float[] position;
    public float[] velocity;
    public float restitution;
}

[Serializable]
public class SimBounds
{
    public float[] min;
    public float[] max;
}

[Serializable]
public class SimScenario
{
    public string mode;

    // 这两项必须是 double，不能用 float。
    // 0.01f 的真实值是 0.00999999977648…，累积上百步后位移会少 2.8e-8 量级；
    // 而 elastic 算例的碰撞时刻正好是第 125 步（距离恰好等于半径和），这点差就足以
    // 让"碰上"翻成"差一点没碰上"，整条轨迹晚一帧。Python 侧是 float64，这里必须对齐。
    public double time_step;
    public double duration;

    // 物理世界边界：画背景网格和刻度线时用它定范围，不要用可见帧的 min/max —
    // 可见范围会随物体运动变化，网格会跟着抖。
    public SimBounds bounds;

    // 边界碰撞恢复系数：1.0 = 边界完全弹性，0.0 = 吸附/吸收。（动量版遗留，磁场版粒子不该碰墙）
    public float boundary_restitution;

    // 匀强磁场，垂直 xy 平面（+z = 垂直纸面向外）。单位 T。
    // 旧 JSON 没有这个字段时 JsonUtility 补 0 —— q 不为 0 而 Bz=0 属于配置错误，
    // 求解器的 Validate 会拦住，不会静默算出直线。
    public double Bz;
}

[Serializable]
public class FrameBody
{
    public string id;
    public float[] position;
    public float[] velocity;
}

[Serializable]
public class SimFrame
{
    public int step;
    public float time;
    public FrameBody[] objects;
    public float[] total_momentum;
    public float kinetic_energy;
    public int collision_count;
    // 累计的边界接触步数。旧版 JSON 没有这个字段，JsonUtility 会补 0，
    // 正好等价于"没碰过墙"，向后兼容。
    public int boundary_contacts;
}

[Serializable]
public class SimAnalysis
{
    public float[] initial_momentum;
    public float[] final_momentum;
    public float max_momentum_drift;
    public float relative_momentum_error;
    public float initial_kinetic_energy;
    public float final_kinetic_energy;
    public int collision_count;
    public int boundary_contacts;
    // "pass" = 全程无边界接触，动量守恒成立；"inapplicable" = 中途撞过墙，
    // 墙是外力，动量不再是守恒量，只能说"不适用"。见 MomentumValidation 的说明。
    public string validation;
}

[Serializable]
public class SimulationOutput
{
    public string schema_version;
    public string input_hash;
    public SimScenario scenario;
    public SimObject[] objects;
    public SimFrame[] frames;
    public SimAnalysis analysis;
}
