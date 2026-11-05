using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// 对账测试：同一组初值，C# 求解器 vs Python 生成的 JSON，逐帧比对。
// 目的：证明"改参数重算"和"预置算例"跑的是同一个物理，而不是两套。
// 菜单：Tools/03 Reconcile Solver vs Python JSON
public static class ReconcileTest
{
    const string DataFolder = "Assets/StreamingAssets/SimData";

    // 位置单位是米。1e-3 = 1 毫米，肉眼绝对看不出来，但足以抓住"碰撞处理写错"这类问题。
    const double PositionTolerance = 1e-3;
    const double VelocityTolerance = 1e-3;

    static readonly StringBuilder Report = new StringBuilder();

    [MenuItem("Tools/03 Reconcile Solver vs Python JSON")]
    public static void Run()
    {
        Report.Length = 0;
        Line("=== C# 求解器 vs Python JSON 对账 ===");
        Line($"运行时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("");

        if (!Directory.Exists(DataFolder))
        {
            Line($"[错误] 目录不存在：{DataFolder}");
            Flush(false);
            return;
        }

        var files = Directory.GetFiles(DataFolder, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Line("[错误] SimData 下没有 json 文件");
            Flush(false);
            return;
        }

        int passed = 0;
        foreach (var file in files)
        {
            if (Compare(file)) passed += 1;
        }

        Line("");
        Line($"结果：{passed}/{files.Length} 个算例通过（位置 ≤ {PositionTolerance:G} m，速度 ≤ {VelocityTolerance:G} m/s）");
        Flush(passed == files.Length);
    }

    static bool Compare(string path)
    {
        string name = Path.GetFileName(path);

        SimulationOutput reference;
        try
        {
            reference = JsonUtility.FromJson<SimulationOutput>(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            Line($"[{name}] X 读取失败：{error.Message}");
            return false;
        }

        if (reference?.frames == null || reference.frames.Length == 0)
        {
            Line($"[{name}] X JSON 里没有 frames");
            return false;
        }

        var setup = SimulationSolver.Capture(reference);
        if (setup == null)
        {
            Line($"[{name}] X 参数不完整（缺 position/velocity/bounds），无法重算");
            return false;
        }

        var result = SimulationSolver.Solve(setup);
        if (!result.Ok)
        {
            Line($"[{name}] X 求解失败：{result.Error}");
            return false;
        }

        var actual = result.Output;

        if (actual.frames.Length != reference.frames.Length)
        {
            Line($"[{name}] X 帧数不同：Python {reference.frames.Length} / C# {actual.frames.Length}" +
                 $"\n            duration={setup.duration} dt={setup.timeStep} → round({setup.duration}/{setup.timeStep})");
            return false;
        }

        double maxPosition = 0.0, maxVelocity = 0.0;
        int positionFrame = -1, velocityFrame = -1;

        for (int i = 0; i < reference.frames.Length; i++)
        {
            var expected = reference.frames[i];
            var got = actual.frames[i];
            if (expected?.objects == null || got?.objects == null) continue;

            if (expected.objects.Length != got.objects.Length)
            {
                Line($"[{name}] X 第 {i} 帧物体数不同：{expected.objects.Length} / {got.objects.Length}");
                return false;
            }

            for (int j = 0; j < expected.objects.Length; j++)
            {
                var a = expected.objects[j];
                var b = got.objects[j];
                if (a?.position == null || b?.position == null || a.id != b.id) continue;

                for (int axis = 0; axis < 2; axis++)
                {
                    double dp = Math.Abs(a.position[axis] - (double)b.position[axis]);
                    if (dp > maxPosition) { maxPosition = dp; positionFrame = i; }

                    double dv = Math.Abs(a.velocity[axis] - (double)b.velocity[axis]);
                    if (dv > maxVelocity) { maxVelocity = dv; velocityFrame = i; }
                }
            }
        }

        bool ok = maxPosition <= PositionTolerance && maxVelocity <= VelocityTolerance;
        string mark = ok ? "OK" : "X";

        Line($"[{name}] {mark}  帧数 {reference.frames.Length}" +
             $" | 位置最大误差 {Format(maxPosition)} (第 {positionFrame} 帧)" +
             $" | 速度最大误差 {Format(maxVelocity)} (第 {velocityFrame} 帧)");

        var lastExpected = reference.frames[reference.frames.Length - 1];
        var lastActual = actual.frames[actual.frames.Length - 1];

        // 末帧判空：数据异常时要报出来，不能在这里空引用崩掉、把前面的误差数据盖掉。
        if (lastExpected == null || lastActual == null)
        {
            Line("            末帧缺失，跳过碰撞次数与末速比对");
            Line("");
            return false;
        }

        bool collisionsMatch = lastExpected.collision_count == lastActual.collision_count;
        if (!collisionsMatch) ok = false;

        Line($"            碰撞次数 Python {lastExpected.collision_count} / C# {lastActual.collision_count}" +
             $"{(collisionsMatch ? "" : "  <== 不一致")}");

        // 末态速度是最能说明"物理是否一致"的量：学生看到的最终结果就是它。
        var speedText = new StringBuilder();
        if (lastExpected.objects != null && lastActual.objects != null)
        {
            int count = Math.Min(lastExpected.objects.Length, lastActual.objects.Length);
            for (int j = 0; j < count; j++)
            {
                var a = lastExpected.objects[j];
                var b = lastActual.objects[j];
                if (a?.velocity == null || b?.velocity == null) continue;
                if (speedText.Length > 0) speedText.Append("   ");
                speedText.Append(a.id).Append(' ')
                         .Append(Signed(a.velocity[0])).Append('/').Append(Signed(b.velocity[0]));
            }
        }
        Line($"            末速 Python/C#：{speedText}");

        if (lastExpected.total_momentum != null && lastActual.total_momentum != null)
        {
            Line($"            动量 {lastExpected.total_momentum[0]:0.000000} vs {lastActual.total_momentum[0]:0.000000}" +
                 $" | KE {lastExpected.kinetic_energy:0.000000} vs {lastActual.kinetic_energy:0.000000}");
        }
        Line("");

        return ok;
    }

    static string Format(double value) => value.ToString("0.0e+00");

    static string Signed(float value) => (value >= 0f ? "+" : "") + value.ToString("0.00");

    static void Line(string text) => Report.Append(text).Append('\n');

    // 同时写进 Console 和 txt：txt 方便把结果直接贴回来核对，不用截图。
    static void Flush(bool success)
    {
        string text = Report.ToString();
        if (success) Debug.Log(text);
        else Debug.LogError(text);

        try
        {
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "reconcile_report.txt"));
            File.WriteAllText(output, text, new UTF8Encoding(false));
            Debug.Log($"[对账] 报告已写入 {output}");
        }
        catch (Exception error)
        {
            Debug.LogWarning($"[对账] 报告写入失败：{error.Message}");
        }
    }
}
