using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Features.Main;
using WorkflowStudio.Workflows;
using WorkflowStudio.Workflows.ArtWorkflow;
using Xunit;

namespace FractalArtPlugin.WorkflowIntegration.Tests;

public sealed class G0013IntegrationTests : IClassFixture<PlatformFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public async Task 完整定义往返真实PNG交接及ActionScope释放(int count)
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(count);
        var original = await File.ReadAllBytesAsync(h.RecipePath);
        var codec = new WorkflowDefinitionCodec();
        var definition = codec.Parse(codec.Serialize(plan.Definition));
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(h.Catalog.ContractRevision, definition.ContractRevision);
        Assert.Equal(count == 1 ? 0 : 2, definition.Steps.Count(s => s.ForEach is not null));
        var result = await h.Runner.RunAsync(definition, null, default);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1 + 2 * count, result.Entries.Count);
        Assert.All(h.Recovery.Items, item => { Assert.True(item.Succeeded, item.Processing); Assert.Equal("已释放", item.Cleanup); Assert.True(File.Exists(item.OutputPath)); });
        Assert.Equal(count, Directory.GetFiles(h.OutputRoot, "*.png").Length);
        Assert.Equal(original, await File.ReadAllBytesAsync(h.RecipePath));
        Assert.All(h.Gateway.Sources.Values, source => Assert.False(File.Exists(source.GetProperty("path").GetString())));
        Assert.Equal(h.Fractal.ScopesCreated, h.Fractal.ScopesDisposed);
        Assert.Equal(h.ImageLab.ScopesCreated, h.ImageLab.ScopesDisposed);
        Assert.Equal(h.Gateway.RunsCreated, h.Gateway.RunsDisposed);
        Assert.Empty(Directory.GetFiles(h.OutputRoot, "*.partial"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task 批次数越界不产生调用(int count)
    {
        await using var h = new IntegrationHarness();
        Assert.Throws<InvalidDataException>(() => h.Builder.Create(h.Catalog,
            Enumerable.Repeat(h.RecipePath, count).ToArray(), h.OutputRoot, new()));
        Assert.Empty(h.Gateway.Requests);
    }

    [Theory]
    [InlineData(ArtWorkflowDefinitionBuilder.Batch)]
    [InlineData(ArtWorkflowDefinitionBuilder.ApplyDirectory)]
    [InlineData(ArtWorkflowDefinitionBuilder.Release)]
    public async Task 插件缺失或旧插件缺少新Action时阻止生成(string missing)
    {
        await using var h = new IntegrationHarness();
        h.Gateway.Actions.Remove(missing);
        Assert.Throws<InvalidDataException>(() => h.Builder.Create(h.Catalog, [h.RecipePath, h.RecipePath], h.OutputRoot, new()));
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task 契约漂移阻止调用而展示变化只产生提示()
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(2);
        var item = h.Gateway.Actions[ArtWorkflowDefinitionBuilder.ApplyDirectory];
        var old = item.Descriptor;
        h.Gateway.Actions[old.Id.Value] = (item.Registration, Copy(old, "新文案"), item.Handler);
        Assert.Equal(plan.Definition.ContractRevision, h.Catalog.ContractRevision);
        Assert.NotEqual(plan.Definition.PresentationRevision, h.Catalog.PresentationRevision);
        var validation = h.StudioScope.ServiceProvider.GetRequiredService<IWorkflowDefinitionValidator>().Validate(plan.Definition, h.Catalog);
        Assert.True(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "catalog.presentation-stale");
        h.Gateway.Actions[old.Id.Value] = (item.Registration, Copy(old, "新风险", WorkflowActionRiskFlags.DeletesLocalFiles), item.Handler);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => h.Runner.RunAsync(plan.Definition, null, default));
        Assert.Empty(h.Gateway.Requests);
        Assert.Throws<InvalidDataException>(() => h.Builder.Create(h.Catalog, [h.RecipePath, h.RecipePath], h.OutputRoot, new()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 中断后续跑仅处理未完成项且不覆盖已有输出(int failAt)
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(3);
        var calls = 0;
        h.Gateway.Fault = request => request.ActionId.Value == ArtWorkflowDefinitionBuilder.ApplyDirectory && ++calls == failAt ? LocalGateway.Failed() : null;
        var first = await h.Runner.RunAsync(plan.Definition, null, default);
        Assert.False(first.Succeeded);
        Assert.Equal(failAt - 1, h.Recovery.Items.Count(item => item.Succeeded));
        var saved = h.Recovery.Items.Where(item => item.Succeeded).ToDictionary(item => item.OutputPath, item => SHA256.HashData(File.ReadAllBytes(item.OutputPath)));
        h.Gateway.Fault = null;
        var before = h.Gateway.Requests.Count;
        var resume = await h.Recovery.PrepareResumeAsync(h.Catalog, default);
        var second = await h.Runner.RunAsync(resume, null, default);
        Assert.True(second.Succeeded);
        Assert.All(h.Recovery.Items, item => Assert.True(item.Succeeded, item.Processing));
        var retried = h.Gateway.Requests.Skip(before).ToArray();
        Assert.DoesNotContain(retried, r => r.ActionId.Value == ArtWorkflowDefinitionBuilder.Batch);
        Assert.Equal(4 - failAt, retried.Count(r => r.ActionId.Value == ArtWorkflowDefinitionBuilder.Apply));
        foreach (var (path, hash) in saved) Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Empty(h.Recovery.PendingCleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.RunAsync(resume, null, default));
    }

    [Fact]
    public async Task 已写文件但Host拒绝结果时标记核对并使用新名称重试()
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(1);
        h.Gateway.TransformOutput = (request, _) => request.ActionId.Value == ArtWorkflowDefinitionBuilder.Apply ? LocalGateway.Failed("host.rejected") : null;
        Assert.False((await h.Runner.RunAsync(plan.Definition, null, default)).Succeeded);
        var item = Assert.Single(h.Recovery.Items);
        Assert.False(item.Succeeded);
        Assert.Contains("需核对", item.Processing);
        Assert.Contains(item.OutputPath, item.UncertainOutputs);
        var oldPath = item.OutputPath;
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(oldPath));
        h.Gateway.TransformOutput = null;
        var definition = await h.Recovery.PrepareResumeAsync(h.Catalog, default);
        Assert.NotEqual(oldPath, h.Recovery.Items[0].OutputPath);
        Assert.True((await h.Runner.RunAsync(definition, null, default)).Succeeded);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(oldPath)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("expired")]
    public async Task 源失效后要求重新生成且保留成功项(string scenario)
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(2);
        var calls = 0;
        h.Gateway.Fault = r => r.ActionId.Value == ArtWorkflowDefinitionBuilder.ApplyDirectory && ++calls == 2 ? LocalGateway.Failed() : null;
        await h.Runner.RunAsync(plan.Definition, null, default);
        var completed = h.Recovery.Items[0].OutputPath;
        var completedHash = SHA256.HashData(await File.ReadAllBytesAsync(completed));
        var source = h.Gateway.Sources.Values.Last();
        var path = source.GetProperty("path").GetString()!;
        if (scenario == "missing") File.Delete(path);
        if (scenario == "hash")
        {
            var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        }
        if (scenario == "expired")
        {
            var markerPath = Path.Combine(Path.GetDirectoryName(path)!, ".owner.json");
            var marker = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!;
            marker["createdAtUtc"] = DateTimeOffset.UtcNow.AddHours(-25); await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Recovery.PrepareResumeAsync(h.Catalog, default));
        h.Gateway.Fault = null;
        var definition = h.Recovery.PrepareRegenerate(h.Catalog);
        Assert.True((await h.Runner.RunAsync(definition, null, default)).Succeeded);
        Assert.All(h.Recovery.Items, item => Assert.True(item.Succeeded));
        Assert.Equal(completedHash, SHA256.HashData(await File.ReadAllBytesAsync(completed)));
    }

    [Fact]
    public async Task 取消保留成功项并释放Run且可再次续跑()
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(3);
        using var cts = new CancellationTokenSource();
        h.Gateway.AfterInvoke = (request, _) => { if (request.ActionId.Value == ArtWorkflowDefinitionBuilder.ApplyDirectory) cts.Cancel(); };
        var result = await h.Runner.RunAsync(plan.Definition, null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Single(h.Recovery.Items, item => item.Succeeded);
        Assert.Equal(h.Gateway.RunsCreated, h.Gateway.RunsDisposed);
        h.Gateway.AfterInvoke = null;
        Assert.True((await h.Runner.RunAsync(await h.Recovery.PrepareResumeAsync(h.Catalog, default), null, default)).Succeeded);
    }

    [Fact]
    public async Task 释放延迟不是成功且重复清理幂等()
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(1);
        h.Gateway.Fault = request => request.ActionId.Value == ArtWorkflowDefinitionBuilder.Release ?
            new(Guid.NewGuid(), WorkflowActionInvocationStatus.Succeeded, JsonSerializer.SerializeToElement(new { released = false, warningCode = "cleanup_deferred" }), null) : null;
        await h.Runner.RunAsync(plan.Definition, null, default);
        Assert.Single(h.Recovery.PendingCleanup);
        Assert.Contains("延迟", h.Recovery.Items[0].Cleanup);
        h.Gateway.Fault = null;
        var id = h.Recovery.PendingCleanup[0];
        Assert.True((await h.Runner.RunAsync(h.Recovery.PrepareCleanup(h.Catalog, id), null, default)).Succeeded);
        Assert.Empty(h.Recovery.PendingCleanup);
        Assert.True((await h.Runner.RunAsync(h.Recovery.PrepareCleanup(h.Catalog, id), null, default)).Succeeded);
        Assert.True(File.Exists(h.Recovery.Items[0].OutputPath));
    }

    [Fact]
    public async Task 编辑后的定义不写入旧恢复台账且新Scope完全隔离()
    {
        await using var h = new IntegrationHarness();
        var plan = await h.PrepareAsync(1);
        var render = plan.Definition.Steps[0];
        var edited = new WorkflowDefinitionV2(2, plan.Definition.ContractRevision, plan.Definition.PresentationRevision, "编辑后",
            [new("different-render", render.ActionId, render.Arguments)]);
        Assert.True((await h.Runner.RunAsync(edited, null, default)).Succeeded);
        Assert.Empty(h.Recovery.PendingCleanup);
        Assert.False(h.Recovery.Items[0].Succeeded);
        using var second = h.Studio.Provider!.CreateScope();
        var other = second.ServiceProvider.GetRequiredService<ArtWorkflowRecoverySession>();
        Assert.NotSame(other, h.Recovery);
        Assert.Empty(other.Items);
        h.Recovery.Dispose();
        Assert.Empty(h.Recovery.Items);
        Assert.Throws<ObjectDisposedException>(() => h.Recovery.Attach(plan));
    }

    [Fact]
    public async Task 面板编译绑定文件选择排序和完整定义进入真实Document()
    {
        await using var h = new IntegrationHarness();
        await h.PrepareAsync(1);
        h.Recovery.Abandon();
        var document = h.StudioScope.ServiceProvider.GetRequiredService<MainDocument>();
        await document.InitializeAsync(new NewDocumentActivation("G0013"), default);
        var panel = document.ArtWorkflow!;
        panel.Recipes.Add(h.RecipePath); panel.Recipes.Add(h.RecipePath);
        panel.SelectedIndex = 1; panel.MoveUpCommand.Execute(null); Assert.Equal(0, panel.SelectedIndex);
        panel.MoveDownCommand.Execute(null); Assert.Equal(1, panel.SelectedIndex);
        panel.RemoveRecipeCommand.Execute(null); Assert.Single(panel.Recipes);
        panel.OutputDirectory = h.OutputRoot;
        await panel.CreateCommand.ExecuteAsync(null);
        Assert.True(document.CanExecute, panel.Status);
        Assert.Equal(3, document.Steps.Count);
        Assert.Contains(h.Catalog.ContractRevision, document.DefinitionJson);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ArtWorkflowView { DataContext = panel };
            ((Expander)view.Content!).IsExpanded = true;
            var window = new Window { Content = view, Width = 1280, Height = 600 };
            try
            {
                window.Show();
                view.Measure(new Size(1280, 450)); view.Arrange(new Rect(0, 0, 1280, 450));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Same(panel.Recipes, view.FindControl<ListBox>("RecipeList")!.ItemsSource);
                Assert.Equal(h.OutputRoot, view.FindControl<TextBox>("OutputDirectoryBox")!.Text);
                var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-results/G0013"));
                Directory.CreateDirectory(evidence);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1280, 450));
                bitmap.Render(view);
                using var stream = File.Create(Path.Combine(evidence, "studio-panel.png"));
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        });
        Assert.True((await document.RunCurrentAsync()).Succeeded);
        panel.Refresh();
        Assert.Single(panel.Results);
        Assert.True(panel.Results[0].Succeeded);
    }

    [Fact]
    public async Task 生产程序集不相互引用且协议目录来自真实注册()
    {
        await using var h = new IntegrationHarness();
        var assemblies = new[] { typeof(FractalArtPlugin.Plugin.FractalArtPluginModule).Assembly,
            typeof(ImageLabPlugin.Plugin.ImageLabPluginModule).Assembly, typeof(WorkflowStudio.Plugin.WorkflowStudioModule).Assembly };
        foreach (var assembly in assemblies)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => assemblies.Any(other => other != assembly && other.GetName().Name == reference.Name));
        Assert.Equal(5, h.Catalog.Actions.Count);
        using var fixtureStream = typeof(G0013IntegrationTests).Assembly.GetManifestResourceStream(
            "FractalArtPlugin.WorkflowIntegration.Tests.Fixtures.g0013-actions.json")!;
        using var fixtures = JsonDocument.Parse(fixtureStream);
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var actual = h.Catalog.Actions.Single(action => action.Id.Value == fixture.GetProperty("id").GetString());
            Assert.True(JsonElement.DeepEquals(actual.InputSchema, fixture.GetProperty("inputSchema")), actual.Id.Value);
            Assert.True(JsonElement.DeepEquals(actual.OutputSchema, fixture.GetProperty("outputSchema")), actual.Id.Value);
        }
    }

    private static WorkflowActionDescriptor Copy(WorkflowActionDescriptor action, string name, WorkflowActionRiskFlags? risks = null) =>
        new(action.Id, name, action.Description, action.InputSchema, action.OutputSchema, risks ?? action.Risks,
            action.ConfirmationPolicy, action.SensitiveInputPointers);
}
