# G0012 批量 Workflow Provider 设计

## 设计思路与 SOLID

批量能力增加的是一个受预算约束的应用用例，不另建渲染引擎，也不把 Document 或原始像素传进 JSON。

| 原则 | 实现落点 |
| --- | --- |
| 单一职责 | Handler 解析与序列化；BatchExporter 预检与执行；RecipeReader 限流读取；ArtifactStore 创建、标记和清理 |
| 开闭原则 | 新增 Action 和应用用例，复用既有生成器、导出计划、PNG 与 Release |
| 里氏替换 | 端口替身遵守取消与所有权契约；应用层也防御底层迟到返回，并立即登记产物以便回滚 |
| 接口隔离 | 带字节预算的读取端口独立于 UI 配方保存；批次端口不暴露 Gateway/Document |
| 依赖倒置 | 应用层依赖读取、导出计划、Artifact 端口；SDK 和 JSON 保留在 Infrastructure 边界 |

`WorkflowBatchArtifacts` 是本用例专用的暂存所有权对象：`Commit` 前释放即回滚。
它解决“渲染已经返回，但序列化或最终取消检查失败”的具体问题，不提供通用事务框架。
BatchExporter 和 ArtifactStore 为 Scoped，缓存继承同一个 Action Scope 的 128 MiB/256 项 LRU。

## Action 契约

稳定 ID：`myavalonia.plugin.fractal.art.workflow.export-artwork-batch`。
风险为 `ReadsLocalFiles | WritesLocalFiles | LongRunning`，最低确认频率 `OncePerRun`。
确认缓存、可信 Caller/Invocation、Run 和超时由 Host 负责；Handler 不拥有 RunId，也不能提交授权结果。
回滚和 TTL 只回收 Provider 自己生成的临时文件；显式 Release 仍声明删除风险与 `EveryInvocation`。

输入示例（替换为已经通过 Fractal Document 导出的真实 Workflow 配方路径）：

```json
{
  "items": [
    { "itemId": "flowers", "recipePath": "D:\\Art\\flowers.json" },
    { "itemId": "branches", "recipePath": "D:\\Art\\branches.json" }
  ]
}
```

所有对象拒绝额外、重复或缺失字段。数组长度 1–16；`itemId` 区分大小写，非空白，最多 64 个 Unicode 字符，批内唯一。
`recipePath` 为长度不超过 32767 的绝对路径；允许多项读取同一文件，形成不同操作身份。
输入 JSON 还受 Host 256 KiB 总预算约束，不能通过把调用者身份藏进参数绕过治理。

输出为 `results` 数组，每项必有 `itemId`、`artifact`、`image`；数量与输入相同、顺序对应。
`image` 只有 `width`、`height`；`artifact` 完整沿用 [File Artifact v1](../G0007/file-artifact-v1.md)，
包含 contract/version、producerPluginId、producerOperationId、lifetime、path、mediaType、byteLength、sha256。
其中 lifetime 固定 `run`，mediaType 固定 `image/png`，摘要为 64 位大写 SHA-256，总输出 JSON 不超过 1 MiB。
不存在像素数组、ViewModel、CLR 业务 DTO 或伪造的 Host Artifact 句柄。

单张 Render/Release 的描述符保持兼容。新增 Action 会改变 Studio 的整个 Catalog contract revision，
所以已有定义仍需在当前目录下刷新并重新验证；这不属于旧 Action Schema 的破坏性变更。

## 预检、预算与执行

| 边界 | 规则 |
| --- | --- |
| 文件输入 | 单文件实际读取最多 4 MiB；整批实际读取最多 16 MiB；越界至多多读一个探测字节 |
| 作品输入 | Workflow Recipe v1 包装现有 Artwork v1–v8 迁移，不改变快照格式 |
| 尺寸 | 既有领域要求至少 64×64；Workflow 单边最多 4096、单图最多 16,777,216 像素 |
| 整批像素 | 最多 67,108,864，不使用批次并行放大峰值 |
| 领域资源 | 继续执行图层工作量、点数、L-System、Bloom、精度与可渲染性检查 |
| PNG | 最终质量、配方画布及背景、sRGB/straight Alpha；单文件协议上限 256 MiB |
| 时间/缓存 | Host 的 LongRunning 超时与取消令牌；Action Scope 有界缓存，随 Scope 释放 |

顺序为：校验全部参数 → 顺序读取固定配方快照 → 全部预检 → 顺序渲染并登记所有权 → 序列化 → 最后取消检查 → 提交。
不随机改变 Seed、尺寸或配方，不写回输入文件；允许的同路径重复项分别读取一次，预算按实际读取累计。
进度 `validating` 为 0–19，`rendering` 为 20–94，`committing` 为 95，`succeeded` 为 100；消息只给序号，不带本地路径。
内部阶段进度不承诺逐像素百分比，Host 可限流。100% 通知不是持久成功凭据，Consumer 应以 Host 返回终态为准。

## 所有权、取消与恢复

批项使用独立操作 GUID 保持旧 Release 的目录语义；marker 新增可选 `invocationId`、`itemId`，旧 marker 仍可读取。
它们只辅助关联审计，不替代 Host 授权；`run` 是 File Artifact v1 的清理约定，并不意味着 Host 自动删除文件。

暂存对象在 Handler 最后提交前负责整个批次。失败或取消逆序尝试释放已登记的每张图，不使用原取消令牌；
单项异常不阻止其它项，也不覆盖原始故障。文件创建阶段由 Store 负责清理尚未返回的当前产物。
清理延迟只记录非敏感操作 GUID；有效 marker 保留给 24 小时 TTL，TTL 在插件加载和新产物创建时执行，无后台计时器。

先检查已存在的各级父目录、操作目录及文件的重解析点，再按生产者与操作 GUID 重建删除根；
不将传入 path 用作删除根。未知文件或子目录阻止清理，普通文件占用返回 `cleanup_deferred`，marker 最后删除。
重复操作目录拒绝覆盖；marker 使用 `CreateNew` 防止并发身份碰撞后的误删。

这是应用层的整批成功语义，不是文件系统多文件原子事务。磁盘故障、进程崩溃、取消恰好发生在最后检查之后，
或 Host 在 Handler 返回后拒绝结果，都无法由当前 SDK 提供提交确认回调；此时未交付文件由有效 marker 的 TTL 恢复。
损坏/缺失 marker 不满足清理所有权证明，不进行猜测性删除。真实 Host 的最终终态和关闭排空仍需发布阶段联调。

## Studio Definition v2 示例

下面仅是可以放入 Definition v2 的 `steps` 片段，不是带有效 revision 的完整导入文件。
在真实 Host 加载 Fractal 和 Studio 后，通过 Studio 刷新当前 Action 目录并创建/导出 v2 定义，
使用其中真实 `contractRevision`、`presentationRevision`；不要复制占位 SHA 或用旧目录摘要。

```json
[
  {
    "id": "render-batch",
    "actionId": "myavalonia.plugin.fractal.art.workflow.export-artwork-batch",
    "arguments": {
      "items": [{ "itemId": "flowers", "recipePath": "D:\\Art\\flowers.json" }]
    }
  },
  {
    "id": "release-images",
    "forEach": "${render-batch.result.results}",
    "actionId": "myavalonia.plugin.fractal.art.workflow.release-artifact",
    "arguments": { "artifact": "${item.artifact}" }
  }
]
```

该片段演示生成和逐项释放，会删除示例临时图。实际处理流程应在 Release 前加入对应 Consumer 步骤。
Studio 中途失败不会自动执行后续 Release；尚未释放的文件依靠恢复性清理。跨 ImageLab 产品化编排属于 G0013。
