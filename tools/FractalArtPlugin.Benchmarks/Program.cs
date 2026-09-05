using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Fractals.Attractor;
using FractalArtPlugin.Domain.Fractals.Julia;
using FractalArtPlugin.Domain.Fractals.RecursiveTree;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

// 这是可重复执行的本地证据工具，而不是微秒级门禁。场景尺寸有意保持较小，
// 使 1024 位参考路径在普通开发机上也能完成；优化前后必须使用同一参数比较。
var scenarios = new[]
{
    new Scenario("normal-double", "3.2", 96, false, 96, 80, 60),
    new Scenario("deep-96", "1e-80", 96, true, 96, 48, 32),
    new Scenario("deep-256", "1e-220", 256, true, 96, 32, 24),
    new Scenario("extreme-1024", "1e-1000", 1024, true, 64, 16, 12)
};

var generator = new JuliaFieldGenerator();
var results = new List<ScenarioResult>();
foreach (var scenario in scenarios)
{
    var definition = ArtworkDefinition.CreateDefault().Julia with
    {
        CenterX = "-0.745",
        CenterY = "0.113",
        Scale = scenario.Scale,
        PrecisionDigits = scenario.PrecisionDigits,
        ForceHighPrecision = scenario.ForceHighPrecision,
        MaxIterations = scenario.Iterations
    };
    var precision = scenario.ForceHighPrecision ? NumericPrecision.Arbitrary : NumericPrecision.Double;
    var descriptor = precision == NumericPrecision.Arbitrary
        ? new PrecisionPolicy().Describe(definition, scenario.Height)
        : new PrecisionDescriptor(scenario.PrecisionDigits, 16, 16, 0, 16, 0, "double");
    var context = new RenderContext(
        scenario.Width,
        scenario.Height,
        RenderQuality.Draft,
        42,
        RenderContext.CurrentRendererVersion,
        precision,
        descriptor.EffectiveDigits)
    {
        ConfiguredPrecisionDigits = descriptor.ConfiguredDigits,
        EffectivePrecisionDigits = descriptor.EffectiveDigits
    };

    _ = await generator.GenerateAsync(definition, context, CancellationToken.None);
    var samples = new List<Sample>();
    ScalarField? latest = null;
    for (var run = 0; run < 5; run++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        latest = await generator.GenerateAsync(definition, context, CancellationToken.None);
        stopwatch.Stop();
        process.Refresh();
        samples.Add(new Sample(
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            GC.CollectionCount(0) - collections[0],
            GC.CollectionCount(1) - collections[1],
            GC.CollectionCount(2) - collections[2],
            (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            process.WorkingSet64,
            process.PeakWorkingSet64));
    }

    var ordered = samples.OrderBy(sample => sample.ElapsedMilliseconds).ToArray();
    var median = ordered[ordered.Length / 2];
    var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
    var totalIterations = (long)scenario.Width * scenario.Height * scenario.Iterations;
    results.Add(new ScenarioResult(
        scenario,
        samples,
        median.ElapsedMilliseconds,
        p95.ElapsedMilliseconds,
        scenario.Width * scenario.Height / (median.ElapsedMilliseconds / 1000d),
        totalIterations / (median.ElapsedMilliseconds / 1000d),
        Fingerprint(latest!),
        ReferencePoints(latest!),
        latest!.Diagnostics));
}

var cancellationDefinition = ArtworkDefinition.CreateDefault().Julia with
{
    Scale = "1e-1000",
    PrecisionDigits = 1024,
    ForceHighPrecision = true,
    MaxIterations = 4096
};
var cancellationContext = new RenderContext(
    64, 64, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 1016)
{
    ConfiguredPrecisionDigits = 1024,
    EffectivePrecisionDigits = 1016,
    CancellationCheckInterval = 16
};
var cancellationResponses = new List<double>();
for (var run = 0; run < 5; run++)
{
    using var cancellation = new CancellationTokenSource();
    var cancellationTask = generator.GenerateAsync(cancellationDefinition, cancellationContext, cancellation.Token);
    await Task.Delay(20);
    var cancellationStopwatch = Stopwatch.StartNew();
    cancellation.Cancel();
    try
    {
        await cancellationTask;
    }
    catch (OperationCanceledException)
    {
        // 取消是该测量的预期终点。
    }

    cancellationStopwatch.Stop();
    cancellationResponses.Add(cancellationStopwatch.Elapsed.TotalMilliseconds);
}

var orderedCancellation = cancellationResponses.Order().ToArray();
var cancellationP95 = orderedCancellation[(int)Math.Ceiling(orderedCancellation.Length * 0.95) - 1];

// G0011 在原有高精度专项之外补充静态闭环的四类代表工作负载。这里记录趋势证据而不设置机器相关的
// 毫秒阈值；真正的硬门禁仍由像素、采样、缓存字节和取消契约等确定性预算承担。
var closureArtwork = ArtworkDefinition.CreateDefault();
var closureContext = RenderContext.ForThumbnail(closureArtwork, 320);
var gradientMapper = new LinearGradientMapper();
var treeGenerator = new RecursiveTreePathGenerator();
var pathRenderer = new PathStrokeRenderer();
var attractorDefinition = closureArtwork.StrangeAttractor with { SampleCount = 200_000, GlowEnabled = false };
var attractorKernels = new IAttractorFormulaKernel[] { new CliffordAttractorKernel(), new DeJongAttractorKernel() };
var attractorGenerator = new StrangeAttractorPointGenerator(attractorKernels);
var densityRenderer = new PointDensityRenderer();
var densityMapper = new DensityGradientMapper();
var compositor = new LayerCompositor();
var masterEffects = new MasterEffectRenderer();
var closureScenarios = new[]
{
    await MeasureClosureAsync("escape-time-rgba", async token =>
    {
        var field = await generator.GenerateAsync(closureArtwork.Julia, closureContext, token);
        return gradientMapper.Map(field, closureArtwork.Gradient, token);
    }),
    await MeasureClosureAsync("recursive-path-rgba", token =>
    {
        var path = treeGenerator.Generate(closureArtwork.RecursiveTree, closureArtwork.Seed, token);
        return Task.FromResult(pathRenderer.Render(
            path,
            new PathStrokeDefinition(closureArtwork.RecursiveTree.StrokeWidth, 0.82),
            closureArtwork.Gradient,
            closureArtwork.Canvas.Background,
            closureContext,
            token));
    }),
    await MeasureClosureAsync("attractor-density-rgba", async token =>
    {
        var cloud = await attractorGenerator.GenerateAsync(
            attractorDefinition, closureArtwork.Seed, closureContext with { PointSampleBudget = 200_000 }, token);
        var field = await densityRenderer.RenderAsync(cloud, attractorDefinition, closureContext, token);
        return densityMapper.Map(field, closureArtwork.Gradient, token);
    }),
    await MeasureClosureAsync("multi-layer-effects", token =>
    {
        var current = compositor.CreateBackground(320, 180, new RgbaColor(10, 14, 28));
        for (var layer = 0; layer < 3; layer++)
        {
            current = compositor.Composite(
                current,
                CreateSyntheticLayer(320, 180, layer),
                0.72,
                layer == 1 ? LayerBlendMode.Screen : LayerBlendMode.Normal,
                null,
                token);
        }

        return Task.FromResult(masterEffects.Apply(
            current,
            new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(true, 0.05, 0.08, 1.1),
                new BloomEffectDefinition(true, 0.72, 2.4, 0.8)
            ]),
            token));
    })
};

var report = new BenchmarkReport(
    DateTimeOffset.Now,
    Environment.MachineName,
    Environment.OSVersion.ToString(),
    Environment.Version.ToString(),
    Environment.ProcessorCount,
    Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
    GitCommit(),
    "dotnet run --project tools/FractalArtPlugin.Benchmarks -c Debug -- <output.json>",
    results,
    closureScenarios,
    cancellationP95,
    cancellationResponses);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("benchmark-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(json);

static string Fingerprint(ScalarField field)
{
    var bytes = new byte[field.Values.Length * sizeof(float) + field.Escaped.Length];
    Buffer.BlockCopy(field.Values.ToArray(), 0, bytes, 0, field.Values.Length * sizeof(float));
    for (var index = 0; index < field.Escaped.Length; index++)
    {
        bytes[field.Values.Length * sizeof(float) + index] = field.Escaped[index] ? (byte)1 : (byte)0;
    }

    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
}

static async Task<ClosureScenarioResult> MeasureClosureAsync(
    string name,
    Func<CancellationToken, Task<ImageSurface>> render)
{
    _ = await render(CancellationToken.None);
    var samples = new List<ClosureSample>();
    ImageSurface? latest = null;
    for (var run = 0; run < 3; run++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSet = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        latest = await render(CancellationToken.None);
        stopwatch.Stop();
        process.Refresh();
        samples.Add(new ClosureSample(
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            process.WorkingSet64 - workingSet,
            process.WorkingSet64,
            process.PeakWorkingSet64));
    }

    var ordered = samples.OrderBy(sample => sample.ElapsedMilliseconds).ToArray();
    return new ClosureScenarioResult(
        name,
        latest!.Width,
        latest.Height,
        ordered[ordered.Length / 2].ElapsedMilliseconds,
        ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1].ElapsedMilliseconds,
        Convert.ToHexString(SHA256.HashData(latest.Pixels.Span)).ToLowerInvariant()[..16],
        samples);
}

static ImageSurface CreateSyntheticLayer(int width, int height, int layer)
{
    var pixels = new byte[checked(width * height * 4)];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)((x + layer * 31) % 256);
            pixels[offset + 1] = (byte)((y * 2 + layer * 47) % 256);
            pixels[offset + 2] = (byte)((x + y + layer * 59) % 256);
            pixels[offset + 3] = (byte)(96 + layer * 48);
        }
    }

    return new ImageSurface(width, height, pixels);
}

static IReadOnlyDictionary<string, PointResult> ReferencePoints(ScalarField field)
{
    var points = new Dictionary<string, PointResult>();
    Add("top-left", 0, 0);
    Add("center", field.Width / 2, field.Height / 2);
    Add("right-edge", field.Width - 1, field.Height / 2);
    Add("bottom-right", field.Width - 1, field.Height - 1);
    return points;

    void Add(string name, int x, int y)
    {
        var index = y * field.Width + x;
        points[name] = new PointResult(x, y, field.Values[index], field.Escaped[index]);
    }
}

static string GitCommit()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return process is null ? "unknown" : process.StandardOutput.ReadToEnd().Trim();
    }
    catch
    {
        return "unknown";
    }
}

internal sealed record Scenario(
    string Name,
    string Scale,
    int PrecisionDigits,
    bool ForceHighPrecision,
    int Iterations,
    int Width,
    int Height);

internal sealed record Sample(
    double ElapsedMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double CpuMilliseconds,
    long WorkingSetBytes,
    long PeakWorkingSetBytes);

internal sealed record ClosureSample(
    double ElapsedMilliseconds,
    long AllocatedBytes,
    long WorkingSetDeltaBytes,
    long WorkingSetBytes,
    long PeakWorkingSetBytes);

internal sealed record ClosureScenarioResult(
    string Name,
    int Width,
    int Height,
    double MedianMilliseconds,
    double P95Milliseconds,
    string Fingerprint,
    IReadOnlyList<ClosureSample> Samples);

internal sealed record PointResult(int X, int Y, float Value, bool Escaped);

internal sealed record ScenarioResult(
    Scenario Scenario,
    IReadOnlyList<Sample> Samples,
    double MedianMilliseconds,
    double P95Milliseconds,
    double PixelsPerSecond,
    double MaximumIterationsPerSecond,
    string Fingerprint,
    IReadOnlyDictionary<string, PointResult> ReferencePoints,
    RenderDiagnostics Diagnostics);

internal sealed record BenchmarkReport(
    DateTimeOffset CreatedAt,
    string MachineName,
    string OperatingSystem,
    string Runtime,
    int LogicalProcessorCount,
    string Processor,
    string GitCommit,
    string Command,
    IReadOnlyList<ScenarioResult> Scenarios,
    IReadOnlyList<ClosureScenarioResult> StaticClosureScenarios,
    double CancellationResponseMilliseconds,
    IReadOnlyList<double> CancellationSamplesMilliseconds);
