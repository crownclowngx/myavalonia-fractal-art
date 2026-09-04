# G0009 实施说明

## SOLID 落点

| 原则 | 实际实现 |
| --- | --- |
| 单一职责 | 公式策略、轨道采样、点云值、密度累积、密度着色、局部发光、图节点与 UI 投影分别实现 |
| 开闭原则 | 新公式实现同一窄策略；既有 Julia、Mandelbrot、路径和合成代码无需修改算法 |
| 里氏替换 | Clifford/De Jong 遵守同一有限坐标、确定性和取消契约；测试按同一生成端口验证 |
| 接口隔离 | 点云生成、密度、密度渐变和局部发光各自只有所需输入输出 |
| 依赖倒置 | 图节点和 Document 依赖应用/领域端口；公式与像素算法不依赖 Avalonia 或 Host |

## 领域与创作图

- 作品格式升级为 v8，追加 `StrangeAttractor` 生成器、`AttractorFormula` 与完整吸引子配方；
- 创作图追加 `StrangeAttractorPoints → PointDensity → DensityGradient → DensityGlow`；
- 遮罩旁路改为执行到规范图唯一 `ScalarField` 节点，吸引子会执行点云和密度但跳过颜色与发光；
- `RenderContext.PointSampleBudget` 明确区分作品最终点数和本次预览实际预算；
- 节点缓存按真实读取参数建立键，颜色或发光修改不会重新采样点云。

## 持久化、变体和 UI

- v8 图层、候选和收藏完整保存吸引子定义；v7 补默认定义，v1–v6 延续原迁移链；
- 变体保持公式、预热和采样数，形态模式变异 A–D，质感模式变异曝光、Gamma、局部发光和渐变；
- 新增极光织网、丝绸星云、星尘花冠和深海回环四个预设；
- 图层面板增加“+ 吸引子”，属性区提供公式、A–D、预热、采样、曝光、Gamma 和局部发光；
- 吸引子可作为图层和 Mask 源；复平面拖动/缩放仍只属于 Julia/Mandelbrot。

## 外部兼容

Plugin/Document ID、Document Content schema、Workflow Recipe 外层 v1、Workflow Action 和 File Artifact v1
全部保持不变。G0008 Master Bloom 与四类既有生成器的渲染路径不修改。
