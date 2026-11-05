# ChargeMotionLab · 电荷在磁场中的运动

> 三台实验舱、九个案例、一个“这颗粒子到底是什么”的挑战 —— 学生自己从轨迹里把荷质比量出来。

香港教育大学（EdUHK）MSc AI & Educational Technology · **INT6067** 课程项目
作者：**黄炯桦（HUANG Jionghua）** · GitHub [@BBBrandon1221](https://github.com/BBBrandon1221)

![实验舱大厅](docs/hall.png)

---

## 这个项目在做什么

“电荷垂直射入匀强磁场做圆周运动、半径 r = mv/(qB)” 这条公式，讲一遍学生就忘。这里把它拆成三个课时，学生走进一间实验舱大厅，点开对应的舱，自己改参数、看轨迹、下结论：

| 课时 | 主题 | 案例 | 认知层次 |
|---|---|---|---|
| **① 转向与半径** | 找出转向和半径的规律 | 同种电荷 / 异种电荷 / 电荷量不同 | 找规律（pattern recognition） |
| **② 控制变量** | 一次只改一个量 | 速度不同 / 弱场 / 强场 / 质量不同 / 无磁场 | 算法与抽象（只改一个变量，看结果怎么变） |
| **③ 未知粒子** | 测出它的荷质比 | mystery：q、m、r、T 全部遮住 | 评估（从网格量半径，用 r = mv/(qB) 反算并验证） |

![课时一](docs/lesson1.png)
![课时三](docs/lesson3.png)

课时三是一道真正的挑战题：粒子参数被遮成 `?`，动能显示 `—`（因为动能 = ½mv²，给了 v 就能反算 m，会把答案漏出去），学生只能从网格量半径、拿 r = mv/(qB) 逐个试算，最后在四选一答题卡上作答，答对后补一句“轨迹只能定出 q/m 的比值，定不出各自的值”。

## 功能

**实验舱大厅（2.5D 第一人称）**：三台玻璃舱 = 三个课时。WASD 走动、右键环视、点舱体或按 `1/2/3` 进入，`Esc` 退回大厅并站在刚才那台舱前。

**物理回放**：帧序列 JSON 驱动，播放 / 暂停 / 单步 / 重置 / 0.5×–4× 倍速；坐标网格与刻度、磁场符号（⊙ 出纸面 / ⊗ 入纸面）、轨迹线与理论圆参考、每颗粒子的速度标签、实时数据卡（速率、动能、磁感应强度、能量守恒校验）。

**参数面板（`P`）**：改电荷量 q、质量 m、初速度 v、磁感应强度 B、时间步长、网格间距，点“重算并播放”就地跑求解器；`Esc` 之外还能一键“恢复原案例”。课时三的参数面板会主动遮罩 q/m/r/T 并禁用 `P` 键。

**双语界面**：中 / 英一键切换，验收对两种语言各跑一遍。

## 快速开始

1. 装 **Unity 2022.3.14f1c1**（其它 2022.3.x 一般也能开）。
2. Unity Hub → Add → 选这个仓库目录 → 打开。
3. 打开 `Assets/Scenes/Main.unity`，按 Play。

命令行出 Windows 包：

```bash
Unity.exe -batchmode -quit -projectPath <仓库路径> \
  -executeMethod BuildWin.RebuildAndBuild -logFile build.log
# 产物：<仓库同级>/_build/Win64/ChargeMotionLab.exe
```

> ⚠️ 环境里同时存在 `http_proxy` 和 `HTTP_PROXY` 会让 Unity 的 Bee 崩掉，出包前先 `unset` 掉全部代理变量。

## 操作

| 场景 | 按键 | 作用 |
|---|---|---|
| 大厅 | `W A S D` / 右键拖动 | 走动 / 环视 |
| 大厅 | 左键点舱体、`1` `2` `3` | 进入对应课时 |
| 实验里 | `P` | 参数面板（课时三禁用） |
| 实验里 | `空格` / 按钮 | 播放 / 暂停 |
| 实验里 | `C` | AI 助教 |
| 实验里 | `F5` | 重读当前 JSON |
| 实验里 | `Esc` | 退回大厅 |

## 命令行验收

```bash
ChargeMotionLab.exe -autotest D:/out.txt -lang zh
ChargeMotionLab.exe -autoshot D:/shot.png -dataset SimData/same_q.json -shotdelay 3 -lang zh
ChargeMotionLab.exe -autoshot D:/hall.png -starthub -shotdelay 4          # 大厅
ChargeMotionLab.exe -autoshot D:/st3.png -starthub -enterstation 2 -shotdelay 6  # 直接进课时三
```

路径请给**绝对路径**（相对路径会被 `ScreenCapture` 写到 persistentDataPath）。

## 数据与生成脚本

九个案例的数据在 `Assets/StreamingAssets/SimData/`，由 `tools/charge_gen.py` 生成（纯 Python，无第三方依赖）：

```bash
python tools/charge_gen.py      # 重新生成全部案例，输出到 Assets/StreamingAssets/SimData/
```

脚本按目录名探测工程位置（改工程目录名不用改脚本）。数据结构与 MomentumLab 同源：

```json
{ "scenario": { "mode": "2d", "time_step": 0.01, "duration": 6.3, "Bz": 1.0,
                "bounds": { "min": [-6, -6], "max": [6, 6] }, "boundary_restitution": 1.0 },
  "objects": [{ "id": "ball_1", "mass": 1.0, "charge": 1.0, "radius": 0.3,
                "position": [-2.5, 0], "velocity": [0, 2.0], "restitution": 1.0 }],
  "frames": [{ "step": 0, "time": 0.0,
               "objects": [{ "id": "ball_1", "position": [-2.5, 0], "velocity": [0, 2.0] }],
               "total_momentum": [0.0, 0.0], "kinetic_energy": 4.0,
               "collision_count": 0, "boundary_contacts": 0 }],
  "analysis": { "validation": "pass", "relative_momentum_error": 0.0 } }
```

磁场垂直于运动平面（`Bz`），洛伦兹力不做功，所以动能应当整段不变——这也是 `-autotest` 里“能量守恒 ✓ 通过”那条判据的来源。

## 踩过的坑

打包后**内置 shader 变体会被剥离**，症状是“编辑器里好好的，exe 里全没了”。这个项目踩了三类，最后都改成自写 shader 放 `Assets/Resources/Shaders/`，用 `Resources.Load<Shader>` 加载：

- Standard 的 `_EMISSION` 关键字（自发光灯带、指示灯）→ `ChargeLab/Glow`
- Standard 透明混合（玻璃舱罩）→ `ChargeLab/ChamberGlass`
- `Sprites/Default` 被剥掉，兜底 `Unlit/Color` 不读顶点色 → 所有 LineRenderer 画成白色、在浅色背景上隐形 → `ChargeLab/LineColor`

另外两个：

- **正交相机取景只设 `position` 不设 `rotation`**：从大厅进来时相机带着“站在某台舱前”的朝向，正交一斜视物理平面就侧对出去，画面全空。取景必须先把 `rotation` 摆正。
- **取景用到启动那一帧的纵横比**（启动瞬间窗口可能是竖条），正交尺寸会一直偏大一圈 → 缓存首帧 aspect，之后每帧比对，变了重新取景。

## 目录结构

```
Assets/
  Scenes/Main.unity
  Editor/BuildWin.cs                batchmode 构建入口
  Editor/PlaybackSceneBuilder.cs    场景由代码生成
  Scripts/
    PhysicsPlayback/                ChargeLabHub（大厅三舱）/ 回放 / 网格 / 参数面板 /
                                    LabRoomController（实验室房间）/ 轨迹与理论圈
    AI/                             DeepSeek 客户端 + 实验上下文注入
    UI/                             HUD、控制条、答题卡、主题
    AutoTest.cs / AutoShot.cs       无界面验收与截图
  Resources/Shaders/                自写 shader（Glow / ChamberGlass / LineColor）
  StreamingAssets/SimData/          九个案例的数据
tools/charge_gen.py                 案例数据生成脚本
```

## 署名与致谢

- **作者**：黄炯桦（HUANG Jionghua）/ [@BBBrandon1221](https://github.com/BBBrandon1221)
- 课程：EdUHK MSc AI & Educational Technology · INT6067 学习科学相关课程的小组项目
- 与作者同系列的另一个项目：[MomentumLab-INT6066](https://github.com/BBBrandon1221/MomentumLab-INT6066)（动量守恒，同一套回放框架）

## 许可

课程作业代码，欢迎 fork、阅读、拿去改自己的教学项目，**保留作者署名**即可。
仓库内不含任何 API key：AI 功能需要你自己把 `ai_config.sample.json` 复制成 `ai_config.json` 并填上 `sk-` 开头的 key（该文件已被 `.gitignore` 排除）。
