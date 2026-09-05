using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FractalArtPlugin.Application.Workflow;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Infrastructure.Workflow;

/// <summary>
/// 批量动作的传输边界。沿用单张动作的 Artifact/image Schema，避免同一个 File Artifact v1
/// 在两条入口中出现不兼容字段；新增契约不修改旧描述符，所以旧工作流仍可使用原 Action。
/// </summary>
internal static class ExportArtworkBatchWorkflowAction
{
    internal static readonly WorkflowActionId Id = new("myavalonia.plugin.fractal.art.workflow.export-artwork-batch");

    internal static WorkflowActionDescriptor CreateDescriptor()
    {
        using var input = JsonDocument.Parse("""
            {"type":"object","properties":{"items":{"type":"array","minItems":1,"maxItems":16,
              "items":{"type":"object","properties":{
                "itemId":{"type":"string","minLength":1,"maxLength":64},
                "recipePath":{"type":"string","minLength":1,"maxLength":32767}},
                "required":["itemId","recipePath"],"additionalProperties":false}}},
              "required":["items"],"additionalProperties":false}
            """);
        var resultSchema = JsonNode.Parse(FractalWorkflowActions.CreateRenderDescriptor().OutputSchema.GetRawText())!;
        resultSchema["properties"]!["itemId"] = JsonNode.Parse("""{"type":"string","minLength":1,"maxLength":64}""");
        resultSchema["required"]!.AsArray().Add("itemId");
        var output = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["results"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = WorkflowBatchExporter.MaximumItems,
                    ["items"] = resultSchema
                }
            },
            ["required"] = new JsonArray("results"),
            ["additionalProperties"] = false
        };
        return new(Id, "批量渲染 Fractal 作品", "预检 1–16 个作品配方后顺序渲染；失败或取消回滚自有临时 PNG，成功后由调用方显式释放。",
            input.RootElement, JsonSerializer.SerializeToElement(output),
            WorkflowActionRiskFlags.ReadsLocalFiles | WorkflowActionRiskFlags.WritesLocalFiles | WorkflowActionRiskFlags.LongRunning,
            WorkflowActionConfirmationPolicy.OncePerRun);
    }

    /// <summary>即使测试或其它适配器绕过 Host，也不能把重复 JSON 字段静默解释成最后一个值。</summary>
    internal static IReadOnlyList<WorkflowBatchItem> Parse(JsonElement arguments)
    {
        if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 256 * 1024)
            throw new InvalidDataException("批次参数超过 Workflow 输入预算。");
        RequireProperties(arguments, "items");
        var items = arguments.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > WorkflowBatchExporter.MaximumItems)
            throw new InvalidDataException("批次必须包含 1–16 项作品。");
        var result = new List<WorkflowBatchItem>();
        foreach (var item in items.EnumerateArray())
        {
            RequireProperties(item, "itemId", "recipePath");
            if (item.GetProperty("itemId").ValueKind != JsonValueKind.String ||
                item.GetProperty("recipePath").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("批次项身份和配方路径必须是字符串。");
            result.Add(new(item.GetProperty("itemId").GetString()!, item.GetProperty("recipePath").GetString()!));
        }
        return result;
    }

    private static void RequireProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("批次参数必须是对象。");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!actual.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("批次参数包含未知或重复字段。");
        if (actual.Count != expected.Length) throw new InvalidDataException("批次参数缺少必填字段。");
    }
}

/// <summary>
/// Handler 仅桥接 Host 进度与 JSON。暂存所有权跨越序列化边界，最终检查成功后才提交；
/// 不创建 Run，不嵌套调用其它 Provider，也不持有可编辑 Document。
/// </summary>
internal sealed class ExportArtworkBatchWorkflowActionHandler(IWorkflowBatchExporter exporter) : IWorkflowActionHandler
{
    public async ValueTask<JsonElement> InvokeAsync(JsonElement arguments, WorkflowActionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var items = ExportArtworkBatchWorkflowAction.Parse(arguments);
        await using var pending = await exporter.ExportAsync(items, context.InvocationId,
            new ProgressAdapter(context.Progress), cancellationToken).ConfigureAwait(false);
        var output = JsonSerializer.SerializeToElement(new
        {
            results = pending.Results.Select(result => new
            {
                itemId = result.ItemId,
                artifact = ArtifactJson.ToObject(result.Artifact),
                image = new { width = result.Width, height = result.Height }
            }).ToArray()
        });
        if (Encoding.UTF8.GetByteCount(output.GetRawText()) > 1024 * 1024)
            throw new InvalidDataException("批次结果超过 Workflow 输出预算。");
        cancellationToken.ThrowIfCancellationRequested();
        context.Progress.Report(new("succeeded", 100, "批次 PNG Artifact 已全部生成。"));
        cancellationToken.ThrowIfCancellationRequested();
        pending.Commit();
        return output;
    }

    // 同步转发，避免 Progress<T> 把通知排到 UI 队列后导致阶段乱序或作用域结束后继续通知。
    private sealed class ProgressAdapter(IProgress<WorkflowActionProgress> target) : IProgress<WorkflowBatchProgress>
    {
        public void Report(WorkflowBatchProgress value) => target.Report(new(value.Stage, value.Percent, value.Message));
    }
}
