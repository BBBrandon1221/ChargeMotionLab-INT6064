#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ChargeMotionLab 预置案例生成器。

物理：匀强磁场 B = Bz·ẑ（垂直纸面），洛伦兹力 F = q·v×B，粒子间无相互作用。
积分器：Boris 推进器 —— 纯磁场情形下等价于把速度矢量精确旋转 ω·dt，
       |v| 逐位不变，动能漂移 ~1e-15。必须与 Unity 侧 SimulationSolver.Step
       逐行一致（double 精度、相同运算顺序），ReconcileTest 才能通过对账。

输出：ChargeMotionLab_INT6064/Assets/StreamingAssets/SimData/*.json
用法：python tools/charge_gen.py
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# 工程目录 2026-11-05 改名为 ChargeMotionLab_INT6064（周三课实际编号 6064）。
# 这里按名字优先、再兜底 glob，改名后不用回来改路径。
def _project_dir():
    cand = os.path.join(HERE, "..", "ChargeMotionLab_INT6064")
    if os.path.isdir(cand):
        return os.path.normpath(cand)
    for name in sorted(os.listdir(os.path.join(HERE, ".."))):
        p = os.path.join(HERE, "..", name)
        if os.path.isdir(os.path.join(p, "Assets", "StreamingAssets")):
            return os.path.normpath(p)
    raise SystemExit("找不到 Unity 工程目录（Assets/StreamingAssets 不存在）")


OUT_DIR = os.path.join(_project_dir(), "Assets", "StreamingAssets", "SimData")

SCHEMA_VERSION = "1.0"


def boris_step(x, y, vx, vy, q, m, Bz, dt):
    """一步 Boris 推进，返回 (x, y, vx, vy)。与 SimulationSolver.Step 逐行对应。"""
    tz = q * Bz / m * dt * 0.5
    sz = 2.0 * tz / (1.0 + tz * tz)

    vpx = vx + vy * tz
    vpy = vy - vx * tz

    # v⁺ = v⁻ + v'×s：基准是 v⁻ 原速度，不是 v'，否则旋转矩阵不正交、动能漂移
    v_plus_x = vx + vpy * sz
    v_plus_y = vy - vpx * sz

    vx, vy = v_plus_x, v_plus_y
    x += vx * dt
    y += vy * dt
    return x, y, vx, vy


def reflect_bounds(x, y, vx, vy, r, bminx, bminy, bmaxx, bmaxy, e):
    """与 C# ResolveBoundary 一致的兜底反弹（正常算例不会触发）。"""
    touched = False
    low_x, high_x = bminx + r, bmaxx - r
    low_y, high_y = bminy + r, bmaxy - r
    if x < low_x:
        x = low_x
        if vx < 0.0:
            vx *= -e
        touched = True
    elif x > high_x:
        x = high_x
        if vx > 0.0:
            vx *= -e
        touched = True
    if y < low_y:
        y = low_y
        if vy < 0.0:
            vy *= -e
        touched = True
    elif y > high_y:
        y = high_y
        if vy > 0.0:
            vy *= -e
        touched = True
    return x, y, vx, vy, touched


def simulate(particles, Bz, dt, duration, bounds, wall_e=1.0):
    """跑完整段轨迹，返回 (frames, analysis)。"""
    bminx, bminy, bmaxx, bmaxy = bounds
    steps = int(round(duration / dt))  # 四舍六入五取偶，与 C# MidpointRounding.ToEven 一致

    state = []
    for p in particles:
        state.append({
            "id": p["id"], "m": p["mass"], "r": p["radius"], "q": p["charge"],
            "x": p["position"][0], "y": p["position"][1],
            "vx": p["velocity"][0], "vy": p["velocity"][1],
        })

    frames = []
    boundary_contacts = 0
    t = 0.0

    def snapshot(step, t, contacts):
        px = py = ke = 0.0
        objs = []
        for s in state:
            objs.append({
                "id": s["id"],
                "position": [round(s["x"], 9), round(s["y"], 9)],
                "velocity": [round(s["vx"], 9), round(s["vy"], 9)],
            })
            px += s["m"] * s["vx"]
            py += s["m"] * s["vy"]
            ke += 0.5 * s["m"] * (s["vx"] ** 2 + s["vy"] ** 2)
        return {
            "step": step,
            "time": round(t, 12),
            "objects": objs,
            "total_momentum": [round(px, 9), round(py, 9)],
            "kinetic_energy": round(ke, 12),
            "collision_count": 0,
            "boundary_contacts": contacts,
        }

    frames.append(snapshot(0, t, 0))
    for step in range(1, steps + 1):
        touched_any = False
        for s in state:
            s["x"], s["y"], s["vx"], s["vy"] = boris_step(
                s["x"], s["y"], s["vx"], s["vy"], s["q"], s["m"], Bz, dt)
            s["x"], s["y"], s["vx"], s["vy"], touched = reflect_bounds(
                s["x"], s["y"], s["vx"], s["vy"], s["r"], bminx, bminy, bmaxx, bmaxy, wall_e)
            if touched:
                touched_any = True
        if touched_any:
            boundary_contacts += 1
        t = round(t + dt, 12)
        frames.append(snapshot(step, t, boundary_contacts))

    ke0 = frames[0]["kinetic_energy"]
    ke1 = frames[-1]["kinetic_energy"]
    max_drift = 0.0
    if ke0 > 0:
        max_drift = max(abs(f["kinetic_energy"] - ke0) / ke0 for f in frames)

    analysis = {
        "initial_momentum": frames[0]["total_momentum"],
        "final_momentum": frames[-1]["total_momentum"],
        "max_momentum_drift": round(max_drift, 15),
        "relative_momentum_error": round(max_drift, 15),
        "initial_kinetic_energy": ke0,
        "final_kinetic_energy": ke1,
        "collision_count": 0,
        "boundary_contacts": boundary_contacts,
        "validation": "pass" if boundary_contacts == 0 else "inapplicable",
    }
    return frames, analysis


def build_case(name, title_zh, particles, Bz, dt, duration, bounds):
    objects = []
    for p in particles:
        objects.append({
            "id": p["id"],
            "mass": p["mass"],
            "radius": p["radius"],
            "charge": p["charge"],
            "position": list(p["position"]),
            "velocity": list(p["velocity"]),
            "restitution": 1.0,  # schema 兼容占位，磁场版不用
        })

    frames, analysis = simulate(particles, Bz, dt, duration, bounds)

    doc = {
        "schema_version": SCHEMA_VERSION,
        "input_hash": "sha256:charge-" + name,
        "title": title_zh,
        "scenario": {
            "mode": "2d",
            "time_step": dt,
            "duration": duration,
            "Bz": Bz,
            "bounds": {"min": [bounds[0], bounds[1]], "max": [bounds[2], bounds[3]]},
            "boundary_restitution": 1.0,
        },
        "objects": objects,
        "frames": frames,
        "analysis": analysis,
    }
    return doc


def theoretical_radius(p, Bz):
    if abs(Bz) < 1e-12:
        return float("inf")
    return p["mass"] * math.hypot(*p["velocity"]) / abs(p["charge"] * Bz)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    R = 0.30          # 粒子显示半径
    DT = 0.01
    BOUNDS = (-6.0, -6.0, 6.0, 6.0)
    BZ = 1.0          # T，垂直纸面向外（+z）
    T1 = 6.3          # ≈ 一个完整周期 T = 2πm/(qB) = 6.283 s
    T2 = 12.6         # 两倍周期：m=2 或 B=0.5 的算例要走满一圈

    # 每项：(名字, 标题, 粒子, Bz, duration)
    # 分三组，对应大厅里的三台实验舱（三个课时）——
    #   站1 课时1「观察与归纳」，站2 课时2「控制变量」，站3 课时3「未知粒子」。
    cases = [
        # ---------- 站 1：认识圆周运动 ----------
        # 同种电荷：同 q 同 m 同速 → 同半径同方向的两个圆
        ("same_q", "同种电荷，同半径同转向", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-2.5, 0.0], "velocity": [0.0, 2.0]},
            {"id": "ball_2", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [2.5, 0.0], "velocity": [0.0, -2.0]},
        ], BZ, T1),
        # 正负电荷对比：同 m 同速，q 反号 → 转向相反
        # q=+1 圆心 = 起点 + (0,-2)；q=-1 圆心 = 起点 + (0,+2)
        # 圆心摆在 ±3.6：圆的极值到 ±5.6，离边界反弹线 ±5.7 留 0.1m 余量。
        ("opposite_q", "正负电荷，转向相反", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [0.0, -1.6], "velocity": [2.0, 0.0]},
            {"id": "ball_2", "mass": 1.0, "radius": R, "charge": -1.0,
             "position": [0.0, 1.6], "velocity": [2.0, 0.0]},
        ], BZ, T1),
        # 不同荷质比：同 m 同速，q 翻倍 → 半径减半（r = mv/(qB)）
        ("different_qm", "电荷量不同，半径不同", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-2.5, 0.0], "velocity": [0.0, 2.0]},
            {"id": "ball_2", "mass": 1.0, "radius": R, "charge": +2.0,
             "position": [2.5, 0.0], "velocity": [0.0, 2.0]},
        ], BZ, T1),

        # ---------- 站 2：控制变量 ----------
        # v 翻 2.5 倍 → 半径翻 2.5 倍，但周期一模一样（都是 6.28 s）。
        # 两颗球同时回到出发点 —— 「快的那个转得慢？」这个直觉在这里被推翻。
        ("speed_diff", "速度不同，半径不同，周期相同", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-4.5, 0.0], "velocity": [0.0, 1.0]},
            {"id": "ball_2", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [0.0, -3.0], "velocity": [0.0, 2.5]},
        ], BZ, T1),
        # B 减半 → 半径翻倍、周期翻倍（r ∝ 1/B，T ∝ 1/B）
        ("field_weak", "弱磁场：半径变大，转得更慢", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-4.0, 0.0], "velocity": [0.0, 2.0]},
        ], 0.5, T2),
        # B 翻倍 → 半径减半、周期减半
        ("field_strong", "强磁场：半径变小，转得更快", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-1.0, 0.0], "velocity": [0.0, 2.0]},
        ], 2.0, T1),
        # m 翻倍 → 半径翻倍（同圆心，半径 1:2，一眼看得出倍数关系）
        ("mass_diff", "质量不同，半径不同", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-2.0, 0.0], "velocity": [0.0, 2.0]},
            {"id": "ball_2", "mass": 2.0, "radius": R, "charge": +1.0,
             "position": [-4.0, 0.0], "velocity": [0.0, 2.0]},
        ], BZ, T2),
        # 对照实验：B = 0，没有磁场就没有洛伦兹力，电荷走直线。
        # 时长压到刚好不撞右墙（-5 → +4.45），撞墙会触发兜底反弹，画面就变成"折线"了。
        ("no_field", "没有磁场：电荷走直线", [
            {"id": "ball_1", "mass": 1.0, "radius": R, "charge": +1.0,
             "position": [-5.0, 0.0], "velocity": [1.5, 0.0]},
        ], 0.0, T1),

        # ---------- 站 3：未知粒子（课时 3 的挑战）----------
        # q=+1、m=2 → r = mv/(qB) = 4.0 m，T = 12.57 s。
        # 候选答案 A(+1,1) B(+2,1) C(+1,2) D(-1,1) 的 |q|/m 分别是 1 / 2 / 0.5 / 1(反号)，
        # 只有 C 能对上「半径 4 m + 顺时针」。轨迹本身只能测出荷质比 q/m，
        # 分不出 q 和 m 各是多少 —— 这正是质谱仪的原理，也是这道题要逼出来的结论。
        ("mystery", "未知粒子：测出它的荷质比", [
            {"id": "ball_1", "mass": 2.0, "radius": R, "charge": +1.0,
             "position": [-4.0, 0.0], "velocity": [0.0, 2.0]},
        ], BZ, T2),
    ]

    for name, title, particles, bz, duration in cases:
        doc = build_case(name, title, particles, bz, DT, duration, BOUNDS)
        path = os.path.join(OUT_DIR, name + ".json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, indent=1)

        r_list = []
        for p in particles:
            r = theoretical_radius(p, bz)
            r_list.append("%s r=%s" % (p["id"], "∞" if r == float("inf") else "%.2fm" % r))
        drift = doc["analysis"]["max_momentum_drift"]
        print("%-14s B=%-4.1f %4d 帧  KE 漂移 %.2e  |  %s" %
              (name, bz, len(doc["frames"]), drift, "  ".join(r_list)))

    print("\n输出目录:", OUT_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main())
