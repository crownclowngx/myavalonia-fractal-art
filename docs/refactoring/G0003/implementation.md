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

- 双精度草稿预览按原宽高比限制到最长边 480，精细预览限制到 960；
- 任意精度预览按 128、256、512 和 1024 位分层收紧像素预算，避免极深尺度阻塞 UI；
- 最终导出严格使用作品画布宽高和 `RenderQuality.Final`；
- 两条路径共享同一个生成器与渐变器，不维护第二套视觉算法。

## 高精度与兼容策略

作品中的中心坐标、尺度和 Julia 常量以规范化十进制字符串保存，避免 JSON 数字先经过 `double` 就丢失信息。
`ArbitraryDecimal` 只负责输入、比较、舍入以及视口几何；Julia 热循环使用基于 `BigInteger` 的二进制定点数，
把每次乘法立即舍入回固定小数位，因此中间整数不会随迭代无界增长。

- 默认 96 位有效数字，可在 32–1024 位之间配置；
- 普通尺度使用 `double` 快速路径，尺度进入约 `1e-12` 后自动切换任意精度，也允许手动强制；
- 输入长度、指数、中心/常量范围、尺度和精度均在领域边界校验；
- 作品格式升级为 v2；v1 的 IEEE 754 数值使用往返格式显式转换，未知版本仍拒绝加载。

这里的“任意精度”表示用户可在受控范围内选择精度，并不表示无限资源或数学证明级的误差界。

## 画布导航与历史

View 只采集指针坐标和滚轮增量，复平面换算集中在领域层的 `HighPrecisionViewport`。平移使用画布高度统一
X/Y 像素尺度；缩放采用精确十进制步长 `0.8`/`1.25`，缩放前后重新计算中心，使指针下的复平面位置
保持不变。拖动期间实时更新预览，但只在释放指针时把起点写入历史，从而让一次手势对应一次撤销。

## 取消和最新提交

Document 每次请求都会取消并释放上一个 CTS，同时推进 generation。算法按行检查取消，渐变按块检查取消；
Document 在计算后和 Bitmap 创建后分别验证 token、generation、关闭状态。即使第三方实现忽略取消，迟到值
仍会被丢弃。

## PNG 与文件事务

编码器生成标准无隔行 RGBA8888 PNG，逐行使用过滤类型 0，并写入 IHDR/IDAT/IEND 及 CRC32。写入器先在
目标同目录创建唯一临时文件，刷新成功且取消令牌仍有效后才原子替换目标；失败和取消在 finally 清理临时文件。
