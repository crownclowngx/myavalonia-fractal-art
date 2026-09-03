# G0003 实施方案

## 数据流

```text
ArtworkDefinition 快照
    → RenderContext
    → IJuliaFieldGenerator
    → ScalarField(values + escaped)
    → IGradientMapper
    → RgbaImage
    ├─ IPreviewImageFactory → Avalonia Bitmap
    └─ IPngEncoder → IAtomicFileWriter → PNG
```

领域与应用层不依赖 Avalonia。只有预览工厂把 RGBA 适配为 Bitmap，文件选择器只通过 SDK 的
`IPluginWindowInteraction` 获取路径。

## Julia 与颜色

生成器对每个像素迭代 `z = z² + c`，逃逸半径为 2。逃逸点使用平滑迭代值并归一化到 0..1，未逃逸点把
`escaped=false` 单独保存。渐变器因此能让内部点使用明确 Interior 色，而不是与某个数值端点碰撞。

## 质量策略

- 草稿预览按原宽高比限制到最长边 480；精细预览限制到 960；
- 最终导出严格使用作品画布宽高和 `RenderQuality.Final`；
- 两条路径共享同一个生成器与渐变器，不维护第二套视觉算法。

## 取消和最新提交

Document 每次请求都会取消并释放上一个 CTS，同时推进 generation。算法按行检查取消，渐变按块检查取消；
Document 在计算后和 Bitmap 创建后分别验证 token、generation、关闭状态。即使第三方实现忽略取消，迟到值
仍会被丢弃。

## PNG 与文件事务

编码器生成标准无隔行 RGBA8888 PNG，逐行使用过滤类型 0，并写入 IHDR/IDAT/IEND 及 CRC32。写入器先在
目标同目录创建唯一临时文件，刷新成功且取消令牌仍有效后才原子替换目标；失败和取消在 finally 清理临时文件。
