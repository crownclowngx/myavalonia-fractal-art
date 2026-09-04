# G0008 实施说明

## SOLID 落点

| 原则 | 实际实现 |
| --- | --- |
| 单一职责 | `ArtworkLayerEditor` 只编辑不可变树并检查引用；Document 只编排命令、历史、选择和异步预览 |
| 开闭原则 | 图层、效果基类允许追加显式版本化类型；未知类型通过占位保留，不靠反射注册 |
| 里氏替换 | 合成管线只依赖标量遮罩、变换、混合和效果端口，测试替身遵循同一取消/返回契约 |
| 接口隔离 | 结构验证与可渲染性分离；Mask 转换、栅格变换、合成、Master Effects 均为窄接口 |
| 依赖倒置 | Document 和导出器依赖应用端口；Avalonia View 不直接修改领域树或执行像素算法 |

没有增加事件总线、仓储、通用命令框架、反射节点扫描或自制 DI。图层操作直接返回新的
`ArtworkDefinition`，由已有 100 项快照历史把一次用户动作作为原子提交。

## 领域与持久化

- `ArtworkDefinition.CurrentFormatVersion` 升为 7；
- `FractalLayerDefinition`、`LayerGroupDefinition`、`LayerTransformDefinition`、`ScalarMaskDefinition`、
  `ToneEffectDefinition`、`BloomEffectDefinition` 及不可用占位均为不可变值；
- v7 DTO 完整保存层序、组、名称、显隐、变换、混合、Mask、逐层探索及 Master Effects；
- v1–v6 先按原格式完整解码和验证，v6 损坏图不能借迁移绕过验证，然后折叠为默认单层树；
- Workflow Recipe 仍是外层 v1，`artworkSchemaVersion` 仍指 Document Content schema v1，内嵌作品正文为 v7；
- Plugin ID、Document ID、Render/Release Action ID 和 File Artifact v1 未修改。

## Document 与 UI

左侧改为图层面板，提供四类分形层、分组、选择、重命名、显隐、同级排序、移入组、移出组和删除。
删除最后一个分形层、非空组或仍被 Mask 引用的层会被阻止；引用错误列出全部目标层名称。

右侧合成属性编辑当前选择的层或组。生成器参数、预设、视口平移/缩放、艺术参数与九宫格仍复用既有界面，
但所有兼容属性都路由到当前选中分形层；每层分别保存候选、收藏、锁定与轮次。Master Effects 是作品状态，
进入 Dirty、Undo/Redo、保存、预览和普通 PNG。G0007 Blur/Bloom/Grain 仍是独立会话级 ImageLab 导出参数。

## 预算与诊断

- 1–8 个已知分形层，最多 4 个顶层组，禁止嵌套组；
- 最终画布像素 × 实际需计算分形层数不超过 64 Mi 像素工作量；
- Bloom 仅允许在最终画布不超过 16,777,216 像素时启用；
- 变换、Mask、Tone、Bloom 参数均在领域边界检查有限数与固定范围；
- 重复 ID、悬空/自引用/非标量 Mask、未知版本、非法效果顺序和超预算都返回中文诊断。

## 本地验证

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
```

Standalone 烟雾另行确认窗口句柄非零、进程响应正常且标题为 `Fractal Art · Standalone Preview`。
不运行 Windows CI、Release/部署/ZIP/签名/发布门禁。
