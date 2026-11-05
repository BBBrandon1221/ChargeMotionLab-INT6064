using System;
using System.Globalization;
using System.Text;
using UnityEngine;

// 系统提示词与预设问题（磁场版：电荷在匀强磁场中的圆周运动）。
//
// 提示词的核心不是"让它懂物理"，而是"让它不许瞎编"：
// 数字全部由下一条 system 消息（实验上下文）给出，AI 只能引用、不能估算。
public static class ExperimentPrompts
{
    static readonly string SystemPromptZh =
        "你是\"磁场中的电荷运动\"实验软件的助教。学生正在操作一个带电粒子圆周运动回放程序，" +
        "你可以看到当前实验的真实参数（见下一条消息）。你的任务是引导他自己发现规律，" +
        "而不是替他得出结论。\n" +
        "\n" +
        "铁律：\n" +
        "1. 只能引用消息中给出的数字，禁止编造或估算任何未提供的值。\n" +
        "2. 不要直接说出最终答案。先问一句让他预测或观察的问题，再顺着他的回答推进。\n" +
        "3. 每次回答最多 3 个要点，总长度不超过 150 字。\n" +
        "4. 需要他动手时，明确指出改哪个参数（电荷量 q、质量 m、初速 v、磁场 B）、观察哪个量" +
        "（半径 r、转向、速率），并说明预期会看到什么。\n" +
        "5. 遇到他误解概念，用一次简短的反例或数量级估算纠正，不要长篇讲定义。" +
        "最常见的误解是\"转圈会消耗能量、越转越慢\"——磁场力不做功，速率不变。\n" +
        "6. 用中文回答，物理术语可保留英文（Lorentz force、cyclotron radius）。\n" +
        "7. 如果他直接问\"什么是洛伦兹力/半径公式\"，不要背定义，先反问他在画面上" +
        "观察到了什么，再引导他从自己看到的数据里归纳出 r = mv/(|q|B)。\n" +
        "8. 本模拟使用缩放单位（宏观电荷量），不要纠结数值是否真实，专注比例关系。\n" +
        "\n" +
        "回答结构建议：一句判断 → 一个引导问题 → 一个可验证的操作建议。";

    static readonly string SystemPromptEn =
        "You are the tutor inside a \"Charges in a Magnetic Field\" lab app. The student is running a " +
        "charged-particle circular-motion replay, and you can see the real parameters of the current " +
        "experiment (next message). Your job is to lead them to discover the rule themselves, " +
        "not to hand over the conclusion.\n" +
        "\n" +
        "Hard rules:\n" +
        "1. Cite only the numbers given in the messages; never invent or estimate a value that is not provided.\n" +
        "2. Do not state the final answer. Ask one question that makes them predict or observe, then follow their reply.\n" +
        "3. At most 3 points per reply, under 120 words in total.\n" +
        "4. When they need to act, name the parameter to change (charge q, mass m, speed v, field B) and the " +
        "quantity to watch (radius r, turning direction, speed), and say what they should expect to see.\n" +
        "5. If they misunderstand a concept, correct it with one short counter-example or order-of-magnitude check \u2014 no long definitions. " +
        "The most common misconception: \"circling consumes energy, it slows down\" \u2014 the magnetic force does no work, speed stays constant.\n" +
        "6. Answer in English; keep the physics terms (Lorentz force, cyclotron radius) as they are.\n" +
        "7. If they ask \"what is the Lorentz force / radius formula\", do not recite a definition. Ask what they " +
        "observe on screen first, then let them generalise r = mv/(|q|B) from their own data.\n" +
        "8. This simulation uses scaled units (macroscopic charges); focus on proportional relations, not realism.\n" +
        "\n" +
        "Suggested shape: one judgement \u2192 one guiding question \u2192 one concrete action they can verify.";

    // 语言随时可切，所以给的是属性：切到英文后新发的问题用英文提示词，
    // 已经在窗口里的历史对话保持原样，不会中英混在一段里。
    public static string SystemPrompt
    {
        get { return UILang.IsEnglish ? SystemPromptEn : SystemPromptZh; }
    }

    // 答辩现场手打中文风险高，这四条是一键发送的入口，也顺便演示了"AI 看着数据说话"。
    static readonly string[] PresetZh =
    {
        "为什么粒子转圈时速率不变？动能去哪了？",
        "把电荷量 q 变成 2 倍，圆的半径会怎么变？",
        "换成负电荷，轨迹有什么不一样？",
        "不看公式，你怎么从画面上量出半径 r？",
    };

    // 每条都控制在 62 字符内：预设按钮高 30px，英文超过一行会被按钮边框切掉
    static readonly string[] PresetEn =
    {
        "Why does the speed stay constant while circling?",
        "Double the charge q - what happens to the radius?",
        "Switch to a negative charge - how does the path change?",
        "Without the formula, how would you measure r on screen?",
    };

    public static string[] PresetQuestions
    {
        get { return UILang.IsEnglish ? PresetEn : PresetZh; }
    }
}

// 把当前 Output 压成一段固定格式的文字，作为第二条 system 消息发给 AI。
//
// 这段是"AI 辅助"和"随便接个聊天框"的分水岭：
// 有了它，AI 说的每个数字都能在画面上被学生验证；没有它，AI 只会讲教科书里的空话。
public static class ExperimentContext
{
    // 数据描述也要跟着界面语言走，否则英文模式下模型会看到一堆中文标签
    static string L(string zh, string en)
    {
        return UILang.IsEnglish ? en : zh;
    }

    public static string Build(SimulationOutput output, int frameIndex, string sourceName)
    {
        if (output == null || output.scenario == null || output.frames == null || output.frames.Length == 0)
            return L("【实验数据】当前还没有加载案例，学生尚未开始实验。",
                   "[DATA] No dataset is loaded yet; the student has not started.");

        var sb = new StringBuilder();
        var scenario = output.scenario;

        // ---- 当前案例 ----
        sb.Append(L("【当前案例】", "[DATASET] "))
          .Append(string.IsNullOrEmpty(sourceName) ? L("未命名案例", "unnamed") : sourceName);
        if (!string.IsNullOrEmpty(output.input_hash) && output.input_hash.StartsWith("runtime-", StringComparison.Ordinal))
            sb.Append(L("（学生改过参数后由程序重算，非预置案例）",
                        " (recomputed in-app after the student edited parameters; not a preset)"));
        sb.Append('\n');

        // ---- 实验设置 ----
        sb.Append(L("【实验设置】", "[SETUP] "))
          .Append("dt=").Append(F(scenario.time_step, 3)).Append("s")
          .Append("  duration=").Append(F(scenario.duration, 2)).Append("s")
          .Append("  B=").Append((scenario.Bz > 0 ? "+" : "") + F(scenario.Bz, 2)).Append("T")
          .Append(scenario.Bz > 0 ? L("（垂直纸面向外）", " (out of page)") : L("（垂直纸面向里）", " (into page)"));

        if (scenario.bounds != null && scenario.bounds.min != null && scenario.bounds.max != null &&
            scenario.bounds.min.Length >= 2 && scenario.bounds.max.Length >= 2)
        {
            sb.Append("  bounds x[").Append(F(scenario.bounds.min[0], 2))
              .Append(", ").Append(F(scenario.bounds.max[0], 2)).Append(']')
              .Append(" y[").Append(F(scenario.bounds.min[1], 2))
              .Append(", ").Append(F(scenario.bounds.max[1], 2)).Append(']');
        }
        sb.Append('\n');

        // ---- 电荷初值 + 理论圆周 ----
        if (output.objects != null)
        {
            for (int i = 0; i < output.objects.Length; i++)
            {
                var body = output.objects[i];
                if (body == null) continue;

                sb.Append(i == 0 ? L("【电荷】", "[CHARGES] ") : "         ");
                sb.Append(body.id)
                  .Append("  q=").Append((body.charge >= 0f ? "+" : "") + F(body.charge, 2)).Append("C")
                  .Append("  m=").Append(F(body.mass, 2)).Append("kg")
                  .Append("  x0=").Append(Vec0(body.position))
                  .Append("  v0=").Append(Vec0(body.velocity));

                double speed = 0.0;
                if (body.velocity != null && body.velocity.Length >= 2)
                    speed = Math.Sqrt(body.velocity[0] * (double)body.velocity[0] +
                                      body.velocity[1] * (double)body.velocity[1]);
                double qB = body.charge * scenario.Bz;
                if (Math.Abs(qB) > 1e-12 && speed > 1e-12)
                {
                    double r = body.mass * speed / Math.Abs(qB);
                    double period = 2.0 * Math.PI * body.mass / Math.Abs(qB);
                    sb.Append("  理论 r=").Append(F(r, 2)).Append("m")
                      .Append("  T=").Append(F(period, 2)).Append("s");
                }
                sb.Append('\n');
            }
        }

        // ---- 能量守恒汇总（磁场版的核心校验）----
        var analysis = output.analysis;
        var last = output.frames[output.frames.Length - 1];

        if (analysis != null)
        {
            sb.Append(L("【能量】初动能=", "[ENERGY] initial KE=")).Append(F(analysis.initial_kinetic_energy, 3)).Append(" J")
              .Append(L(" 末动能=", "  final KE=")).Append(F(analysis.final_kinetic_energy, 3)).Append(" J")
              .Append(L(" 最大相对漂移=", "  max relative drift=")).Append(F(analysis.max_momentum_drift, 12));
            if (!string.IsNullOrEmpty(analysis.validation))
            {
                sb.Append(L(" | 校验=", " | check=")).Append(analysis.validation);
                if (analysis.boundary_contacts > 0)
                    sb.Append(L("（有 ", " (")).Append(analysis.boundary_contacts)
                      .Append(L(" 帧碰到边界，墙是外力，此时不能下能量守恒的结论）",
                            " frames touched a wall; the wall does work, so the conservation claim does not hold)"));
            }
            sb.Append('\n');
        }

        // ---- 当前帧 ----
        int index = Mathf.Clamp(frameIndex, 0, output.frames.Length - 1);
        var current = output.frames[index];
        if (current != null)
            sb.Append(L("【当前帧】", "[CURRENT] ")).Append(FrameLine(current)).Append('\n');

        sb.Append(L("【备注】以上数值均由求解器算出；学生可能刚改过参数。回答中引用的数字必须与这里一致，",
                    "[NOTE] Every number above comes from the solver; the student may have just changed parameters. " +
                    "Any number you quote must match these. "));
        sb.Append(L("不确定的量就让学生自己去看画面。",
                    "If a quantity is missing, ask the student to read it off the screen."));

        return sb.ToString();
    }

    static string FrameLine(SimFrame frame)
    {
        var sb = new StringBuilder();
        sb.Append("t=").Append(F(frame.time, 2)).Append("s");

        if (frame.objects != null)
        {
            foreach (var body in frame.objects)
            {
                if (body == null) continue;
                sb.Append("  ").Append(body.id)
                  .Append("(pos=").Append(Vec0(body.position))
                  .Append(", v=").Append(Vec0(body.velocity)).Append(')');
            }
        }

        sb.Append("  KE=").Append(F(frame.kinetic_energy, 3)).Append(" J");
        return sb.ToString();
    }

    // 标量按需保留位数，避免 0.10000000000000001 这种噪声进了提示词。
    static string F(double value, int digits)
    {
        return value.ToString(digits <= 0 ? "0" : "0." + new string('0', digits), CultureInfo.InvariantCulture);
    }

    static string Vec0(float[] values)
    {
        if (values == null || values.Length < 2) return "?";
        return "(" + Signed(values[0]) + ", " + Signed(values[1]) + ")";
    }

    static string Signed(double value)
    {
        return (value >= 0.0 ? "+" : "") + value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
