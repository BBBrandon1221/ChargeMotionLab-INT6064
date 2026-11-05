using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

// 自动点一遍控制条按钮，把状态变化写进文本文件——用来确认 UGUI 的 onClick 绑定真的接上了，
// 而不是"看起来有按钮、点了没反应"。
//
//   MomentumLab.exe -autotest "D:/autotest.txt"
//
// 只在带参时启用，正常手动玩不受影响。
public sealed class AutoTest : MonoBehaviour
{
    static string _path;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-autotest") _path = args[i + 1];

        if (string.IsNullOrEmpty(_path)) return;

        var go = new GameObject("AutoTest");
        DontDestroyOnLoad(go);
        go.AddComponent<AutoTest>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSeconds(2.5f);

        var sb = new StringBuilder();
        var canvas = Object.FindObjectOfType<Canvas>();
        var player = Object.FindObjectOfType<PlaybackController>();
        var ai = Object.FindObjectOfType<AIChatPanel>();
        var param = Object.FindObjectOfType<ParameterPanel>();

        sb.AppendLine("=== 运行环境 ===");
        sb.AppendLine($"screen = {Screen.width} x {Screen.height}");
        sb.AppendLine($"canvas = {(canvas != null)}  player = {(player != null)}  " +
                      $"AI = {(ai != null)}  param = {(param != null)}");

        if (canvas == null || player == null)
        {
            Write(sb, "找不到 Canvas 或 PlaybackController");
            yield break;
        }

        var bar = canvas.transform.Find("ControlBar");
        sb.AppendLine($"ControlBar = {(bar != null)}");
        sb.AppendLine();

        sb.AppendLine("=== 按钮 onClick 绑定 ===");
        foreach (var name in new[] { "Play", "Pause", "Step", "Reset", "0.5x", "1x", "2x", "4x", "LangToggle" })
        {
            var btn = bar != null ? bar.Find(name) : null;
            var component = btn != null ? btn.GetComponent<Button>() : null;
            int persistent = component != null ? component.onClick.GetPersistentEventCount() : -1;
            sb.AppendLine($"{name,-6} 存在={(btn != null)}  Button={(component != null)}  " +
                          $"持久监听={persistent}  {(persistent > 0 ? "OK" : "!! 未绑定")}");
        }
        sb.AppendLine();

        sb.AppendLine("=== 逐个点一遍，看状态是否真的变 ===");
        // 进场时自动播放可能已经跑到末帧，此时单步无处可进，会误报"没动"。先归零再测。
        Invoke(bar, "Reset");
        yield return new WaitForSeconds(0.25f);
        int frameBefore = player.CurrentFrame;

        Invoke(bar, "Step");
        yield return new WaitForSeconds(0.15f);
        sb.AppendLine($"单步: 帧 {frameBefore} -> {player.CurrentFrame}  {(player.CurrentFrame > frameBefore ? "OK" : "!! 没动")}");

        Invoke(bar, "Play");
        yield return new WaitForSeconds(0.4f);
        sb.AppendLine($"播放: IsPlaying={player.IsPlaying}  {(player.IsPlaying ? "OK" : "!! 没开")}");

        Invoke(bar, "Pause");
        yield return new WaitForSeconds(0.15f);
        sb.AppendLine($"暂停: IsPlaying={player.IsPlaying}  {(!player.IsPlaying ? "OK" : "!! 没停")}");

        foreach (var pair in new[] { ("0.5x", 0.5f), ("2x", 2f), ("4x", 4f), ("1x", 1f) })
        {
            Invoke(bar, pair.Item1);
            yield return new WaitForSeconds(0.1f);
            bool ok = Mathf.Abs(player.PlaybackSpeed - pair.Item2) < 0.001f;
            sb.AppendLine($"速度 {pair.Item1}: PlaybackSpeed={player.PlaybackSpeed:0.##}  {(ok ? "OK" : "!! 不对")}");
        }

        Invoke(bar, "Reset");
        yield return new WaitForSeconds(0.2f);
        sb.AppendLine($"重置: 帧={player.CurrentFrame} IsPlaying={player.IsPlaying}  " +
                      $"{(player.CurrentFrame == 0 && !player.IsPlaying ? "OK" : "!! 没复位")}");

        // 语言按钮：点一下切英文、再点一下切回中文。
        // 顺手把 PlayerPrefs 恢复原状，免得测试把默认语言改掉。
        sb.AppendLine();
        sb.AppendLine("=== 语言切换（点两次，切过去再切回来）===");
        var langLabel = FindLabel(bar, "LangToggle");
        var playLabel = FindLabel(bar, "Play");
        var hudTitle = FindLabel(canvas.transform, "HUDCard/HUDTitle");

        string lang0 = langLabel != null ? langLabel.text : "(找不到)";
        string play0 = playLabel != null ? playLabel.text : "(找不到)";
        string hud0 = hudTitle != null ? hudTitle.text : "(找不到)";

        Invoke(bar, "LangToggle");
        yield return new WaitForSeconds(0.25f);
        string lang1 = langLabel != null ? langLabel.text : "(找不到)";
        string play1 = playLabel != null ? playLabel.text : "(找不到)";
        string hud1 = hudTitle != null ? hudTitle.text : "(找不到)";

        Invoke(bar, "LangToggle");
        yield return new WaitForSeconds(0.25f);
        string lang2 = langLabel != null ? langLabel.text : "(找不到)";

        sb.AppendLine($"切换按钮：\"{lang0}\" -> \"{lang1}\" -> \"{lang2}\"  " +
                      $"{(lang0 != lang1 && lang0 == lang2 ? "OK" : "!! 不对")}");
        sb.AppendLine($"控制条 Play：\"{play0}\" -> \"{play1}\"  {(play0 != play1 ? "OK" : "!! 文字没换")}");
        sb.AppendLine($"数据卡标题：\"{hud0}\" -> \"{hud1}\"  {(hud0 != hud1 ? "OK" : "!! 文字没换")}");

        sb.AppendLine();
        sb.AppendLine("=== 圆周半径是否跟着 r=mv/(|q|B) 走（参数面板「重算」走的就是这条路径）===");
        var binder = Object.FindObjectOfType<SceneBinder>();
        if (binder == null)
        {
            sb.AppendLine("!! 场景里没有 SceneBinder");
        }
        else
        {
            var setup = binder.CaptureSetup();
            if (setup == null || setup.bodies.Count < 1)
            {
                sb.AppendLine("!! 参数快照取不到");
            }
            else
            {
                // --- 用例 1：电荷量翻倍 → 半径减半 ---
                var probe = setup.bodies[0];
                string probeId = probe.id;
                double r1 = TheoreticalRadius(probe, setup.Bz);
                probe.charge *= 2.0;   // 等价于在面板里把这一行的 q 改成两倍再点重算
                bool applied1 = binder.ApplySetup(setup);
                yield return null;
                double r2 = TheoreticalRadius(probe, setup.Bz);
                double measured1 = MeasuredRadius(player, probeId);
                sb.AppendLine($"用例1 重算 = {applied1}");
                sb.AppendLine($"{probeId} q={probe.charge:0.00}C  理论半径 {r1:0.00} -> {r2:0.00} m（期望减半）");
                sb.AppendLine($"  实测轨迹半径（帧数据到圆心最大距离）= {measured1:0.000} m  " +
                              $"{(System.Math.Abs(measured1 - r2) < 0.05 ? "OK 吻合" : "!! 偏差 >5cm")}");
                sb.AppendLine($"  轨迹始终可见（末帧在边界内）= {InsideBounds(player, probeId, setup)}");

                // --- 用例 2：电荷反号 → 转弯方向相反 ---
                // 判据：绕圆心的角动量 z 分量 sign((pos-圆心)×v)。顺时针 <0，逆时针 >0，
                // 比"取 1/4 周期看 y 偏移"稳——后者在 T/2 的整数倍附近会撞上 y≈0。
                var setup2 = binder.CaptureSetup();
                if (setup2 != null && setup2.bodies.Count >= 1)
                {
                    var p2 = setup2.bodies[0];
                    double signPos = TurnSign(player, p2.id);
                    p2.charge = -p2.charge;
                    bool applied2 = binder.ApplySetup(setup2);
                    yield return null;
                    double signNeg = TurnSign(player, p2.id);
                    sb.AppendLine($"用例2 重算 = {applied2}");
                    sb.AppendLine($"{p2.id} 转向符号 q>0: {signPos:+0;-0;0}  q<0: {signNeg:+0;-0;0}  " +
                                  $"{(signPos * signNeg < 0 ? "OK 方向相反" : "!! 方向没反")}");

                    // --- 用例 3：磁场力不做功 → 末速率 = 初速率 ---
                    double speed0 = SpeedOf(player, p2.id, 0);
                    int lastFrame = player.Loader.Output.frames.Length - 1;
                    double speed1 = SpeedOf(player, p2.id, lastFrame);
                    sb.AppendLine($"用例3 末速率 = 初速率：{speed0:0.0000} vs {speed1:0.0000}  " +
                                  $"{(System.Math.Abs(speed1 - speed0) < 1e-3 ? "OK 磁场力不做功" : "!! 速率变了")}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== 其它面板 ===");
        sb.AppendLine($"参数编辑（按 P）: 反射读取不到私有字段，靠手动验");
        sb.AppendLine($"AI 面板可按 C 唤出: {(ai != null ? "已挂载 OK" : "!! 未挂载")}");

        Write(sb, null);
    }

    static Text FindLabel(Transform root, string path)
    {
        if (root == null) return null;
        var t = root.Find(path + "/Label");
        if (t == null) t = root.Find(path);
        return t != null ? t.GetComponent<Text>() : null;
    }

    // ---------- 磁场版用例的辅助量 ----------

    // 理论圆周半径 r = m|v|/(|q|B)
    static double TheoreticalRadius(SimulationSolver.BodySetup body, double Bz)
    {
        double speed = System.Math.Sqrt(body.vx * body.vx + body.vy * body.vy);
        return body.mass * speed / System.Math.Abs(body.charge * Bz);
    }

    // 实测轨迹半径：圆心 = 起点 + (m/(qB))·(vy, -vx)，扫全部帧取离圆心最大距离
    static double MeasuredRadius(PlaybackController player, string id)
    {
        var output = player.Loader.Output;
        if (output == null || output.objects == null || output.frames == null) return -1.0;

        foreach (var info in output.objects)
        {
            if (info == null || info.id != id) continue;
            if (info.velocity == null || info.velocity.Length < 2) return -1.0;
            double Bz = output.scenario != null ? output.scenario.Bz : 0.0;
            double qB = info.charge * Bz;
            if (System.Math.Abs(qB) < 1e-12) return -1.0;

            double cx = info.position[0] + info.mass / qB * info.velocity[1];
            double cy = info.position[1] - info.mass / qB * info.velocity[0];

            double maxDistance = 0.0;
            foreach (var frame in output.frames)
            {
                var body = frame?.objects;
                if (body == null) continue;
                foreach (var item in body)
                {
                    if (item == null || item.id != id || item.position == null || item.position.Length < 2) continue;
                    double d = System.Math.Sqrt((item.position[0] - cx) * (item.position[0] - cx) +
                                                (item.position[1] - cy) * (item.position[1] - cy));
                    if (d > maxDistance) maxDistance = d;
                }
            }
            return maxDistance;
        }
        return -1.0;
    }

    // 轨迹是否全程在边界内（重算后参考圈飞出视野时给出提示）
    static bool InsideBounds(PlaybackController player, string id, SimulationSolver.Setup setup)
    {
        var output = player.Loader.Output;
        if (output?.frames == null || output.frames.Length == 0) return false;
        foreach (var frame in output.frames)
        {
            if (frame?.objects == null) continue;
            foreach (var item in frame.objects)
            {
                if (item == null || item.id != id || item.position == null || item.position.Length < 2) continue;
                if (item.position[0] < (float)setup.boundsMinX || item.position[0] > (float)setup.boundsMaxX ||
                    item.position[1] < (float)setup.boundsMinY || item.position[1] > (float)setup.boundsMaxY)
                    return false;
            }
        }
        return true;
    }

    // 转向判据：绕圆心的角动量 z 分量 sign((pos-圆心)×v)。
    // q>0 且 B>0（磁场向外）时顺时针，cross_z 恒负；q<0 反号恒正。取中段一帧即可。
    static double TurnSign(PlaybackController player, string id)
    {
        var output = player.Loader.Output;
        if (output?.objects == null || output.frames == null || output.frames.Length < 2) return 0.0;

        foreach (var info in output.objects)
        {
            if (info == null || info.id != id) continue;
            double Bz = output.scenario != null ? output.scenario.Bz : 0.0;
            double qB = info.charge * Bz;
            if (System.Math.Abs(qB) < 1e-12) return 0.0;

            double cx = info.position[0] + info.mass / qB * info.velocity[1];
            double cy = info.position[1] - info.mass / qB * info.velocity[0];

            var frame = output.frames[output.frames.Length / 2];
            if (frame?.objects == null) return 0.0;
            foreach (var item in frame.objects)
            {
                if (item == null || item.id != id ||
                    item.position == null || item.position.Length < 2 ||
                    item.velocity == null || item.velocity.Length < 2) continue;
                double cross = (item.position[0] - cx) * item.velocity[1] -
                               (item.position[1] - cy) * item.velocity[0];
                return cross;
            }
        }
        return 0.0;
    }

    // 指定帧的速率 |v|
    static double SpeedOf(PlaybackController player, string id, int frameIndex)
    {
        var output = player.Loader.Output;
        if (output?.frames == null || frameIndex < 0 || frameIndex >= output.frames.Length) return -1.0;
        var frame = output.frames[frameIndex];
        if (frame?.objects == null) return -1.0;
        foreach (var item in frame.objects)
        {
            if (item == null || item.id != id || item.velocity == null || item.velocity.Length < 2) continue;
            return System.Math.Sqrt(item.velocity[0] * (double)item.velocity[0] +
                                    item.velocity[1] * (double)item.velocity[1]);
        }
        return -1.0;
    }

    static void Invoke(Transform bar, string name)
    {
        if (bar == null) return;
        var t = bar.Find(name);
        if (t == null) return;

        var btn = t.GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.Invoke();
            return;
        }

        // 速度按钮可能挂的是 UISpeedControl 上的方法，退一步直接找按钮组件
        var anyButton = t.GetComponentInChildren<Button>();
        if (anyButton != null) anyButton.onClick.Invoke();
    }

    static void Write(StringBuilder sb, string fatal)
    {
        try
        {
            if (fatal != null) sb.AppendLine("FATAL: " + fatal);
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(true));
            Debug.Log("[AutoTest] 已写入 " + _path);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[AutoTest] 写文件失败: " + e.Message);
        }

        Application.Quit();
    }
}
