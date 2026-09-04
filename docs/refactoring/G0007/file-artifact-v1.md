# File Artifact v1 与所有权

```json
{
  "contract": "myavalonia.workflow.file-artifact",
  "version": 1,
  "producerPluginId": "myavalonia.plugin.fractal.art",
  "producerOperationId": "00000000-0000-0000-0000-000000000000",
  "lifetime": "run",
  "path": "C:\\...\\source.png",
  "mediaType": "image/png",
  "byteLength": 123456,
  "sha256": "64位大写十六进制摘要"
}
```

目录为 `%TEMP%\MyAvaloniaManagement\WorkflowArtifacts\<ProducerPluginId>\<OperationId>\`，仅允许
`.owner.json`、`source.png` 和原子写入临时文件。marker 记录契约、版本、生产者、OperationId 和 UTC 创建时间。

- `transient`：Art 内协调器在 Run Dispose 后于 `finally` 释放；
- `run`：Studio 必须调用 Fractal Release Action；
- `persistent`：ImageLab 最终输出，Fractal 不自动删除。

删除时依据生产者与 OperationId 重建目录，不把传入 path 当删除根。目录不存在是幂等成功；所有权错误、
路径越界和重解析点会拒绝；普通占用返回延迟清理警告。24 小时 TTL 只清理带有效 Fractal marker 的目录，
在插件加载和新建 Artifact 前执行，不使用后台定时器。
