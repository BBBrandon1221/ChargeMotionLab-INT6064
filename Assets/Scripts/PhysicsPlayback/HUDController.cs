using UnityEngine;
using UnityEngine.UI;

// 左上「实时数据」卡片里 5 个数值格子的写入者。
//
// 磁场版五格：时间 / 速率|v| / 动能 / 磁场B / 校验。
// 「速率」是这一版的灵魂格子：洛伦兹力永远垂直于速度、不做功，所以速率和动能全程不变 ——
// 学生第一反应往往是"转圈应该会减速"，这一格就是用来当场戳破这个误解的。
public sealed class HUDController : MonoBehaviour
{
    public PlaybackController player;
    public Text timeText;
    public Text momentumText;    // 语义改为「速率 |v|」（格子标签由场景生成器负责）
    public Text energyText;
    public Text collisionText;   // 语义改为「磁场 B」（静态值，随数据集变化）

    [Tooltip("可选：守恒检验结论。旧场景没有这个 Text 时会自动跳过")]
    public Text statusText;

    void Update()
    {
        if (player == null) return;

        // 加载失败时优先把原因显示出来，比留一堆 0 有用。
        if (!string.IsNullOrEmpty(player.LastError))
        {
            if (timeText) timeText.text = UILang.T("hud.loadFailed");
            if (statusText)
            {
                statusText.text = UILang.T("hud.seeConsole");
                statusText.color = UITheme.Bad;
            }
            return;
        }

        if (player.Loader.Output == null) return;
        var f = player.Loader.Output.frames[player.CurrentFrame];

        if (timeText) timeText.text = $"{f.time:F2} s";

        // 速率 |v|：每个电荷一个值，磁场版算例固定 2 个粒子，"2.0 / 2.0 m/s" 放得下。
        if (momentumText) momentumText.text = Speeds(f);

        // 动能 = ½mv²。未知粒子站里 v 是给的，动能一露，m 反手就能除出来 —— 整个挑战就废了。
        if (energyText) energyText.text = ParameterPanel.MaskSolution ? "—" : $"{f.kinetic_energy:F2} J";

        var scenario = player.Loader.Output.scenario;
        if (collisionText && scenario != null)
            collisionText.text = $"{scenario.Bz:F1} T";

        if (statusText)
            ApplyValidation(statusText, player.Loader.Output.analysis);
    }

    static string Speeds(SimFrame frame)
    {
        if (frame?.objects == null) return "—";

        var sb = new System.Text.StringBuilder();
        foreach (var body in frame.objects)
        {
            if (body?.velocity == null || body.velocity.Length < 2) continue;
            double speed = System.Math.Sqrt(body.velocity[0] * (double)body.velocity[0] +
                                            body.velocity[1] * (double)body.velocity[1]);
            if (sb.Length > 0) sb.Append(" / ");
            sb.Append(speed.ToString("0.##"));
        }
        return sb.Length == 0 ? "—" : sb.ToString() + " m/s";
    }

    // 把机读的 validation 枚举翻成人话，并给出颜色。
    // 磁场版语义：pass = 全程没碰墙且动能守恒成立；inapplicable = 撞过墙（外力做功）。
    static void ApplyValidation(Text target, SimAnalysis analysis)
    {
        if (analysis == null)
        {
            target.text = "—";
            target.color = UITheme.TextMuted;
            return;
        }

        switch (analysis.validation)
        {
            case "pass":
                target.text = UILang.T("hud.pass");
                target.color = UITheme.Good;
                break;

            case "inapplicable":
                target.text = UILang.T("hud.na");
                target.color = UITheme.Warn;
                break;

            default:
                target.text = analysis.validation;
                target.color = UITheme.Bad;
                break;
        }
    }
}
