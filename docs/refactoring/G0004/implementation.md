# G0004 实施方案

## 设计思路

实现保持朴素的三段结构：

```text
ArtworkDefinition v3
    ├─ ArtisticParameterMapper：艺术值 ↔ Julia 真实参数
    ├─ VariationGenerator：Seed + 轮次 + 元数据 → 候选配方
    └─ ArtworkExplorationDefinition：设置、候选、收藏
                     ↓
VariationExplorer：最多 3 路缩略图渲染 + 64 项内存缓存 + 取消
                     ↓
FractalArtworkDocument：整批提交、历史、Dirty、Bitmap 适配和 UI 命令
```

没有引入通用策略注册表、事件总线或节点框架。当前只有一个 Julia 生成器，直接实现其参数映射比抽象一套尚无第二实现的
插件式变异框架更容易检查；变化边界仍由窄接口 `IArtisticParameterMapper`、`IVariationGenerator`、
`IVariationExplorer` 和 `IArtworkPresetCatalog` 隔离。

## 单一事实来源

“细节”映射到 64–1024 的 16 步进迭代数，“流动”和“卷曲”映射到复常量实部与虚部的 -1.2–1.2 范围。
UI getter 始终从 `JuliaDefinition` 反算，setter 立即写回 `JuliaDefinition`。v3 JSON 没有 `detail`、`flow` 或 `curl`
字段，因此数学输入、艺术滑杆、撤销、导出和变体不会产生状态分叉。

## 确定性变异

候选随机源使用代码内固定的 SplitMix64。初始状态只组合当前 Seed、持久化 Generation 和候选序号，避免依赖
`System.Random` 的运行时版本。构图变化使用当前高精度 Scale 计算中心偏移和比例变化；形态变化约束复常量与迭代数；
颜色变化逐通道限幅。每个候选生成后由 `ArtworkValidator` 的同一预算再次验证。
若源作品恰好位于数值边界，变异器会沿同一确定性随机序列最多重采样 8 次，仍无合法结果时回退原配方，
因此不会为了凑满九宫格把越界参数交给渲染器。

当前公式只有 Julia，因此公式天然锁定；当前没有效果链，因此效果锁在界面中明确显示为“尚未启用”，不会创建无真实语义的
假持久化字段。Seed、构图、形态和颜色是本阶段可以真实执行和验证的锁定分组。

## 整批提交、缓存和取消

`VariationExplorer` 不持有 Document，只返回候选配方和图像。缩略图固定最长边 240，使用 `SemaphoreSlim(3)` 限制并发；
缓存键包含完整渲染配方、尺寸和 renderer 版本，FIFO 保留最近 64 项。Document 只在 9 张缩略图全部成功且 generation 仍最新时
一次性写入作品与 UI。取消、异常和迟到结果都不会留下半批候选。

## v3 持久化

候选和收藏保存 `VariationRecipeDefinition`，只包含 Seed、Julia 和 Gradient；画布、探索状态和呈现状态不递归复制。
Bitmap、缓存命中、并发数和渲染诊断都不保存。v1 先迁移双精度参数，v2 补入明确的空探索状态，然后统一成为 v3。

## SOLID 对照

| 原则 | G0004 落点 |
| --- | --- |
| 单一职责 | 艺术映射、配方变异、候选渲染、预设目录、快照和 Document 编排分离 |
| 开闭原则 | 后续生成器可替换映射/变异实现，Document 不依赖具体随机算法 |
| 里氏替换 | 测试渲染器按同一取消契约验证有界并发与整批失败 |
| 接口隔离 | 变异器只生成配方；Explorer 只渲染候选；预览工厂只创建 Bitmap |
| 依赖倒置 | Document 依赖上述窄端口，生产实现由现有 DI 组合入口提供 |
