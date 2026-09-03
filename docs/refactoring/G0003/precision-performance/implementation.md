# G0003 高精度性能与领域模块化实施说明

> 实施日期：2026-09-03
> 对应计划：[plan.md](plan.md)
> 原始证据：[baseline-results.json](baseline-results.json)、[optimized-results.json](optimized-results.json)

## 1. SOLID 与模块边界

实现采用模块化单体，不引入事件总线、通用计算图或自制容器：

```text
Features/Artwork
    → Application
        → Domain/Artwork
        → Domain/Rendering
            → Domain/Fractals/Julia
                → Numerics
```

- Artwork 只保存 v2 作品配方和资源不变量；
- Viewport 只负责高精度平移/锚点缩放；
- Rendering 保存上下文、场、图像和窄契约；
- JuliaFieldGenerator 只选择 `IJuliaKernel`，公式分别位于 double、定点和扰动策略；
- Numerics 只依赖 BCL，公共十进制模型与内核私有定点上下文分离；
- Application 只编排验证、生成、着色、导出和历史；
- Features 负责 Avalonia Bitmap 与暂态呈现，不决定有效精度。

这对应单一职责、开闭、接口隔离和依赖倒置。具体策略均为朴素对象；没有为未来不存在的分形种类预建框架。

## 2. P01：可复现基线

新增 `tools/FractalArtPlugin.Benchmarks`，只作为本地 Release 工具加入解决方案，不进入插件交付物。工具执行一次
预热和五次采样，记录中位数、P95、总分配、GC、进程 CPU 时间、像素/迭代吞吐、取消响应、参考点和 SHA-256
短指纹，同时保存机器、运行时、提交号与命令。普通测试没有毫秒断言。

## 3. P02：职责拆分

原先平铺的三个 Domain 文件拆成 Artwork、Viewport、Rendering、Fractals/Julia 与 Numerics。测试新增对应的
`Architecture`、`Domain/Fractals/Julia` 和 `Numerics` 目录。移动前先运行原有 30 项测试；移动和后续实现后，
四组基准指纹仍与旧实现一致，v1 迁移和 v2 往返语义不变。

## 4. P03：动态有效精度

`PrecisionPolicy` 依据尺度指数、输入有效位、最大迭代和像素高度推导 `PrecisionDescriptor`。
`RenderContext` 同时记录配置上限、有效位与原因。所需位数超过配置时抛出 `InsufficientPrecisionException`，
不会静默截断。强制任意精度只禁用 double 路径，不强迫普通尺度使用全部 1024 位。

定点结果若落入逃逸阈值保护区，会以配置精度逐像素复核；回退数进入 `RenderDiagnostics` 和状态文本，
但不写入作品。

## 5. P04–P05：热路径与有界并行

每帧预计算左上角和像素步长；每个连续行块从绝对 Y 锚点开始，行内 X 只做定点加法。`BinaryFixedPoint`
预存小数位、乘法舍入量和逃逸阈值，热循环直接处理同精度 `BigInteger` raw 值。

行块使用 `Parallel.ForEach`，默认最大并行度为 `max(1, min(ProcessorCount - 1, 8))`。每块只写自己的最终数组
区间，调度顺序不参与结果。取消在分块、行、固定像素间隔和固定迭代间隔检查；异常由并行循环统一传播。

## 6. P06：渐进预览

指针输入先更新 `TransientPreviewTransform`，View 立即对上一张 Bitmap 做平移或缩放。该结构位于 Features
运行态，不属于 `ArtworkDefinition`；保存、撤销、导出和指纹只能看到真实作品快照与真实渲染结果。

连续输入合并后先提交低成本真实预览。用户开启精细预览时，同一 generation 稳定 160 ms 后再提交较高质量帧。
两次提交都经过取消、关闭与 generation 检查；真实帧提交后暂态变换归零。

## 7. P07：扰动算法决策

中心参考轨道由权威二进制定点计算。像素差值使用“double 尾数 + 独立二进制指数”的小型扩展浮点递推，
因此 `1e-1000` 不会先下溢成零。非有限、溢出和逃逸阈值保护区样本逐像素回退权威内核，并记录 glitch 数。

逐像素对照、极深尺度和基准结果通过后，Automatic 只在任意精度 `Draft` 上选择扰动内核。`Final` 导出与
显式 `ReferenceArbitrary` 始终使用完整定点内核；这是本轮保守的产品边界。
