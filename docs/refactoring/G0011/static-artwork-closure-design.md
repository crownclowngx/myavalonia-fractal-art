# G0011 静态作品闭环设计

## 设计链路

```text
ArtworkDefinition v8
├─ CompatibilityService → Compatible / Blocked issues → 显式、可撤销移除
├─ RenderContext          → Preview / Thumbnail / Final
└─ ExportPlanner          → 临时尺寸与背景 → 已验证 ExportPlan
                                      ↓
                         RenderPipeline → RGBA8888 → PNG → AtomicFileWriter
```

Document 只编排以上端口和会话状态；View 只绑定状态、参数和命令。导出器消费已经验证并捕获作品修订的
`ArtworkExportPlan`，不再推断 UI 参数。该拆分让尺寸预算、文件选择和像素生成不会再次堆入 Document。

## 工作区状态

- `Loading`：没有可用预览时说明正在生成或已取消，可重试但不伪装失败；
- `Ready`：最近一次真实帧已按当前 generation 提交；
- `Blocked`：作品结构合法，但包含当前运行环境无法解释的图层或效果；
- `Failed`：当前代次渲染异常；若已有成功画面则保留画面并显示错误横幅。

快速开始只在新建 Document 中默认展开。首次真实编辑、应用预设或手动关闭后收起；恢复作品不显示。
状态和引导都不参与快照、Dirty、历史、缓存键或导出。

## 缺失能力策略

解码器继续把未知图层/效果保存为不透明 JSON；兼容服务只建立用户可读报告。只要报告非空，所有像素输出
在昂贵计算前失败，避免“跳过一个效果但仍提示成功”。用户点击某一项“明确移除”后，服务按图层 ID 或
当前效果问题键替换不可变作品，再经过完整领域验证并进入普通撤销栈。其它未知 payload 不受影响。

## PNG、Alpha 与尺寸

导出请求包含宽、高和透明背景。Planner 只在临时作品快照上替换 Canvas，随后复用现有 64–8192 边长、
64M 总像素、64M 多层工作量以及吸引子/Bloom 16,777,216 像素预算。透明导出只令画布背景 Alpha 为零；
图层与效果 Alpha 不变。编码时再把 Alpha=0 的隐藏 RGB 归零，避免后续缩放产生脏边。

PNG 固定为非隔行、8 位、color type 6 的 straight RGBA，并写入 `sRGB` perceptual intent 与
`gAMA=45455`。预览 Bitmap、九宫格缩略图和最终文件都通过同一编码器解释颜色。

## 缩略图与资源预算

`RenderContext.ForThumbnail` 直接从真实作品计算最大边 240 的草稿上下文，不再改写 Canvas。九宫格仍保持
外层最多三路并发、单图单线程，并复用相同的图层、遮罩、效果和生成器管线。性能工具分别测量逃逸时间、
递归路径、吸引子密度和多图层效果，记录耗时、分配、工作集与指纹；时间数据只作本地趋势证据。

## SOLID 落点

| 原则 | 落点 |
| --- | --- |
| 单一职责 | 兼容报告、导出规划、渲染、PNG、原子文件、会话状态和 View 分离 |
| 开闭原则 | 新输出策略通过导出请求/计划扩展，不修改数学内核或快照格式 |
| 里氏替换 | 测试替身和生产导出器遵守同一计划、取消与失败契约 |
| 接口隔离 | Compatibility、Planner、Exporter 和 Dialog 只暴露各自所需操作 |
| 依赖倒置 | Document 依赖窄端口，文件系统、编码器和窗口仍由基础设施适配 |
