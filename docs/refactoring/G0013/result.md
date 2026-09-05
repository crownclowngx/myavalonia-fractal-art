# G0013 实施结果

> 状态：本地实施完成。证据时间：2026-09-05 19:45（UTC+08:00）。真实 Host 与发布验收另列待办。

## 已完成能力

Studio Document 内提供 Fractal → ImageLab 示例，支持 1–16 个已保存配方的排序、移除、效果参数和输出目录选择，
生成可验证、可导出的完整 Definition v2。单张复用原文件处理 Action，批量使用新增目录输出 Action 和两轮 ForEach。
真实目录的两种 revision、Schema 与风险声明参与验证；不兼容、缺少插件或契约过期会阻止运行。

会话恢复只绑定冻结的内置示例，保留经 Gateway 终态确认的 PNG、未完成项和独立清理状态。
有效源可续跑，无效源可按当前原路径配方重新生成；每次重试使用新名称。
不确定提交标为“需核对”，文件存在不等于成功；清理只调用 Fractal Release，released=false 不算已清理。
关闭立即取消并清空台账，迟到结果不会恢复记录；两个 Document 的状态隔离，普通工作流不保留完整输入输出。

ImageLab 补齐严格协议、实际有界读取、marker 身份、摘要、PNG 解码前尺寸检查、重解析点与排他提交边界。
结果序列化和进度通知在提交前完成，保留原 Action Schema 和效果算法。
烟雾测试另发现并修复 Standalone 注册适配器缺少 IWorkflowActionRegistration 导致的启动失败，已增加回归测试。

## 本地门禁实测

在 Fractal 仓库执行 `./tools/verify-g0013.ps1`，最终输出 `G0013_LOCAL_GATE_OK`。
脚本固定使用 Debug，逐步检查退出码，并生成独立日志、TRX 和 JSON 摘要。

| 检查 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Fractal 单元测试 | 248 | 0 | 0 |
| 独立跨插件集成测试 | 20 | 0 | 0 |
| ImageLab 全量单元测试 | 801 | 0 | 0 |
| Studio 全量单元测试 | 73 | 0 | 0 |
| 合计 | **1142** | **0** | **0** |

本阶段增加 68 项测试（ImageLab 29、Studio 19、集成 20）。三个解决方案的 locked restore、
Debug 构建（零警告、零错误）及 `dotnet format --verify-no-changes --no-restore` 全部通过。
ImageLab 原有格式差异仅统一空白，以满足全量格式门禁；既有用户工作区修改保留。

集成工程通过真实 Module 注册获取描述符和 Scoped Handler，执行真实 Fractal 渲染、PNG 文件交接、
ImageLab 后处理和 Release；覆盖单张、两项、16 项、失败恢复、取消、不确定提交及 Scope 释放。
生产程序集没有新增插件间引用，跨插件引用只在测试工程。
新面板完成真实 Avalonia 编译绑定、控件交互与布局测试，生成 `artifacts/test-results/G0013/studio-panel.png` 并检查渲染。

Studio 自检输出 `WORKFLOW_STUDIO_G3_SELF_TEST_OK invocations=4 disposedRuns=1`。
三个 Standalone 均成功创建可响应主窗口：Fractal 1075 ms、ImageLab 1436 ms、Studio 1888 ms。
此烟雾仅验证空白会话启动；完整跨插件路径由上述本地集成适配器测试覆盖。

## Fractal 性能与指纹

沿用既有 Debug 基准，320 像素宽场景实测如下；耗时是本机样本，不作为跨机器保证。

| 场景 | P95（ms） | 像素指纹 |
| --- | ---: | --- |
| escape-time-rgba | 16.5953 | d87b87b6a5692be0 |
| recursive-path-rgba | 8.4362 | 172209e1d5e70d9f |
| attractor-density-rgba | 32.9508 | 72ae35df43a1a025 |
| multi-layer-effects | 42.0929 | 4113a369e04af8bc |

四项既有静态指纹保持一致；这些场景观测到的峰值工作集为 54,276,096 字节。
取消响应最大样本为 2.2391 ms，五个样本均写入基准 JSON。

原始证据位于本地忽略目录 `artifacts/test-results/G0013/local-gate/`：
`summary.json`、四份独立 TRX、各步骤日志、`benchmark.json` 和 Standalone stdout/stderr。
摘要明确记录 `realHostValidated=false`、`releaseGatesExecuted=false`、`windowsCiAdded=false`。
这些机器相关产物不提交，复现命令及事实归档在本文与 [实施说明](implementation.md)。

## 仍需单独验收

- 真实 Host 授权/确认、Catalog 变更广播、ALC 卸载以及 Document 关闭排空。
- 原生文件窗口、不同 DPI/主题与真实 Host 内连续交互的人工验收。
- 正式 ZIP、安装/升级、签名、部署和发布验收；本轮未执行这些门禁，也未增加 Windows CI。

本地 SDK Gateway 适配器不能替代真实 Host 授权或部署验收。
不支持跨重启恢复、自动重试、Seed 覆盖或通用补偿框架；未释放源继续遵守既有 24 小时 TTL。
本轮没有使用 AIFLOW。
