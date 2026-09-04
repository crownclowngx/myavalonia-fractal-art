# G0007：Fractal Art 双角色与 ImageLab 文件效果计划

## 产品结论

Fractal Art 同时是 Workflow Provider 与 Consumer：Document/Application Service 可以通过 caller-bound Gateway
调用 ImageLab；Render/Release Handler 则向 Workflow Studio 提供独立的文件式能力。大型图像全部走文件系统，
Workflow `JsonElement` 只传路径、摘要、参数和元数据。

两条路径复用同一个 ImageLab Action、File Artifact v1、效果参数和确定性规则：

```text
Art 内一键导出：Fractal Document → Coordinator → ImageLab Action
Studio 编排：Studio → Fractal Render → ImageLab Effects → Fractal Release
```

Workflow Studio 生产代码无需修改，因为 Definition v2 已能把 `${render.result.artifact}` 传给下一步。

## 范围

- Host 允许双角色，过滤自有 Action，并拒绝自调用和 Handler 嵌套调用；
- Art 右侧提供仅导出时应用的 Blur、Bloom、Grain 参数；
- Fractal 提供 Render/Release Action、配方 v1、Artifact Store 和 24 小时 TTL 清理；
- ImageLab 提供固定 Blur → Bloom → Grain 的文件式 Action；
- 不修改 Workflow 公共协议，不提供实时预览，不引入插件间共享程序集。

明确不做 AIFLOW、Windows CI、发布、签名、ZIP、NuGet 门禁或 Workflow Studio 生产改造。
