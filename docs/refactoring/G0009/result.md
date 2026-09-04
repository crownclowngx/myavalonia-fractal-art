# G0009 实施结果

## 已完成

- Clifford 与 De Jong 两种奇异吸引子；
- 不可变 PointCloud、32 条确定性逻辑轨道及 1–8 路有界并行；
- 8 位定点双线性整数密度、曝光/Gamma、透明密度渐变和当前层局部发光；
- 吸引子图层、密度遮罩、四个预设、变体、缓存、撤销/重做、保存与导出链路；
- v8 快照及 v1–v7 显式迁移；
- 详细中文设计注释与 G0009 专用文档。

## 自动化证据

本地 Debug 自动化共 165 项通过，包含公式、Seed、不同并发度逐点/逐像素一致、退化取景、密度、透明
Alpha、局部发光、缓存失效、密度遮罩、资源预算、v8 往返、v7 迁移、变体、Document 历史与取消。
既有 149 项回归全部继续通过。

门禁命令：

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
```

Standalone 启动烟雾已通过：进程可响应、主窗口句柄非零，标题精确为
`Fractal Art · Standalone Preview`；验证结束后已关闭该测试进程。

## 尚待人工验收

- 真实 Host 中 Dock、保存信封、关闭确认、ALC 卸载和 Workflow 执行；
- 四个预设、透明叠加、密度遮罩与高采样最终输出的人工视觉验收。

本轮未使用 AIFLOW，未增加 Windows CI，未执行 Release、正式 ZIP、部署、签名、安装或发布门禁。
