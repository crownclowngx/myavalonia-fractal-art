# G0002 实施方案

## 单一事实来源

`ArtworkDefinition` 是不可变聚合，包含 `CanvasDefinition`、`JuliaDefinition`、`GradientDefinition` 和
`ArtworkPresentationDefinition`。属性修改通过 `with` 创建候选，候选先经过 `IArtworkValidator`，成功后才
整体替换。后台渲染只捕获不可变值，不读取变化中的 UI 属性。

## 双层版本与严格恢复

```text
Host DocumentContent.SchemaVersion = 1
    → Fractal Art payload.formatVersion = 1
        → 完整 DTO
        → 领域验证
        → 一次性提交到 Document
```

外层 schema 管插件内容信封，内层 formatVersion 管作品语义。DTO 使用可空成员检测缺字段；颜色必须是
`#RRGGBB` 或 `#RRGGBBAA`。未知版本不降级为默认作品。未来确有旧版本时，在 Codec 内增加显式迁移步骤。

## Dirty 与保存确认

每次成功内容修改推进 `_revision`。`CaptureSaveSnapshotAsync` 在同一同步观察区间捕获不可变作品和修订，
不写文件、不清 Dirty。Host 成功提交后调用 `AcceptChanges`；只有返回的修订仍等于当前修订才更新
`_acceptedRevision`。

## 撤销/重做

首版作品对象很小，使用容量 100 的不可变快照栈。该方案简单、可审计且没有过早引入命令图。历史属于
Document Scope；恢复成功后清空。每次撤销/重做仍产生新修订，确保 Host 不会把未保存操作误判为干净。

## 错误和取消

恢复先解码到局部候选，再验证，再检查取消，最后一次性替换。任何异常由 Host 观察并丢弃暂存 Scope；
当前对象不会出现“画布已恢复、渐变仍是默认值”的半状态。
