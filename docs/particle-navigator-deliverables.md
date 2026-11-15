# 第四站·粒子导航员 —— 交付指标对照清单

> 负责部分：在 Unity 现有 Boris 求解器上实现第四台挑战「粒子导航员：把粒子送进接收器」。
> 三关递进 + 预测后发射 + 前后轨迹对比。本页按老师给的交付指标逐项对照，均已有实物证据。

## 一、交付指标对照

| # | 交付指标 | 状态 | 证据 / 位置 |
|---|---|---|---|
| 1 | 可运行包 | ✅ | `_build/Mac/ChargeMotionLab.app`（macOS 通用包，双击即开）。Windows `.exe` 见第四节。 |
| 2 | 成功 / 失败参数 | ✅ | `docs/particle-navigator-guide.md` 第五节（第一/二/三关参数表） |
| 3 | 测试结果 | ✅ | `docs/particle-navigator-selftest.txt`（`SelfTest()` 实测，4 项判据全过） |
| 4 | 稳定演示失败和成功 | ✅ | `docs/nav-demo/01_level1_fail.png`（失败）、`02_level1_hit.png`（成功）、`03_level2_fail.png`、`04_level2_hit.png` |
| 5 | 退出无状态残留 | ✅ | `ParticleNavigatorModule.HideModule()` 只关根节点、清运行态，`Active=false`，重进即新局 |
| 6 | 支持功能演示、设计与评价机会 | ✅ | 三关递进＝设计机会；命中后可换参数找「另一种解」＝评价机会（guide 第三、七节） |
| 7 | PPT 现场操作约 1.5 分钟 | ✅ | guide 第七节演示脚本（0:00–1:25，逐步台词） |
| 8 | 相关演示素材 | ✅ | `docs/nav-demo/*.png`（01–05 为 1920×1080，06 由视频抽帧 1280×720）＋本页复现命令 |
| 9 | 离线运行 | ✅ | 纯本地 Borís 积分，无任何网络依赖 |

## 二、自检结果（实测）

```
r=3.0  解析落点 x=-5.764  数值 x=-5.770  差 6.0 mm  OK
r=4.0  解析落点 x=-4.127  数值 x=-4.135  差 8.0 mm  OK
r=5.0  解析落点 x=-3.000  数值 x=-3.010  差 10.0 mm  OK
方向翻转：向外→bottom  向里→top  OK
半径单调：v=3 落点 -5.77 → v=6 落点 -2.10  OK
能量守恒：|v| 3 → 3.000000  漂移 1.3e-15  OK 磁场力不做功
```

## 三、演示截图（1920×1080）

| 图 | 关卡 / 操作 | 预期 |
|---|---|---|
| `01_level1_fail.png` | 第一关默认（磁场向里）发射 | 撞上壁 → 红字「方向反了」 |
| `02_level1_hit.png` | 第一关切「向外」发射 | 下壁接收器命中 |
| `03_level2_fail.png` | 第二关默认 `v=3` 发射 | 同壁偏左，未进窄区 |
| `04_level2_hit.png` | 第二关 `v=4` 发射 | 命中（r=4.00 m） |
| `05_level3_predict.png` | 第三关（未发射） | 三段拼接磁场 + 每段 ⊙/⊗ 强度标注 + 三个检查点 + 灰色预测弧线 + 底部接收器 |
| `06_前后轨迹对比.png` | 第三关第二种解飞行中（视频抽帧） | 上一发绿色（命中）+ 当前黄色，同屏对照 |

> 「前后轨迹对比」：连续发射两次（保持 `V` 开启），上一发以细彩线留下（绿=上次命中 / 红=上次失败），与当前黄线同屏。`06` 由下面的演示视频抽帧得到。

## 三·五、演示视频（英文版）

- `ParticleNavigator_Demo_EN.mp4`（约 56 s，1280×720，30 fps，**英文界面**）：一镜到底演示三关——
  - **第一关 · 方向**：Into page 撞上壁失败 → 切 Out of page 收入下壁命中；
  - **第二关 · 速度（半径）**：v=3 同壁偏左失败 → 灰线缓慢增大到 v=5.5 冲过头（差一点）失败 → 调回 v=4 进接收器命中；
  - **第三关 · 路径规划**：三段拼接磁场（⊙1.1 / ⊗1.0 / ⊙1.2），默认 v=2.6 提前出界 → 提示「路线不完整」；调整 v、角度依次穿过 ①②③、检查点逐个点亮后命中；再换一组参数给出**第二种解**命中。
- 截图 01–05 亦为英文界面（1920×1080）。
- 录制方式：程序运行时逐帧 `ScreenCapture` 落盘（`Time.captureFramerate=30` 保证时基），再用 macOS AVFoundation 编码（不依赖录屏权限/ffmpeg）。

## 四、复现命令（macOS 包）

```bash
APP="/Users/zkyyy/Desktop/周三/_build/Mac/ChargeMotionLab.app/Contents/MacOS/MomentumLab"
# 直接进第四站（-enterstation 必须配 -enterlab 才会自动进站）
"$APP" -starthub -enterlab -enterstation 4
# 出验收图（第一关向外命中）
"$APP" -autoshot "$HOME/Desktop/st4_hit.png" -starthub -enterlab -enterstation 4 \
       -navlevel 1 -navparams 3,1,out,0 -navfire -shotdelay 9 -lang zh
```

Windows 版命令同 README（`ChargeMotionLab.exe ...`）。

## 五、Windows exe 说明

本机只装了 macOS Build Support，出不了 `.exe`。两条路：
1. Unity Hub → 2022.3.14f1c1 → Add modules → 勾 **Windows Build Support**，再菜单 `Tools/80 Build Windows (Win64)`；
2. 由组长在原 Windows 环境用 `BuildWin.Build` 出包（工程已就绪，代码零编译错误）。

## 六、键位速查

`空格` 发射 · `R` 复位 · `G` 预测线开关 · `V` 对比线开关 · `1/2/3` 切关 · `B` 第一关翻磁场 · `N` 第三关换靶 · `Esc` 返回大厅。
