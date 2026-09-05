using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Infrastructure;
using FractalArtPlugin.Infrastructure.Workflow;
using Xunit;

namespace FractalArtPlugin.Tests;

// TTL 测试有意把自有 marker 改成过期，串行执行避免其它测试的新建产物提前触发清理。
[CollectionDefinition("Workflow Artifact recovery", DisableParallelization = true)]
public sealed class WorkflowArtifactRecoveryCollection;

[Collection("Workflow Artifact recovery")]
public sealed class G0012WorkflowFileTests
{
    [Theory]
    [InlineData("julia-default")]
    [InlineData("mandelbrot-overview")]
    [InlineData("verdant-growth")]
    [InlineData("lsystem-koch")]
    [InlineData("attractor")]
    [InlineData("layers")]
    public async Task 批量真实PNG与单张最终质量逐字节一致且可由旧Action释放(string preset)
    {
        using var files = new TemporaryFiles();
        var artwork = G0012WorkflowBatchTests.SmallArtwork();
        if (preset == "attractor")
            artwork = artwork with { GeneratorKind = FractalGeneratorKind.StrangeAttractor };
        else if (preset == "layers")
            artwork = new ArtworkLayerEditor(new ArtworkValidator()).AddFractal(artwork, FractalGeneratorKind.RecursiveTree);
        else if (preset != "julia-default") artwork = new ArtworkPresetCatalog().ApplyArtworkPreset(artwork, preset);
        var recipePath = Path.Combine(files.Root, "recipe.json");
        var recipes = Recipes();
        await recipes.ExportAsync(artwork, recipePath, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(recipePath);
        var exporter = new ArtworkExporter(TestArtworkPipeline.Create(), new PngEncoder(), new AtomicFileWriter());
        var store = new FractalWorkflowArtifactStore(exporter, G0012WorkflowBatchTests.Planner());
        var service = new WorkflowBatchExporter(recipes, G0012WorkflowBatchTests.Planner(), store);
        var handler = new ExportArtworkBatchWorkflowActionHandler(service);
        var context = G0012WorkflowBatchTests.Context(new());
        var output = await handler.InvokeAsync(G0012WorkflowBatchTests.Arguments([
            new("first", recipePath), new("second", recipePath)]), context, CancellationToken.None);
        var release = new ReleaseArtifactWorkflowActionHandler(store);
        try
        {
            var expectedPath = Path.Combine(files.Root, "expected.png");
            await exporter.ExportAsync(G0012WorkflowBatchTests.Planner().Create(artwork,
                new(artwork.Canvas.Width, artwork.Canvas.Height, false)), expectedPath, CancellationToken.None);
            var expected = await File.ReadAllBytesAsync(expectedPath);
            var paths = new HashSet<string>();
            foreach (var result in output.GetProperty("results").EnumerateArray())
            {
                var artifact = result.GetProperty("artifact");
                var path = artifact.GetProperty("path").GetString()!;
                Assert.True(paths.Add(path));
                var actual = await File.ReadAllBytesAsync(path);
                Assert.Equal(expected, actual);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(actual)), artifact.GetProperty("sha256").GetString());
                Assert.Equal(actual.Length, artifact.GetProperty("byteLength").GetInt64());
                using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, ".owner.json")));
                Assert.Equal(context.InvocationId, marker.RootElement.GetProperty("invocationId").GetGuid());
                Assert.Equal(result.GetProperty("itemId").GetString(), marker.RootElement.GetProperty("itemId").GetString());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(recipePath));
        }
        finally
        {
            foreach (var result in output.GetProperty("results").EnumerateArray())
            {
                var arguments = JsonSerializer.SerializeToElement(new { artifact = result.GetProperty("artifact") });
                Assert.True((await release.InvokeAsync(arguments, context, CancellationToken.None)).GetProperty("released").GetBoolean());
                Assert.True((await release.InvokeAsync(arguments, context, CancellationToken.None)).GetProperty("released").GetBoolean());
            }
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("unknown-version")]
    [InlineData("missing")]
    [InlineData("oversize")]
    public async Task 实际配方读取拒绝缺失损坏和超预算(string scenario)
    {
        using var files = new TemporaryFiles();
        var path = Path.Combine(files.Root, "recipe.json");
        if (scenario != "missing")
        {
            var content = scenario switch
            {
                "empty" => "",
                "invalid" => "{invalid}",
                "unknown-version" => "{\"schemaVersion\":2,\"artworkSchemaVersion\":8,\"artwork\":{}}",
                _ => new string(' ', WorkflowRecipeFiles.MaximumBytes + 1)
            };
            await File.WriteAllTextAsync(path, content);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => Recipes().ReadBoundedAsync(path, WorkflowRecipeFiles.MaximumBytes, CancellationToken.None));
    }

    [Fact]
    public async Task 实际字节计数与剩余预算一致且预先取消不读取()
    {
        using var files = new TemporaryFiles();
        var path = Path.Combine(files.Root, "recipe.json");
        var recipes = Recipes();
        await recipes.ExportAsync(G0012WorkflowBatchTests.SmallArtwork(), path, CancellationToken.None);
        var length = (int)new FileInfo(path).Length;
        Assert.Equal(length, (await recipes.ReadBoundedAsync(path, length, CancellationToken.None)).ByteLength);
        await Assert.ThrowsAsync<InvalidDataException>(() => recipes.ReadBoundedAsync(path, length - 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => recipes.ReadBoundedAsync(path, 0, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recipes.ReadBoundedAsync(path, length, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 写入失败或写入后取消清理当前产物并保留原异常(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("模拟写入失败");
        var exporter = new FileExporter(async path =>
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            if (cancel) cancellation.Cancel();
            else throw failure;
        });
        var id = Guid.NewGuid();
        var store = new FractalWorkflowArtifactStore(exporter, G0012WorkflowBatchTests.Planner());
        var caught = await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(),
            id, "run", cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(caught);
        else Assert.Same(failure, caught);
        Assert.False(Directory.Exists(OperationRoot(id)));
    }

    [Fact]
    public async Task 摘要读取被占用时保留标记且解锁后TTL可以恢复()
    {
        FileStream? locked = null;
        var exporter = new FileExporter(async path =>
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        var id = Guid.NewGuid();
        var root = OperationRoot(id);
        var store = new FractalWorkflowArtifactStore(exporter, G0012WorkflowBatchTests.Planner());
        try
        {
            await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(), id, "run", CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(root, ".owner.json")));
        }
        finally { locked?.Dispose(); }
        await ExpireAsync(root);
        await store.CleanupExpiredAsync(CancellationToken.None);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task 释放被占用PNG不会先删除marker且旧marker仍可清理()
    {
        var store = new FractalWorkflowArtifactStore(new FileExporter(path => File.WriteAllBytesAsync(path, [1])), G0012WorkflowBatchTests.Planner());
        var artifact = await store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(), Guid.NewGuid(), "run", CancellationToken.None);
        var root = Path.GetDirectoryName(artifact.Path)!;
        try
        {
            using (var locked = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = await store.ReleaseAsync(artifact, false, CancellationToken.None);
                Assert.False(result.Released);
                Assert.Equal("cleanup_deferred", result.WarningCode);
                Assert.True(File.Exists(Path.Combine(root, ".owner.json")));
            }
            var markerPath = Path.Combine(root, ".owner.json");
            var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!;
            marker.AsObject().Remove("invocationId");
            marker.AsObject().Remove("itemId");
            await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
            await ExpireAsync(root);
            await store.CleanupExpiredAsync(CancellationToken.None);
            Assert.False(Directory.Exists(root));
        }
        finally { await store.ReleaseAsync(artifact, false, CancellationToken.None); }
    }

    [Fact]
    public async Task 重复操作身份不会覆盖或回滚既有产物()
    {
        var store = new FractalWorkflowArtifactStore(new FileExporter(path => File.WriteAllBytesAsync(path, [1])), G0012WorkflowBatchTests.Planner());
        var id = Guid.NewGuid();
        var artifact = await store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(), id, "run", CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(), id, "run", CancellationToken.None));
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(artifact.Path));
        }
        finally { await store.ReleaseAsync(artifact, false, CancellationToken.None); }
    }

    [Fact]
    public async Task 伪造路径或marker不会删除真实产物且额外文件阻止整个目录删除()
    {
        var store = new FractalWorkflowArtifactStore(new FileExporter(path => File.WriteAllBytesAsync(path, [1])), G0012WorkflowBatchTests.Planner());
        var artifact = await store.CreateAsync(G0012WorkflowBatchTests.SmallArtwork(), Guid.NewGuid(), "run", CancellationToken.None);
        var root = Path.GetDirectoryName(artifact.Path)!;
        var markerPath = Path.Combine(root, ".owner.json");
        var original = await File.ReadAllTextAsync(markerPath);
        var extra = Path.Combine(root, "unknown.txt");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReleaseAsync(artifact with
            { Path = Path.Combine(root, "..", "source.png") }, false, CancellationToken.None));
            var forged = JsonNode.Parse(original)!;
            forged["producerOperationId"] = Guid.NewGuid();
            await File.WriteAllTextAsync(markerPath, forged.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReleaseAsync(artifact, false, CancellationToken.None));
            Assert.True(File.Exists(artifact.Path));
            await File.WriteAllTextAsync(markerPath, original);
            await File.WriteAllTextAsync(extra, "不属于 Artifact 协议的文件");
            Assert.False((await store.ReleaseAsync(artifact, false, CancellationToken.None)).Released);
            Assert.True(File.Exists(artifact.Path));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            File.Delete(extra);
            await File.WriteAllTextAsync(markerPath, original);
            await store.ReleaseAsync(artifact, false, CancellationToken.None);
        }
    }

    [Fact]
    public async Task 操作目录重解析点被拒绝且不删除链接目标()
    {
        using var files = new TemporaryFiles();
        var targetFile = Path.Combine(files.Root, "source.png");
        await File.WriteAllBytesAsync(targetFile, [7, 8, 9]);
        var id = Guid.NewGuid();
        var root = OperationRoot(id);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows 普通用户通常没有创建符号链接的特权；junction 同样设置 ReparsePoint，
                // 可无提权地验证生产删除保护。只在此测试创建链接，不启动窗口、不删除目标目录。
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
                    root.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" +
                    files.Root.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null");
                using var process = System.Diagnostics.Process.Start(start)!;
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }
            else Directory.CreateSymbolicLink(root, files.Root);
            Assert.True(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint));
            var artifact = new WorkflowFileArtifact(FractalWorkflowFileArtifactContract.Name, 1,
                FractalWorkflowFileArtifactContract.PluginId, id, "run", Path.Combine(root, "source.png"), "image/png", 3, new string('A', 64));
            var store = new FractalWorkflowArtifactStore(new FileExporter(_ => Task.CompletedTask), G0012WorkflowBatchTests.Planner());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReleaseAsync(artifact, false, CancellationToken.None));
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(targetFile));
        }
        finally
        {
            // 非递归删除且仅接受刚创建的重解析点；绝不能沿 junction 枚举或删除目标内容。
            if (Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(root, false);
        }
    }

    [Fact]
    public async Task 流在读取中增长超限最多读取一个探测字节()
    {
        using var stream = new ChunkedStream(1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkflowRecipeFiles.ReadContentAsync(stream, 100, CancellationToken.None));
        Assert.Equal(101, stream.Position);
    }

    [Fact]
    public async Task 流读取中取消及时终止后续读取()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ChunkedStream(1024) { AfterRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkflowRecipeFiles.ReadContentAsync(stream, 1024, cancellation.Token));
        Assert.Equal(16, stream.Position);
    }

    private sealed class ChunkedStream(int length) : MemoryStream(new byte[length])
    {
        internal Action? AfterRead { get; init; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 16)], cancellationToken);
            AfterRead?.Invoke();
            return read;
        }
    }

    private static WorkflowRecipeFiles Recipes() => new(new WorkflowRecipeCodec(new ArtworkSnapshotCodec(new ArtworkValidator())), new AtomicFileWriter());
    private static string OperationRoot(Guid id) => Path.Combine(FractalWorkflowFileArtifactContract.RootPath,
        FractalWorkflowFileArtifactContract.PluginId, id.ToString("D"));
    private static async Task ExpireAsync(string root)
    {
        var path = Path.Combine(root, ".owner.json");
        var marker = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        marker["createdAtUtc"] = DateTimeOffset.UtcNow.AddHours(-25);
        await File.WriteAllTextAsync(path, marker.ToJsonString());
    }
    private sealed class FileExporter(Func<string, Task> write) : IArtworkExporter
    {
        public Task ExportAsync(ArtworkExportPlan plan, string path, CancellationToken cancellationToken) => write(path);
    }
    private sealed class TemporaryFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"fractal-g0012-tests-{Guid.NewGuid():N}");
        internal TemporaryFiles() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root)) File.Delete(path);
            Directory.Delete(Root, false);
        }
    }
}
