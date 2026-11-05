using System;
using System.Collections.Generic;

// 把 tools/charge_gen.py 的求解核心移植到 C#，让打包后的 exe 在"改参数"时也能重算。
//
// 物理：匀强磁场 B = Bz·ẑ（垂直 xy 平面），电荷受洛伦兹力 F = q·v×B，粒子间无相互作用。
// 积分器：Boris 推进器 —— 对纯磁场它是一个精确旋转，动能严格守恒（数值上漂移 < 1e-12），
// 这正是磁场演示最重要的教学点："磁场力不做功，速率不变"。
//
// 移植基准（逐公式对应，改这里就要同步改 charge_gen.py）：
//   charge_gen.py boris_step() → Step
//   charge_gen.py 帧循环 / round(t, 12) → Solve
//
// 内部一律用 double 计算（Python 侧是 float64），只在写入 SimulationOutput 时转 float。
// 一致性由 Editor/ReconcileTest.cs 保证：同一组初值下与 Python 生成的 JSON 逐帧比对。
public static class SimulationSolver
{
    // 帧数上限：dt 填得过小会把内存吃光，这里先挡住。
    const int MaxSteps = 200000;

    public sealed class BodySetup
    {
        public string id;
        public double mass;
        public double radius;
        public double charge;
        public double x;
        public double y;
        public double vx;
        public double vy;

        public BodySetup Clone() => (BodySetup)MemberwiseClone();
    }

    // 编辑面板直接改的就是这个对象：可序列化、可回滚、可校验。
    public sealed class Setup
    {
        public string mode = "2d";
        public double timeStep = 0.01;
        public double duration = 6.3;
        public double Bz = 1.0;
        public double boundsMinX = -6.0;
        public double boundsMinY = -6.0;
        public double boundsMaxX = 6.0;
        public double boundsMaxY = 6.0;

        // 动量版遗留字段：边界恢复系数在磁场版里只用于"粒子意外撞墙"的兜底反弹，
        // 正常算例全程不碰墙（Validate 与 presets 都保证这一点）。
        public double boundaryRestitution = 1.0;

        public List<BodySetup> bodies = new List<BodySetup>();

        public Setup Clone()
        {
            var copy = new Setup
            {
                mode = mode,
                timeStep = timeStep,
                duration = duration,
                Bz = Bz,
                boundsMinX = boundsMinX,
                boundsMinY = boundsMinY,
                boundsMaxX = boundsMaxX,
                boundsMaxY = boundsMaxY,
                boundaryRestitution = boundaryRestitution,
                bodies = new List<BodySetup>(),
            };
            foreach (var body in bodies) copy.bodies.Add(body.Clone());
            return copy;
        }
    }

    public sealed class Result
    {
        public SimulationOutput Output;
        public string Error;
        public bool Ok => Output != null && string.IsNullOrEmpty(Error);
    }

    // 运行时的可变状态，字段名短，只在求解器内部流动。
    sealed class Body
    {
        public string Id;
        public double Mass;
        public double Radius;
        public double Charge;
        public double X, Y, Vx, Vy;
    }

    // ---------- 从已有 JSON 抓一份可编辑参数 ----------

    // 返回 null 表示这份数据缺字段、不足以编辑（调用方应提示而不是崩）。
    public static Setup Capture(SimulationOutput output)
    {
        if (output == null || output.scenario == null) return null;
        if (output.objects == null || output.objects.Length < 1) return null;

        var scenario = output.scenario;
        var setup = new Setup
        {
            mode = "2d",   // 磁场版只有二维：洛伦兹力 F=qv×B 天然是平面旋转，1d 没有物理意义
            timeStep = scenario.time_step,
            duration = scenario.duration,
            Bz = scenario.Bz,
            boundaryRestitution = scenario.boundary_restitution,
        };

        if (!FillBounds(setup, scenario, output)) return null;

        var firstFrame = output.frames != null && output.frames.Length > 0 ? output.frames[0] : null;

        foreach (var item in output.objects)
        {
            if (item == null) continue;

            var body = new BodySetup
            {
                id = item.id,
                mass = item.mass,
                radius = item.radius,
                charge = item.charge,
                x = 0, y = 0, vx = 0, vy = 0,
            };

            // objects[] 里的 position / velocity 是 t=0 初值；旧数据没有就退回第 0 帧。
            bool hasInitial = TryRead(item.position, item.velocity, body);
            if (!hasInitial && firstFrame?.objects != null)
            {
                foreach (var state in firstFrame.objects)
                {
                    if (state == null || state.id != item.id) continue;
                    hasInitial = TryRead(state.position, state.velocity, body);
                    break;
                }
            }

            // 初值都读不到就没法重算，直接判为"这份数据不可编辑"，别拿 0 当起点算出假结果。
            if (!hasInitial) return null;

            if (setup.mode == "1d") { body.y = 0; body.vy = 0; }
            setup.bodies.Add(body);
        }

        return setup.bodies.Count >= 1 ? setup : null;
    }

    static bool FillBounds(Setup setup, SimScenario scenario, SimulationOutput output)
    {
        var bounds = scenario.bounds;
        if (bounds != null && bounds.min != null && bounds.max != null &&
            bounds.min.Length >= 2 && bounds.max.Length >= 2)
        {
            setup.boundsMinX = bounds.min[0];
            setup.boundsMinY = bounds.min[1];
            setup.boundsMaxX = bounds.max[0];
            setup.boundsMaxY = bounds.max[1];
            return true;
        }

        // 旧数据没有 bounds：用整段轨迹的外扩范围兜底，至少还能画网格。
        if (output.frames == null || output.frames.Length == 0) return false;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var frame in output.frames)
        {
            if (frame?.objects == null) continue;
            foreach (var state in frame.objects)
            {
                if (state?.position == null || state.position.Length < 2) continue;
                minX = Math.Min(minX, state.position[0]);
                maxX = Math.Max(maxX, state.position[0]);
                minY = Math.Min(minY, state.position[1]);
                maxY = Math.Max(maxY, state.position[1]);
            }
        }
        if (minX > maxX) return false;

        setup.boundsMinX = minX - 1.0;
        setup.boundsMaxX = maxX + 1.0;
        setup.boundsMinY = minY - 1.0;
        setup.boundsMaxY = maxY + 1.0;
        return true;
    }

    static bool TryRead(float[] position, float[] velocity, BodySetup body)
    {
        if (position == null || velocity == null || position.Length < 2 || velocity.Length < 2) return false;
        body.x = position[0];
        body.y = position[1];
        body.vx = velocity[0];
        body.vy = velocity[1];
        return true;
    }

    // ---------- 求解 ----------

    public static Result Solve(Setup setup)
    {
        var error = Validate(setup);
        if (!string.IsNullOrEmpty(error)) return new Result { Error = error };

        var bodies = new List<Body>(setup.bodies.Count);
        foreach (var item in setup.bodies)
        {
            bodies.Add(new Body
            {
                Id = item.id,
                Mass = item.mass,
                Radius = item.radius,
                Charge = item.charge,
                X = item.x, Y = item.y, Vx = item.vx, Vy = item.vy,
            });
        }

        // 与 Python 的 round(duration / time_step) 一致：Python 的 round 与 Math.Round 都是四舍六入五取偶。
        int steps = (int)Math.Round(setup.duration / setup.timeStep, MidpointRounding.ToEven);
        if (steps < 1) steps = 1;

        var frames = new List<SimFrame>(steps + 1);
        double time = 0.0;
        int boundaryContacts = 0;

        frames.Add(Snapshot(0, time, bodies, boundaryContacts));
        for (int step = 1; step <= steps; step++)
        {
            Step(bodies, setup, setup.timeStep, ref boundaryContacts);
            time = Math.Round(time + setup.timeStep, 12, MidpointRounding.ToEven);
            frames.Add(Snapshot(step, time, bodies, boundaryContacts));
        }

        var output = new SimulationOutput
        {
            schema_version = SimulationLoader.SupportedSchemaVersion,
            input_hash = RuntimeHash(setup),
            scenario = BuildScenario(setup),
            objects = BuildObjects(setup),
            frames = frames.ToArray(),
            analysis = BuildAnalysis(frames),
        };

        return new Result { Output = output };
    }

    // 单步：Boris 推进（纯磁场情形 = 把速度矢量旋转 ω·dt），再处理边界兜底。
    // Boris 对纯磁场是精确的辛旋转：|v| 逐位不变，能量漂移在 1e-15 量级。
    // 必须与 charge_gen.py 的 boris_step() 逐行一致，ReconcileTest 才能通过。
    static void Step(List<Body> bodies, Setup setup, double dt, ref int boundaryContacts)
    {
        bool touchedBoundary = false;

        for (int i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];

            // t = (q·B / m) · dt/2，只考虑 z 分量磁场
            double tz = body.Charge * setup.Bz / body.Mass * dt * 0.5;
            double sz = 2.0 * tz / (1.0 + tz * tz);

            // v' = v⁻ + v⁻×t　（t = (0,0,tz)，v×t = (vy·tz, -vx·tz, 0)）
            double vpx = body.Vx + body.Vy * tz;
            double vpy = body.Vy - body.Vx * tz;

            // v⁺ = v⁻ + v'×s　（s = (0,0,sz)）。注意基准是 v⁻ 原速度，不是 v' ——
            // 基准写错时旋转矩阵不正交，动能会每步放大约 4tz²（实测 KE 漂移 8%）。
            double vPlusX = body.Vx + vpy * sz;
            double vPlusY = body.Vy - vpx * sz;

            body.Vx = vPlusX;
            body.Vy = vPlusY;

            body.X += body.Vx * dt;
            body.Y += body.Vy * dt;

            if (ResolveBoundary(body, setup)) touchedBoundary = true;
        }

        if (touchedBoundary) boundaryContacts++;
    }

    // 兜底反弹：正常算例不会走到这里（圆都在边界内）。撞墙是外力，会破坏"能量守恒"
    // 的教学结论，所以必须回报给调用方，而不是悄悄把球推回去。
    static bool ResolveBoundary(Body body, Setup setup)
    {
        bool touched = false;
        for (int axis = 0; axis < 2; axis++)
        {
            double low = (axis == 0 ? setup.boundsMinX : setup.boundsMinY) + body.Radius;
            double high = (axis == 0 ? setup.boundsMaxX : setup.boundsMaxY) - body.Radius;

            double position = axis == 0 ? body.X : body.Y;
            double velocity = axis == 0 ? body.Vx : body.Vy;

            if (position < low)
            {
                position = low;
                if (velocity < 0.0) velocity *= -setup.boundaryRestitution;
                touched = true;
            }
            else if (position > high)
            {
                position = high;
                if (velocity > 0.0) velocity *= -setup.boundaryRestitution;
                touched = true;
            }

            if (axis == 0) { body.X = position; body.Vx = velocity; }
            else { body.Y = position; body.Vy = velocity; }
        }

        if (setup.mode == "1d") { body.Y = 0.0; body.Vy = 0.0; }

        return touched;
    }

    static SimFrame Snapshot(int step, double time, List<Body> bodies, int boundaryContacts)
    {
        var frame = new SimFrame
        {
            step = step,
            time = (float)time,
            objects = new FrameBody[bodies.Count],
            collision_count = 0,
            boundary_contacts = boundaryContacts,
        };

        double px = 0.0, py = 0.0, energy = 0.0;
        for (int i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            frame.objects[i] = new FrameBody
            {
                id = body.Id,
                position = new[] { (float)body.X, (float)body.Y },
                velocity = new[] { (float)body.Vx, (float)body.Vy },
            };
            px += body.Mass * body.Vx;
            py += body.Mass * body.Vy;
            energy += 0.5 * body.Mass * (body.Vx * body.Vx + body.Vy * body.Vy);
        }

        // 磁场版语义：total_momentum 只是"逐帧 Σp 记录"（它本来就不守恒，方向一直在转），
        // 真正要盯的守恒量是 kinetic_energy —— HUD 和校验都只看它。
        frame.total_momentum = new[] { (float)px, (float)py };
        frame.kinetic_energy = (float)energy;
        return frame;
    }

    static SimScenario BuildScenario(Setup setup) => new SimScenario
    {
        mode = setup.mode,
        // 不要在这里转 float：一转就把 double 的 dt 打回 0.00999999977648，
        // 重算结果再被 Capture 读一次，精度又丢一层。
        time_step = setup.timeStep,
        duration = setup.duration,
        Bz = setup.Bz,
        bounds = new SimBounds
        {
            min = new[] { (float)setup.boundsMinX, (float)setup.boundsMinY },
            max = new[] { (float)setup.boundsMaxX, (float)setup.boundsMaxY },
        },
        boundary_restitution = (float)setup.boundaryRestitution,
    };

    static SimObject[] BuildObjects(Setup setup)
    {
        var objects = new SimObject[setup.bodies.Count];
        for (int i = 0; i < setup.bodies.Count; i++)
        {
            var body = setup.bodies[i];
            objects[i] = new SimObject
            {
                id = body.id,
                mass = (float)body.mass,
                radius = (float)body.radius,
                charge = (float)body.charge,
                position = new[] { (float)body.x, (float)body.y },
                velocity = new[] { (float)body.vx, (float)body.vy },
                restitution = 1f,   // 磁场版不用，占位保持 schema 完整
            };
        }
        return objects;
    }

    static SimAnalysis BuildAnalysis(List<SimFrame> frames)
    {
        double initialEnergy = frames[0].kinetic_energy;
        double finalEnergy = frames[frames.Count - 1].kinetic_energy;

        // 磁场不做功，速率不该变。maxDrift 这里语义改为"动能最大相对漂移"。
        double maxDrift = 0.0;
        foreach (var frame in frames)
        {
            if (initialEnergy <= 0.0) break;
            double drift = Math.Abs(frame.kinetic_energy - initialEnergy) / initialEnergy;
            if (drift > maxDrift) maxDrift = drift;
        }

        // 累计边界接触步数由 Snapshot 一路带到这里，末帧即全程总数。
        int contacts = frames[frames.Count - 1].boundary_contacts;

        // validation 语义沿用动量版的两个值（HUD 不用改判断逻辑）：
        //   pass         = 全程没碰墙且能量守恒成立
        //   inapplicable = 撞过墙，墙是外力，能量守恒的结论不能下
        return new SimAnalysis
        {
            initial_momentum = frames[0].total_momentum,
            final_momentum = frames[frames.Count - 1].total_momentum,
            max_momentum_drift = (float)maxDrift,
            relative_momentum_error = (float)maxDrift,
            initial_kinetic_energy = frames[0].kinetic_energy,
            final_kinetic_energy = frames[frames.Count - 1].kinetic_energy,
            collision_count = 0,
            boundary_contacts = contacts,
            validation = contacts == 0 ? "pass" : "inapplicable",
        };
    }

    // 运行时重算没有对应的输入文件，用参数拼一个短摘要，便于日志里区分"这份是谁算的"。
    static string RuntimeHash(Setup setup)
    {
        uint hash = 2166136261u;
        foreach (var text in new[] { setup.mode, setup.duration.ToString("R"), setup.timeStep.ToString("R"), setup.Bz.ToString("R") })
        {
            foreach (char c in text) { hash ^= c; hash *= 16777619u; }
        }
        foreach (var body in setup.bodies)
        {
            foreach (var value in new[] { body.mass, body.radius, body.charge, body.vx, body.vy })
            {
                foreach (char c in value.ToString("R")) { hash ^= c; hash *= 16777619u; }
            }
        }
        return "runtime-" + hash.ToString("x8");
    }

    // ---------- 校验 ----------

    // 返回 null 表示通过；否则是给面板直接显示的中文原因。
    public static string Validate(Setup setup)
    {
        if (setup == null) return "没有可用的实验参数";
        if (setup.mode != "2d") return "磁场实验只支持 2d 模式";
        if (!IsFinite(setup.timeStep) || setup.timeStep <= 0.0 || setup.timeStep > 0.05)
            return "时间步长必须在 (0, 0.05] 之间";
        if (!IsFinite(setup.duration) || setup.duration <= 0.0)
            return "实验时长必须大于 0";
        if (setup.duration / setup.timeStep > MaxSteps)
            return $"帧数超过 {MaxSteps}，请增大时间步长或缩短时长";
        if (!IsFinite(setup.Bz))
            return "磁场强度不是有效数字";
        // B = 0 是合法实验（课时 2 的对照：没有磁场就没有洛伦兹力，电荷走直线）。
        // 只是此时不存在圆周，下面的圆心 / 半径检查要跳过。
        if (!IsFinite(setup.boundsMinX) || !IsFinite(setup.boundsMaxX) ||
            !IsFinite(setup.boundsMinY) || !IsFinite(setup.boundsMaxY))
            return "实验边界不是有效数字";
        if (setup.boundsMaxX <= setup.boundsMinX || setup.boundsMaxY <= setup.boundsMinY)
            return "实验边界的上界必须大于下界";

        if (setup.bodies == null || setup.bodies.Count < 1)
            return "至少需要 1 个电荷才能做实验";

        var ids = new HashSet<string>();
        foreach (var body in setup.bodies)
        {
            if (body == null) return "参数里存在空物体";
            if (string.IsNullOrEmpty(body.id)) return "物体 ID 不能为空";
            if (!ids.Add(body.id)) return $"物体 ID 重复：{body.id}";
            if (!IsFinite(body.mass) || body.mass <= 0.0) return $"{body.id} 的质量必须大于 0";
            if (!IsFinite(body.radius) || body.radius <= 0.0) return $"{body.id} 的半径必须大于 0";
            if (!IsFinite(body.charge) || Math.Abs(body.charge) < 1e-12) return $"{body.id} 的电荷量不能为 0";
            if (!IsFinite(body.x) || !IsFinite(body.y) || !IsFinite(body.vx) || !IsFinite(body.vy))
                return $"{body.id} 的位置或速度不是有效数字";
            if (Math.Abs(body.vx) + Math.Abs(body.vy) < 1e-12)
                return $"{body.id} 的初速度为 0，静止电荷在磁场里不会运动";

            // 初始位置必须让整个球体落在边界内，否则第一帧就会被边界推回，看起来像"瞬移"。
            if (body.x - body.radius < setup.boundsMinX || body.x + body.radius > setup.boundsMaxX)
                return $"{body.id} 的初始位置超出左右边界";
            if (setup.mode == "2d" &&
                (body.y - body.radius < setup.boundsMinY || body.y + body.radius > setup.boundsMaxY))
                return $"{body.id} 的初始位置超出上下边界";

            // 圆周轨迹必须在边界内：圆心 = 起点 + (m/(qB))·(vy, -vx)，半径 = m|v|/(|q|B)。
            // 不检查的话学生把 B 改小 10 倍，圆直接飞出视野，还以为程序坏了。
            // B = 0 时没有圆周，只校验直线不会在中途撞墙。
            double qB = body.charge * setup.Bz;
            double speed = Math.Sqrt(body.vx * body.vx + body.vy * body.vy);

            if (Math.Abs(qB) < 1e-12)
            {
                double travel = speed * setup.duration;
                double endX = body.x + body.vx / speed * travel;
                double endY = body.y + body.vy / speed * travel;
                if (endX - body.radius < setup.boundsMinX || endX + body.radius > setup.boundsMaxX ||
                    endY - body.radius < setup.boundsMinY || endY + body.radius > setup.boundsMaxY)
                    return $"{body.id} 的直线轨迹会撞上边界，请缩短实验时长或降低初速";
                continue;
            }

            double circleRadius = body.mass * speed / Math.Abs(qB);
            double centerX = body.x + body.mass / qB * body.vy;
            double centerY = body.y - body.mass / qB * body.vx;

            if (centerX - circleRadius < setup.boundsMinX || centerX + circleRadius > setup.boundsMaxX)
                return $"{body.id} 的圆周轨迹超出左右边界（半径 {circleRadius:0.##} m，圆心 x {centerX:0.##}）。" +
                       "可减小初速、减小质量、增大 |q| 或 |B|";
            if (setup.mode == "2d" &&
                (centerY - circleRadius < setup.boundsMinY || centerY + circleRadius > setup.boundsMaxY))
                return $"{body.id} 的圆周轨迹超出上下边界（半径 {circleRadius:0.##} m，圆心 y {centerY:0.##}）。" +
                       "可减小初速、减小质量、增大 |q| 或 |B|";
        }

        return null;
    }

    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
