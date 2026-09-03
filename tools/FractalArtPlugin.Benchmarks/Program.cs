using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Fractals.Julia;
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
        samples.Add(new Sample(
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            GC.CollectionCount(0) - collections[0],
            GC.CollectionCount(1) - collections[1],
            GC.CollectionCount(2) - collections[2],
            (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds));
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

var report = new BenchmarkReport(
    DateTimeOffset.Now,
    Environment.MachineName,
    Environment.OSVersion.ToString(),
    Environment.Version.ToString(),
    Environment.ProcessorCount,
    Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
    GitCommit(),
    "dotnet run --project tools/FractalArtPlugin.Benchmarks -c Release -- <output.json>",
    results,
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
    Buffer.BlockCopy(field.Values, 0, bytes, 0, field.Values.Length * sizeof(float));
    for (var index = 0; index < field.Escaped.Length; index++)
    {
        bytes[field.Values.Length * sizeof(float) + index] = field.Escaped[index] ? (byte)1 : (byte)0;
    }

    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
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
    double CpuMilliseconds);

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
    double CancellationResponseMilliseconds,
    IReadOnlyList<double> CancellationSamplesMilliseconds);
