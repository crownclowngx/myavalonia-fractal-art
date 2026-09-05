# G0012 实施结果

> 2026-09-05：代码、专用文档及本地 Debug 门禁完成；真实 Host、正式 ZIP、干净部署与发布验收待发布阶段执行。

## 已完成

- 新增 `export-artwork-batch`，1–16 项固定作品配方顺序渲染，返回有序 run File Artifact v1；复用既有 Render/Release。
- 全批预检、4/16 MiB 实际读取预算、4096 单边与 67,108,864 批总像素预算，以及既有领域预算。
- 独立产物 GUID、InvocationId/itemId 关联 marker、最后提交检查、逐项尽力回滚与延迟 TTL 恢复。
- 单张 Render 迟到取消收口、所有权路径保护、marker 最后删除及旧 marker 兼容。
- SOLID 窄端口分工、中文设计注释、Studio ForEach 示例及四份专用文档。

## 本地证据

Debug 全解决方案构建 **0 警告、0 错误**；**248 项测试通过，0 失败、0 跳过**，其中新增 G0012 覆盖 57 项。
既有 191 项领域、Document、迁移、注册、Workflow 和像素指纹回归全部通过。格式验证通过。
实际目录 junction 测试确认 Release 拒绝重解析点且不删除链接目标；不需要符号链接特权。

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug --no-restore -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build --no-restore --logger 'trx;LogFileName=g0012.trx' --results-directory artifacts/test-results/G0012
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
dotnet run --project tools/FractalArtPlugin.Benchmarks -c Debug --no-build -- artifacts/test-results/G0012/benchmark-results.json
```

本机 Debug 基准结果：

| 代表场景 | 输出指纹 | P95 |
| --- | --- | --- |
| Escape Time | `d87b87b6a5692be0` | 16.04 ms |
| Recursive Path | `172209e1d5e70d9f` | 8.14 ms |
| Attractor Density | `72ae35df43a1a025` | 32.09 ms |
| Multi-layer Effects | `4113a369e04af8bc` | 41.93 ms |

四组指纹与 G0011 相同，进程峰值工作集约 **51.8 MiB**；1024 位取消响应 P95 **2.58 ms**。
这些数据来自现有渲染代表负载，作为本机趋势证据，不代表最大批次资源消耗，也不是跨机器时间门禁。
真实批次像素一致性及预算边界由专项自动化验证。

Standalone 烟雾确认进程响应、主窗口句柄 `1312552` 非零、标题为 `Fractal Art · Standalone Preview`；
验证后已关闭测试进程。未进行不同 DPI 或完整人工视觉走查。
原始 TRX、基准 JSON 和烟雾 JSON 存于本机忽略目录 `artifacts/test-results/G0012/`，可通过上述命令重新生成。

## 待发布阶段验收

- 真实 ZIP 与候选 Host 中的目录刷新、Studio v2 ForEach、授权、进度、取消、关闭排空及 ALC 卸载。
- Host 对双角色自有 Action 过滤、自调用/Handler 嵌套调用拒绝，以及真实调用 Scope 的创建释放。
- 跨插件文件消费者中断后的恢复、正式部署/升级、签名及发布门禁。

本轮未使用 AIFLOW，未增加 Windows CI，未执行 Release、正式 ZIP、部署、安装、签名或发布验收。
本地 Scope 与协议测试不替代真实 Host 联调；G0013 的 ImageLab 批量编排尚未实施。
