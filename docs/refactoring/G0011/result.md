# G0011 实施结果

> 状态：代码、专用文档和本地 Debug 自动化门禁完成；真实 Host、正式 ZIP、干净部署与发布验收待发布阶段执行

## 已完成

- Loading/Ready/Blocked/Failed 工作区、新建会话快速开始、失败重试和最后成功预览保留；
- 缺失图层/效果无损报告、像素输出阻止、逐项显式移除以及撤销/重做；
- 会话态自定义导出尺寸、等比锁定、透明画布背景、领域预算预检和最终质量原子导出；
- RGBA8888 straight Alpha、全透明隐藏 RGB 清理、sRGB 与 gAMA 元数据；
- 同生产管线的 240 最大边缩略图、三路批并发与单图单线程；
- Content schema 1、Artwork v8、renderer v1、稳定身份、SDK 区间和 `1.0.0` 兼容政策冻结；
- v1–v8 固定迁移夹具、详细中文设计注释及 G0011 专用文档。

## 自动化与性能证据

本地 Debug 自动化共 191 项通过；新增覆盖导出计划与预算、4K/透明输出、PNG 块和真实像素、缩略图上下文、
新手引导、工作区失败/重试、缺失能力显式修复/撤销以及 v1–v8 固定夹具。既有 179 项领域、Document、
Workflow、注册、缓存、迁移、数学透镜和 RGBA 指纹回归继续通过。

Debug 性能证据在本机 320px 代表负载中得到稳定指纹：escape-time `d87b87b6a5692be0`、recursive-path
`172209e1d5e70d9f`、attractor-density `72ae35df43a1a025`、multi-layer-effects `4113a369e04af8bc`；
四者本轮 P95 分别约 16.16ms、8.09ms、31.53ms、41.70ms，进程峰值工作集约 50.1MiB。1024 位取消响应
P95 约 2.45ms。这些数字是当前机器的趋势证据，不是跨机器时间门禁。

门禁命令：

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
dotnet run --project tools/FractalArtPlugin.Benchmarks -c Debug -- <output.json>
```

Standalone Debug 烟雾确认进程响应、主窗口句柄非零且标题为 `Fractal Art · Standalone Preview`；基础创作、
导出会话态和透明像素链路由同一 Document/生产管线自动化覆盖。验证后已关闭测试进程。

## 尚待发布阶段验收

- 真实 Host 的 Document Scope、Dock、保存信封、关闭确认和 ALC 卸载；
- 正式 ZIP、干净安装/升级、签名、部署以及发布候选验收；
- 不同 DPI、透明 PNG 外部查看器和完整人工视觉走查。

本轮未使用 AIFLOW，未增加 Windows CI，未执行 Release、正式 ZIP、部署、签名、安装或发布门禁。
