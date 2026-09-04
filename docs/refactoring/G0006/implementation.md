# G0006 实施说明

## 领域与应用结构

- `ArtworkGraphDefinition` 保存版本化节点、连接和输出身份，`ArtworkGraphFactory` 为四种生成器产生唯一规范图；
- `ArtworkGraphValidator` 负责全部静态诊断和稳定拓扑排序，`ArtworkValidator` 在渲染及保存边界复用它；
- 原 `IArtworkGeneratorRenderer` 整链策略被拆为生成、着色/描边、效果、合成和输出节点执行器；
- 现有 Julia/Mandelbrot 内核、L-System 展开/Turtle、递归树、渐变和描边实现没有复制或改写；
- `IArtworkRenderPipeline` 返回 `ArtworkRenderResult`，其中执行摘要让预览、变体和测试准确判断节点命中。

## 缓存与生命周期

无状态算法、图验证器和快照编解码器保持 Singleton；`IArtworkGraphCache`、图执行器、渲染管线、变体探索器和导出器
改为 Scoped。Host 或 Standalone 为每个 Document 创建 Scope 时，会自然得到独立缓存；Scope 释放后缓存清空所有引用。

九宫格仍限制最多三路并发，但不再持有 64 项独立图片字典。候选渲染通过同一个节点缓存，只有全部可缓存计算节点命中时
`RenderedVariation.FromCache` 才为真。

## v6 保存与迁移

Document 内容 schema 仍为 1，作品内部格式升为 6。v6 追加：

- `graph.version/nodes/connections/outputNodeId`；
- `effects.version/effects`，当前 `effects` 必须为空。

v1–v5 先按原规则迁移生成器、参数、候选和收藏，再按最终 `GeneratorKind` 创建规范图及空效果链。v6 缺字段、未知图/节点
版本、未知操作、循环或未知效果都会整体失败；解码和验证完成前不会发布半个 Document 状态。

生成器切换、应用预设和采用候选均调用 `WithGeneratorKind` 或 `ApplyVariationRecipe`，同时替换生成器身份与规范图。

## 视觉兼容

`ImageSurface` 替换名称和可写数组暴露，但 PNG、渐变和描边的像素计算保持不变。测试固定 Julia、Mandelbrot、递归树和
L-System 四类 96×96 代表输出；原有五个经典 L-System 指纹以及高精度 Release 基准继续保留。

## 本地验证命令

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build
dotnet build FractalArtPlugin.slnx -c Release -warnaserror
dotnet test FractalArtPlugin.slnx -c Release --no-build
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
```

另执行 Standalone 启动烟雾，必须确认进程响应、主窗口句柄非零且标题为
`Fractal Art · Standalone Preview`。真实 Host、不同 DPI 和旧文件人工视觉比较单独保留为待验收项。
