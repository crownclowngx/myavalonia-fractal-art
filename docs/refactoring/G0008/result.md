# G0008 实施与测试结果

> 状态：代码、v7 迁移、专用文档和本地 Debug 自动化门禁完成；真实 Host 与人工视觉验收待完成

## 已完成

- v7 非破坏性图层树、8 分形层/4 单层组预算及稳定层 ID；
- 五种混合、完整二维变换、预乘 Alpha 双线性插值及画布空间 ScalarField Mask；
- 隐藏未引用分支跳过、隐藏 Mask 源最小求值和逐层节点缓存复用；
- 固定 Tone → Bloom Master Effects 及其 Dirty、历史、保存、预览和 PNG 语义；
- 图层面板、当前层/组属性、增删改序、分组/移出、引用删除保护；
- 当前层生成参数、预设、视口和九宫格探索路由；
- v1–v6 单层迁移、v7 完整往返、未知能力原样保留及统一输出阻断；
- Workflow Recipe 外层 v1、Render/Release Action 和 File Artifact v1 兼容。

## 本地自动化结果

- `dotnet restore`：通过；
- Debug `dotnet build -warnaserror`：通过，0 警告、0 错误；
- Debug 全量测试：`149 passed, 0 failed, 0 skipped`；
- `dotnet format --verify-no-changes --no-restore`：通过；
- Standalone 烟雾：进程响应正常，主窗口句柄非零，标题为 `Fractal Art · Standalone Preview`。

新增测试覆盖混合与 Alpha 边界、透明路径、逆变换、Mask 阈值/柔化/反相、层序与组序、隐藏分支、
隐藏 Mask 源、局部缓存失效、Master Effects 固定指纹、图层编辑保护、结构/像素/Bloom 预算、Document
参数路由与历史、v7 完整往返以及未知能力阻断。既有缓存并发、Document Scope 隔离、取消/异常不入缓存、
迟到结果不提交、v1–v6 迁移、Workflow/ImageLab/Release 和四类旧 RGBA 指纹测试继续通过。

## 尚待人工验收

- 在 Standalone 人工观察图层选择、显隐、排序、入组/出组和右侧属性切换；
- 人工比较五种混合、旋转锚点、缩放边缘、Mask 对齐、柔化/反相以及 Tone/Bloom 视觉；
- 在真实 Host 验证多 Document Scope、保存/关闭/恢复、Dirty/关闭确认、PNG、ImageLab 导出和 Workflow Render；
- 用真实历史 v1–v6 文件逐一打开并人工比较迁移前后画面。

本阶段没有使用 AIFLOW，没有创建或修改 `.aiflow`，没有增加 Windows CI，也没有执行 Release 构建、部署、
正式 ZIP、签名、发布或其他发布门禁。
