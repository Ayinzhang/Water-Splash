# MPM 白沫改进、RTX 分阶段实测与移动端路线

日期：2026-10-27。以用户最新偏好为准：保留夸张的 MPM 飞溅，默认 8 子步，单池切换 Zibra，不用双池并排。

## 已落实

- 物理范围仍为 4m 立方体；可见地板改为 4m×4m，与物理边界一致，移除视觉线框。没有扩大体积来改变双方网格工作量。
- 比较场景默认 MPM 8 子步；按钮可选择 1/2/3/4/6/8/12/16/24/32。换粒子档位保留手选值，另有保守推荐按钮。
- 原有 EOS 立方压力、声速 6m/s、粘性 .15 保留；可选的仿射/散度耗散均为 0。没有速度截断或高度封顶来强行匹配 Zibra。
- 水面白沫从圆点改成沿流向、贴水面法线的重叠椭圆密度覆盖，加入深度边界感知柔化和世界坐标细节；空中喷雾独立。
- 不增加白沫粒子数，不增加邻域搜索或额外模拟子步。过滤在现有合成阶段完成。
- 比较 Renderer 的 continuousFoam 开关能在同一模拟状态比较新旧外观。Captures/Foam-before.png 与 Foam-after.png 是同状态 128K 对照。稀疏孤立种子仍可能辨认出形状，这版主要消除圆珠状堆叠，尚不是完整的泡沫纹理输运系统。

## 设备及计时方法

Unity 2022.3.62f3 / URP 14 / Windows / Direct3D11。实际渲染设备为 **NVIDIA GeForce RTX 5070 Ti Laptop GPU**，不是 Intel UHD，也不能当作桌面版同名显卡或手机成绩。

初态、4m 范围、粒子数量、有效空间网格、重力、相机与 1280×720 输出相同。32K 对照为 MPM 存储 48³（43³ 物理区域），Zibra 43³；128K 为 MPM 80³（75³ 物理区域）。一次请求推进 1/60s；MPM 子步和 Zibra native iteration 不是同一种精度单位。

GPU 数据来自 Unity GPU timestamp Recorder；每次分母使用实际 sample block 数。先热身 24 个渲染帧，然后至少采集 180 个请求帧和 60 个有效求解器计时帧（最多 1200）。有效数据稀疏，因此部分配置延长到 4–11 秒模拟时长。**这些平均数是阶段成本诊断，不是逐帧相同水形/相同冲击时刻的严格画质等价排名。** 小幅差异、开关白沫前后的微小差值不能当作精确加速比。

MPM 求解成本 = 五个阶段的 ms/block×子步数 + 每 tick 一次白沫阶段。渲染成本按一次相机渲染计算。诊断同步、截图不在计时窗口。数字不包含整个 Unity 帧、UI、全部驱动开销。Profiler 本身也有成本。完整原始数据：rtx-ab-gpu.json；早期较少样本数据已单独保留。

### 总成本

| 配置 | 有效求解计时帧 | GPU 求解 ms / 1/60s tick | 水面渲染 ms / frame |
|---|---:|---:|---:|
| Zibra 32K，1 iteration，白沫关 | 60 | 0.227 | 0.311 |
| MPM 32K，4 子步，白沫关 | 60 | 1.199 | 0.382 |
| MPM 32K，8 子步，白沫关 | 60 | 2.092 | 0.399 |
| Zibra 32K，1 iteration，白沫开 | 60 | 0.243 | 0.379 |
| MPM 32K，4 子步，新白沫开 | 60 | 1.143 | 0.423 |
| Zibra 32K，2 iterations，白沫关 | 60 | 0.426 | 0.426 |
| MPM 32K，8 子步，新白沫开 | 83 | 2.238 | 0.423 |
| MPM 128K，8 子步，新白沫开 | 121 | **7.946** | **0.713** |

32K 基础求解 MPM4/Zibra1 约 5.3 倍；MPM8/Zibra1 约 9.2 倍。这描述当前实现与配置，不能归因为 MPM 对所有算法天然慢该倍数。Zibra density whitening 与 MPM 独立喷雾/泡沫粒子并非相同白沫功能。

### MPM 128K / 80³ / 8 子步

| 阶段 | ms / dispatch | ms / 1/60s tick |
|---|---:|---:|
| 清网格 | 0.01928 | 0.15425 |
| 质量/动量 P2G | 0.47879 | 3.83033 |
| 密度/应力传输 | 0.41344 | 3.30750 |
| 网格速度 | 0.00770 | 0.06163 |
| G2P / 推进 | 0.07081 | 0.56650 |
| 白沫输运/生成 | 0.02614 | 0.02614 |
| 合计 | — | **7.94634** |

两次传输共 **7.138ms，约 89.8%** 的求解时间。每主粒子/子步 27×(4+3)=189 次全局整数 atomicAdd，128K×8 约 **1.98 亿次/tick**；另有 80B 粒子状态、重复权重计算、密度读回与诊断原子操作。

渲染：深度 0.35510ms，厚度 0.14157ms，平滑 0.16928ms，法线 0.00817ms，白沫覆盖绘制 0.01219ms，含新白沫合成的着色 0.02416ms，喷雾 0.00218ms。**改白沫外观不会解决求解器的主要差距。**

Zibra 32K/1 iteration 基础：Clear SDF 0.02165ms、native solver 0.20527ms；背景拷贝 0.00890ms、native surface 0.27398ms、着色 0.02775ms。其原生二进制内部的 P2G/压力/排序阶段没有可见标记，因此未伪造对应阶段。

## 128K 实时运行验证

独立关闭阶段录制后，128K / 80³ / 8 子步 / 720p High / 白沫开 / 确定性运动球 / 60 FPS 上限：

- 20.0045s 墙钟推进 20.0000s 模拟，积压 0.00479s；fault=0。
- 无非有限粒子、越界、球内残留；最终网格质量 131071.74 / 131072。
- 最近 600 渲染帧平均 16.6807ms（约 59.95 FPS），P95 24.0245ms。
- 平均能达到 60 FPS，**不能声称每帧锁 60**；这也不是安卓验收。
- 数据：128k-eight-realtime.json 与 comparison-MlsMpm-20261027-134138.json。
- 31 项 Unity 测试通过；8 组切换/重复重建回归通过。切换回归细网格使用保守 12/16 子步；128K/8 的移动交互由上面的独立长跑覆盖。

## 反弹与低子步结论

无移动球、无 kick、无白沫、无渲染的 6 秒审计复现大反弹。初始最高约 2.481m；原 gamma3/c6 在 4、8、16 子步都可达到 4m 顶部，99 分位高度最高约 3.8m。提高子步并未消除。

平动 + APIC 仿射 + 重力势能 + EOS 压缩势能的诊断没有显示总能量持续超过初始（诊断不是严格守恒不变量，密度有子步时序差，不能证明数学守恒）。观察到撞击时约两倍压缩、随后压力能释放与局部动量集中，符合视觉上弹飞但总能量仍衰减的情况。Zibra 暴露了仿射衰减、散度衰减和原生限速，材料设置不等价。

原 gamma3/c6：1/2/3 子步分别在 tick19/27/35 触发 fault16。gamma1/c6 的 3 子步可通过六秒，但 1/2 仍失败。降低声速到3也不能直接让1/2子步稳定。故不把他人“一步稳定”的配置直接照搬到4m、43格、固定60Hz。按钮保留实验低步数，默认按最新偏好设8。

同一初态前0.1秒质心二阶差分：MPM有效重力约 -9.807m/s²，Zibra约 -9.558m/s²；native 时间换算基本同数量级，仍有约2.6%差异与早期离散/边界影响。没有为追求表面相同数字而偷偷更改重力。见 freefall-calibration.json。

固定粒子数、稠密网格和子步数时，粒子飞高不会直接增加五个主核的工作量；但会提高稳定步数要求、扩展投影覆盖、喷雾寿命及透明 overdraw。因此保留夸张外观与优化 P2G 可以同时进行。

## 移动端路线建议

以下是主源阅读后的工程判断，**没有一项在 Redmi K90 Pro Max 本项目场景做过实机 A/B**。

| 路线/参考 | 适配此次需求 | 代价与建议 |
|---|---|---|
| 保留 MLS-MPM，分桶/共享内存聚合/分块传输 | 直接保留当前立体飞溅与现有白沫 | 第一优先；针对已测89.8%传输瓶颈。粒子分桶和tile聚合有额外开销，需实测，不承诺固定倍数。先避免额外physics重写。 |
| Splash / WebGPU-Ocean | 同类MLS-MPM+屏幕空间表面，风格最接近 | 借鉴线性EOS、较低子步、表面filter，但稳定性受单位/声速/时间步共同影响；不是Android Unity成品。 |
| Obi 7 Compute + advected foam | 完整Unity体积流体、泡沫、喷雾链路，CPU Burst与GPU Compute可选 | 最值得做商业框架验证。官方源代码/文档有密度约束、被动白沫、弹道spray；不等于必然更快。Obi7网格表面基于体素surface nets，手机上网格重建也需预算。未购买/导入。 |
| PBF / XPBD 密度约束 | 若要更容易限制压缩和弹性反弹，可列为3D备选 | 需要邻域构建、排序、多次密度/位置约束遍历；省显式声学子步不等于总成本低。主粒子少而泡沫多可能合理，但先做同场景原型计时。 |
| EA PB-MPM | 有位置投影、分桶、workgroup tile与融合G2P2G可借鉴 | 当前公开演示是2D，shader位置是vec2。README也说明移动端体验未适配。3D移植涉及27点支持、共享内存/寄存器和调度成本，不能当即插即用手机加速器。 |
| lamp-cap FLIP / Narrow Band FLIP | 可追求薄水片和低耗散飞溅；用户参考画面有价值 | 主demo为4M粒子、256×128×128、UAAMGPCG、Marching Cubes，作者仅测RTX4070 wave32。压力投影/排序/网格重建成本不能忽略；窄带是削减粒子工作的更合理研究方向，但不先整套移植。 |
| 水面heightfield/浅水 + 局部3D粒子/水片 + advected foam | 最可能大幅减手机成本，保留撞击局部夸张效果 | 推荐作为移动端独立渲染/模拟LOD。底层水面2D成本，飞溅局部3D；无法天然表达任意全池翻卷、洞穴或多层水体，视觉边界需接受。 |
| Crest / FluXY | Crest动态波/泡沫；FluXY二维GPU纹理输运 | 可借鉴表面/白沫层。FluXY是2.5D密度/速度纹理模拟，不是现有3D水块的替换。Crest主要是海面/动态波，不是粒子体积自由表面求解器。 |

排序建议：**先现有MPM传输优化 → 在手机量测 → 若整体热/带宽预算仍不达标，采用局部3D+水面LOD → 需要完整框架时评估Obi7。** 暂不因方法名字直接切PB-MPM或完整FLIP。

白沫下一档可独立演进：将粒子覆盖烘到贴水面/局部网格的可输运泡沫纹理，做时间稳定和小泡孔细节，喷雾继续少量粒子；这会比堆主流体分辨率更直接改善视觉。手机需要量测R16 blending、半分辨率表面、fillrate和长时间温升；桌面60FPS没有替代作用。

## 核验过的主源

2026-10-27通过HTTP读取，下列原文副本在Temp/ReferenceSources；网络搜索工具未返回可用内容，因此此处给原始来源，不构造搜索引文。所有作者示例数值均不是本项目的手机成绩。

1. Splash作者README：<https://github.com/matsuoka-601/Splash> — MLS-MPM、Narrow-Range Filter、gamma5→1与stiffness调节、一子步声明。
2. WebGPU-Ocean：<https://github.com/matsuoka-601/WebGPU-Ocean> — 同类实现与后续喷雾/泡沫方向。
3. Obi7后端：<https://obi.virtualmethodstudio.com/manual/7.0/backends.html> — Compute/Burst、源代码、compute支持要求、小负载CPU可能更合适。
4. Obi7白沫：<https://obi.virtualmethodstudio.com/manual/7.0/particleadvection.html> — passive advection、foam potential、喷雾、可选density control。
5. Obi7渲染：<https://obi.virtualmethodstudio.com/manual/7.0/fluidrendering.html>；性能：<https://obi.virtualmethodstudio.com/manual/7.0/performancetips.html>。
6. Obi7密度约束参数：<https://obi.virtualmethodstudio.com/manual/7.0/emittermaterials.html>。
7. EA PB-MPM：<https://github.com/electronicarts/pbmpm>；shaders/particle.inc.wgsl、g2p2g.wgsl核对维度与共享tile。
8. lamp-cap FLIP：<https://github.com/lamp-cap/Unity_FLIP_Fluid_Simulation> — 粒子/网格规模、wave32硬件测试、UAAMGPCG、窄带、Marching Cubes。
9. PBF原论文：<https://mmacklin.com/pbf_sig_preprint.pdf>。
10. Unified Spray, Foam and Bubbles：<https://cg.informatik.uni-freiburg.de/publications/2012_CGI_sprayFoamBubbles.pdf> — 次级粒子与主流体分离的参考。
11. Crest动态波：<https://crest.readthedocs.io/en/stable/user/waves.html>；水外观：<https://crest.readthedocs.io/en/stable/user/water-appearance.html>。此处读取的是4.23文档，不声称为最新版本。
12. FluXY overview：<https://fluxy.virtualmethodstudio.com/manual/1.0/bigpicture.html> — 2D纹理状态与pseudo3D定位。
13. Cirrus作者项目：<https://wang-mengdi.github.io/proj/25-cirrus/> — 粒子/自适应网格GPU优化参考；RTX4090研究结果不可外推Android。
