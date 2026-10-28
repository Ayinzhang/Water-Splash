# GPU、稳定性与求解器路线调查（2026-10-27）

## 独显问题

Unity 实测 Intel(R) UHD Graphics / Direct3D11。PCI 设备
`VEN_10DE&DEV_2F18&SUBSYS_604E1D05` 存在，本机 NVIDIA INF 识别为
**NVIDIA GeForce RTX 5070 Ti Laptop GPU**，但设备没有绑定显示驱动，
pnputil 显示 Stopped、Class Unknown，仅有通用 display.inf 候选，nvlddmkm 服务不存在。

`C:/Windows/INF/setupapi.dev.log`：2026-10-25 21:11:09–21:11:24，NVIDIA 安装器
卸载 oem28.inf / nvtf.inf（572.84），删除 nvlddmkm 服务。之后有 NULL driver
安装记录；这些证据不足以判定是谁最初触发卸载。查询设备安装限制策略未返回配置，
不能仅因日志标题包含 Policy Check 就断言是组策略禁用。

找到本机缓存 **617.42**（DriverVer 09/30/2026, 32.0.16.1742），NV_DISP.CAT
签名 Valid，INF 含完整硬件 ID，nvlddmkm.sys 存在。
`Tools/Repair-NvidiaDisplayDriver.ps1` 验证这些条件后调用 pnputil 安装。
当前进程无管理员令牌，故尚未安装、关闭 Unity 或重启。

操作：保存并关闭 Unity，在管理员 PowerShell 运行修复脚本；按实际安装提示
决定是否重启，再打开 Unity，以面板实际 NVIDIA 设备名称为准。已设置的高性能
偏好不能代替缺失驱动。所有本次性能结果均为核显，不是 5070 Ti/安卓成绩。

## fault 16 复现与修正

这是应用的数值保护：速度×子步时长超过 0.75 网格单位，不是 Windows device lost/TDR。
原对比 UI 升粒子/网格档位时保留旧子步，且只在 4/6/8 循环；独立求解器
高档位原本为 12/16 子步。

相同初态、重力、球体轨迹、白沫开启，修正碰撞运动后仍复现：

| 粒子 / 网格 | 子步 | 结果 |
|---|---:|---|
| 64K / 64³ | 4 | 第 23 tick fault 16，约 0.383s |
| 128K / 80³ | 4 | 第 21 tick fault 16，约 0.350s |

球体 1s 后才搅动，因此这条路径由过大数值时间步触发，不需要 native 切换才能发生。
不能保证所有用户操作的 fault 都有相同原因。

已修正：档位恢复 16K=4、32K=8、64K=12、128K=16 子步；UI 从推荐值向上调；
球体一 tick 内逐子步插值，避免先瞬移到终点；独立/对比模式错误状态的 R 键恢复。
保留 fault 条件、实际模拟秒数、积压，不截断速度、不删诊断、不丢时间。

验证数据：`stability-regression-recommended.json`、`stability-regression.json`、
`runtime-switch-regression.json`。推荐档位 16K 10s、32K 20s、64K 10s、128K 5s
搅动通过，无非有限数、越界、球内残留。8 组运行切换/重置通过，覆盖 Zibra↔MLS、
全部档位、同帧多次重建取消；每组 MLS 180 ticks。31 项 Unity 测试通过。
有限场景通过不代表任意外力/任意时长稳定。峰值密度约 2.1–2.3 倍，仍是弱可压缩
实验水；无 fault 不代表体积精度合格，速度保护也不是完整声学 CFL 保证。

## GPU 实测瓶颈

32,768 主粒子、48³、4m、8 子步/tick、32K 白沫容量、1280×720 High，Intel UHD/D3D11。
135 个有效帧，Unity GPU timestamp recorders；每帧 2 tick，按标记次数归一化。
数据 `gpu-stages.json`。Profiler/Editor 有开销；不可外推独显/安卓。

| 阶段 | GPU ms/dispatch | GPU ms/1/60s tick |
|---|---:|---:|
| 清网格 | 0.059 | 0.472 |
| 质量/动量 P2G | 1.424 | 11.395 |
| 密度/应力传输 | 1.332 | 10.659 |
| 网格更新 | 0.028 | 0.225 |
| G2P/推进 | 0.355 | 2.839 |
| 白沫 | 0.272 | 0.272 |
| 求解器标记之和 | — | **25.863** |
| 表面渲染 | **5.759 ms/rendered frame** | 不能与 tick 混算 |

两段传输合计 **85.3%**。每粒子/子步 27×(4+3)=**189 次全局整数原子加法**，
32K×8=49,545,216 次/tick。还包括重复 27 点权重/访存、80B 粒子、多次全局诊断
原子操作和每 tick 40 次求解器 dispatch。显式 EOS 压力使子步不能随意减少。

首要瓶颈是传输数据组织和时间积分，不是白沫。Zibra native 内核未公开，本次未
测到其同条件 GPU 阶段，不能报告可信差距倍数，也不能断言 MLS-MPM 天生慢。

## 路线建议与参考核查

先恢复独显，固定物理场景、网格尺寸、模拟秒数、渲染尺寸重测；相同迭代数不等于
相同质量。第一优先 **按网格块分桶 + shared-memory 局部 P2G/归约**，降低全局冲突
和重复访存；用密度/体积/形态验证，再谈减少子步。保持现有实现作为基线。
若仍受显式压力小步长限制，做 **3D PB-MPM** 第二求解器原型，复用同池、渲染和
白沫接口。它仍需约束迭代与粒子网格传输，大步长稳定不保证等质量更快。

### 用户给的 FLIP 工程

https://github.com/lamp-cap/Unity_FLIP_Fluid_Simulation

检查 README、GPU_FLIP.cs、MGPCG.compute、ParticleToGrid.compute、NarrowBand_FLIP.cs。
作者只验证 RTX4070、wave size 32；GPU_FLIP 硬编码 4,194,304 粒子、256×128×128 网格；
16B 打包粒子、radix 排序/范围、块传输、半精度纹理、UAAMGPCG 压力、多层网格、
marching cubes 表面。`Projection(cmd,64)` 当前编译分支调用一次 MGPCG，64 次
Gauss-Seidel 在未启用分支，不能误读为当前做了 64 轮投影。一次 MGPCG 仍包含
多级平滑、归约和回代。Shader 有 wave/subgroup 操作，手机迁移需另验。

它证明组织良好的 FLIP 可以有效率，不能证明搬到手机比 MPM 快。应借鉴数据组织，
不要把三维压力投影、排序、多级网格、网格提面整套当手机捷径。Narrow band 减少
内部粒子并没有消除压力求解。其压缩、精度与当前 MPM 不同，不能仅按粒子数比。

### PB-MPM 和 PBF

https://github.com/electronicarts/pbmpm ：SIGGRAPH 2024 官方 WebGPU 实现。main 有
分桶、workgroup 网格、融合 g2p2g。shader 用 vec2 网格与二维矩阵，公开 demo 是
**二维**，README 明确未为手机适配，不能外推三维 Android。
https://github.com/AtanGames/ParTan 也明确是二维、未优化、无专门渲染器的 Unity 移植。

Macklin & Müller, Position Based Fluids (2013)：
https://mmacklin.com/pbf_sig_preprint.pdf 。PBF 可作低中粒子数移动候选，但邻域搜索与
多轮邻域遍历也有成本；薄片、体积和飞溅质量需实测，不能保证快于优化的 grid 方法。

| 路线 | 项目取舍 |
|---|---|
| 优化现有 MLS-MPM | 迁移最低；解决已测到传输成本，仍有 EOS/CFL/压缩限制 |
| 3D PB-MPM | 值得小原型验证步长/体积；不能套用二维 demo 成绩 |
| PBF | 小中规模候选；邻域多轮读写可能成为新瓶颈 |
| FLIP/APIC+投影 | 飞溅与组织值得参考；完整迁移/手机适配成本最高 |

安卓分别量主液体、白沫/喷雾、表面 GPU ms，以及内存、密度/体积误差、
同模拟时刻截图、实时率和热稳态。独显接近 Zibra 只是桌面里程碑，不是手机帧率保证。
