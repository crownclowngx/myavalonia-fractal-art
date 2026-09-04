using System.Text.Json;
using System.Globalization;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Application;

public interface IArtworkSnapshotCodec
{
    DocumentContent Encode(ArtworkDefinition artwork);
    ArtworkDefinition Decode(DocumentContent content);
}

/// <summary>
/// 作品格式的唯一序列化边界。DTO 的可空成员只用于发现缺失字段，领域对象本身保持完整；
/// 解码成功并通过全部验证前不会把任何部分状态交给 Document。
/// </summary>
internal sealed class ArtworkSnapshotCodec(IArtworkValidator validator) : IArtworkSnapshotCodec
{
    public const int ContentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    public DocumentContent Encode(ArtworkDefinition artwork)
    {
        validator.Validate(artwork);
        var dto = new SnapshotLayeredDto(
            artwork.FormatVersion,
            new CanvasDto(artwork.Canvas.Width, artwork.Canvas.Height, artwork.Canvas.Background.ToHex()),
            new PresentationDto(
                artwork.Presentation.SelectedSection,
                artwork.Presentation.HighQualityPreview,
                artwork.Presentation.SelectedLayerId),
            artwork.Layers.Select(EncodeLayer).ToArray(),
            EncodeEffects(artwork.MasterEffects));
        return new DocumentContent(ContentSchemaVersion, JsonSerializer.SerializeToElement(dto, JsonOptions));
    }

    public ArtworkDefinition Decode(DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.SchemaVersion != ContentSchemaVersion)
        {
            throw new NotSupportedException($"不支持 Document 内容 schema {content.SchemaVersion}。");
        }

        if (content.Payload.ValueKind != JsonValueKind.Object ||
            !content.Payload.TryGetProperty("formatVersion", out var formatElement) ||
            !formatElement.TryGetInt32(out var formatVersion))
        {
            throw new InvalidDataException("作品缺少整数 formatVersion。");
        }

        return formatVersion switch
        {
            1 => DecodeVersion1(content.Payload),
            2 => DecodeVersion2(content.Payload),
            3 => DecodeVersion3(content.Payload),
            4 => DecodeVersion4(content.Payload),
            5 => DecodeVersion5(content.Payload),
            6 => DecodeVersion6(content.Payload),
            7 => DecodeLayeredVersion(content.Payload, 7),
            ArtworkDefinition.CurrentFormatVersion => DecodeLayeredVersion(content.Payload, ArtworkDefinition.CurrentFormatVersion),
            _ => throw new NotSupportedException($"不支持作品格式版本 {formatVersion}。")
        };
    }

    private ArtworkDefinition DecodeVersion5(JsonElement payload) => DecodeVersionedSnapshot(payload, 5);

    private ArtworkDefinition DecodeVersion6(JsonElement payload) => DecodeVersionedSnapshot(payload, 6);

    /// <summary>
    /// v7 与 v8 共用图层树外形；v8 为每层和每个探索配方增加吸引子字段。读取 v7 时只补安全默认值，
    /// 不改变任何旧生成器参数，读取 v8 时则把吸引子字段视为必要内容，避免损坏文件静默降级。
    /// </summary>
    private ArtworkDefinition DecodeLayeredVersion(JsonElement payload, int sourceVersion)
    {
        SnapshotLayeredDto? dto;
        try
        {
            dto = payload.Deserialize<SnapshotLayeredDto>(JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"v{sourceVersion} 作品 JSON 结构损坏。", exception);
        }

        if (dto?.FormatVersion != sourceVersion || dto.Canvas?.Width is null ||
            dto.Canvas.Height is null || !RgbaColor.TryParse(dto.Canvas.Background, out var background) ||
            dto.Presentation?.HighQualityPreview is null || string.IsNullOrWhiteSpace(dto.Presentation.SelectedSection) ||
            string.IsNullOrWhiteSpace(dto.Presentation.SelectedLayerId) || dto.Layers is null || dto.MasterEffects is null)
        {
            throw new InvalidDataException($"v{sourceVersion} 作品缺少画布、呈现、图层或 Master Effects 必要字段。");
        }

        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(dto.Canvas.Width.Value, dto.Canvas.Height.Value, background),
            new ArtworkPresentationDefinition(
                dto.Presentation.SelectedSection,
                dto.Presentation.HighQualityPreview.Value,
                dto.Presentation.SelectedLayerId),
            dto.Layers.Select(layer => DecodeLayer(layer ??
                throw new InvalidDataException($"v{sourceVersion} 图层集合包含 null。"), sourceVersion)),
            DecodeMasterEffects(dto.MasterEffects));
        validator.Validate(artwork);
        return artwork;
    }

    private ArtworkDefinition DecodeVersion4(JsonElement payload) => DecodeVersionedSnapshot(payload, 4);

    /// <summary>
    /// G0004 的 v3 文件只有 Julia 配方。迁移明确选择 Julia，并为尚未使用的递归树补入安全默认值；
    /// 候选和收藏也按相同规则升级，保证旧九宫格恢复后仍指向原来的 Julia 画面。
    /// </summary>
    private ArtworkDefinition DecodeVersion3(JsonElement payload) => DecodeVersionedSnapshot(payload, 3);

    private ArtworkDefinition DecodeVersionedSnapshot(JsonElement payload, int sourceVersion)
    {
        SnapshotDto? dto;
        try
        {
            dto = payload.Deserialize<SnapshotDto>(JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("作品 JSON 结构损坏。", exception);
        }

        if (dto?.FormatVersion is null || dto.Seed is null || dto.Canvas is null || dto.Julia is null ||
            dto.Gradient is null || dto.Presentation is null || dto.Exploration is null)
        {
            throw new InvalidDataException("作品缺少 formatVersion、seed、canvas、julia、gradient、presentation 或 exploration。");
        }

        var canvas = dto.Canvas;
        var julia = dto.Julia;
        var isVersion3 = sourceVersion == 3;
        var supportsVersion5Fields = sourceVersion >= 5;
        var isVersion6 = sourceVersion == 6;
        var recursiveTree = isVersion3 ? null : dto.RecursiveTree;
        var gradient = dto.Gradient;
        var presentation = dto.Presentation;
        if (canvas.Width is null || canvas.Height is null || string.IsNullOrWhiteSpace(julia.CenterX) ||
            string.IsNullOrWhiteSpace(julia.CenterY) || string.IsNullOrWhiteSpace(julia.Scale) ||
            string.IsNullOrWhiteSpace(julia.ConstantReal) || string.IsNullOrWhiteSpace(julia.ConstantImaginary) ||
            julia.MaxIterations is null || julia.ForceHighPrecision is null || julia.PrecisionDigits is null ||
            (!isVersion3 && (dto.GeneratorKind is null || recursiveTree is null || !HasAllFields(recursiveTree))) ||
            (supportsVersion5Fields && (dto.Mandelbrot is null || !HasAllFields(dto.Mandelbrot) ||
                dto.LSystem is null || !HasAllFields(dto.LSystem))) ||
            (isVersion6 && (dto.Graph is null || dto.Effects is null)) ||
            presentation.HighQualityPreview is null ||
            !RgbaColor.TryParse(canvas.Background, out var background) ||
            !RgbaColor.TryParse(gradient.Start, out var start) ||
            !RgbaColor.TryParse(gradient.End, out var end) ||
            !RgbaColor.TryParse(gradient.Interior, out var interior) ||
            string.IsNullOrWhiteSpace(presentation.SelectedSection))
        {
            throw new InvalidDataException("作品包含缺失或非法的画布、Julia、渐变或呈现字段。");
        }

        var generatorKind = isVersion3 ? FractalGeneratorKind.Julia : (FractalGeneratorKind)dto.GeneratorKind!.Value;
        if (!Enum.IsDefined(generatorKind))
        {
            throw new InvalidDataException($"v{sourceVersion} 作品包含未知生成器 {dto.GeneratorKind}。");
        }

        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            dto.Seed.Value,
            new CanvasDefinition(canvas.Width.Value, canvas.Height.Value, background),
            generatorKind,
            new JuliaDefinition(
                julia.CenterX,
                julia.CenterY,
                julia.Scale,
                julia.ConstantReal,
                julia.ConstantImaginary,
                julia.MaxIterations.Value,
                julia.ForceHighPrecision.Value,
                julia.PrecisionDigits.Value),
            supportsVersion5Fields ? DecodeMandelbrot(dto.Mandelbrot!) : ArtworkDefinition.CreateDefault().Mandelbrot,
            isVersion3 ? ArtworkDefinition.CreateDefault().RecursiveTree : DecodeRecursiveTree(recursiveTree!),
            supportsVersion5Fields ? DecodeLSystem(dto.LSystem!) : ArtworkDefinition.CreateDefault().LSystem,
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                presentation.SelectedSection,
                presentation.HighQualityPreview.Value),
            DecodeExploration(dto.Exploration, sourceVersion),
            isVersion6 ? DecodeGraph(dto.Graph!) : ArtworkGraphFactory.Create(generatorKind),
            isVersion6 ? DecodeEffects(dto.Effects!) : EffectChainDefinition.Empty);
        validator.Validate(artwork);
        return artwork.ClearLegacyGraphOverride();
    }

    /// <summary>
    /// G0003 的 v2 快照没有探索字段。迁移时只补入明确的空探索状态，既不虚构收藏，也不改变原有渲染配方。
    /// </summary>
    private ArtworkDefinition DecodeVersion2(JsonElement payload)
    {
        var legacy = DecodeVersion2Fields(payload);
        var migrated = legacy with
        {
            FormatVersion = ArtworkDefinition.CurrentFormatVersion,
            Exploration = ArtworkExplorationDefinition.CreateDefault()
        };
        validator.Validate(migrated);
        return migrated.ClearLegacyGraphOverride();
    }

    private ArtworkDefinition DecodeVersion2Fields(JsonElement payload)
    {
        SnapshotDto? dto;
        try
        {
            dto = payload.Deserialize<SnapshotDto>(JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("v2 作品 JSON 结构损坏。", exception);
        }

        if (dto?.FormatVersion != 2 || dto.Seed is null || dto.Canvas?.Width is null || dto.Canvas.Height is null ||
            dto.Julia is null || string.IsNullOrWhiteSpace(dto.Julia.CenterX) || string.IsNullOrWhiteSpace(dto.Julia.CenterY) ||
            string.IsNullOrWhiteSpace(dto.Julia.Scale) || string.IsNullOrWhiteSpace(dto.Julia.ConstantReal) ||
            string.IsNullOrWhiteSpace(dto.Julia.ConstantImaginary) || dto.Julia.MaxIterations is null ||
            dto.Julia.ForceHighPrecision is null || dto.Julia.PrecisionDigits is null || dto.Gradient is null ||
            dto.Presentation?.HighQualityPreview is null || string.IsNullOrWhiteSpace(dto.Presentation.SelectedSection) ||
            !RgbaColor.TryParse(dto.Canvas.Background, out var background) ||
            !RgbaColor.TryParse(dto.Gradient.Start, out var start) ||
            !RgbaColor.TryParse(dto.Gradient.End, out var end) ||
            !RgbaColor.TryParse(dto.Gradient.Interior, out var interior))
        {
            throw new InvalidDataException("v2 作品包含缺失或非法字段，无法安全迁移。");
        }

        return new ArtworkDefinition(
            2,
            dto.Seed.Value,
            new CanvasDefinition(dto.Canvas.Width.Value, dto.Canvas.Height.Value, background),
            FractalGeneratorKind.Julia,
            new JuliaDefinition(dto.Julia.CenterX, dto.Julia.CenterY, dto.Julia.Scale,
                dto.Julia.ConstantReal, dto.Julia.ConstantImaginary, dto.Julia.MaxIterations.Value,
                dto.Julia.ForceHighPrecision.Value, dto.Julia.PrecisionDigits.Value),
            ArtworkDefinition.CreateDefault().Mandelbrot,
            ArtworkDefinition.CreateDefault().RecursiveTree,
            ArtworkDefinition.CreateDefault().LSystem,
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(dto.Presentation.SelectedSection, dto.Presentation.HighQualityPreview.Value),
            ArtworkExplorationDefinition.CreateDefault(),
            ArtworkGraphFactory.Create(FractalGeneratorKind.Julia),
            EffectChainDefinition.Empty);
    }

    /// <summary>
    /// 把 G0003 初版的 IEEE 754 数值显式迁移为 round-trip 十进制文本。
    /// 迁移不会声称恢复 double 已经丢失的位数，但此后所有新平移和缩放都由当前高精度模型保存。
    /// </summary>
    private ArtworkDefinition DecodeVersion1(JsonElement payload)
    {
        LegacySnapshotDto? dto;
        try
        {
            dto = payload.Deserialize<LegacySnapshotDto>(JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("v1 作品 JSON 结构损坏。", exception);
        }

        if (dto?.Seed is null || dto.Canvas?.Width is null || dto.Canvas.Height is null ||
            dto.Julia?.CenterX is null || dto.Julia.CenterY is null || dto.Julia.Scale is null ||
            dto.Julia.ConstantReal is null || dto.Julia.ConstantImaginary is null || dto.Julia.MaxIterations is null ||
            dto.Gradient is null || dto.Presentation?.HighQualityPreview is null ||
            !RgbaColor.TryParse(dto.Canvas.Background, out var background) ||
            !RgbaColor.TryParse(dto.Gradient.Start, out var start) ||
            !RgbaColor.TryParse(dto.Gradient.End, out var end) ||
            !RgbaColor.TryParse(dto.Gradient.Interior, out var interior) ||
            string.IsNullOrWhiteSpace(dto.Presentation.SelectedSection))
        {
            throw new InvalidDataException("v1 作品包含缺失或非法字段，无法安全迁移。");
        }

        var migrated = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            dto.Seed.Value,
            new CanvasDefinition(dto.Canvas.Width.Value, dto.Canvas.Height.Value, background),
            FractalGeneratorKind.Julia,
            new JuliaDefinition(
                FormatDouble(dto.Julia.CenterX.Value),
                FormatDouble(dto.Julia.CenterY.Value),
                FormatDouble(dto.Julia.Scale.Value),
                FormatDouble(dto.Julia.ConstantReal.Value),
                FormatDouble(dto.Julia.ConstantImaginary.Value),
                dto.Julia.MaxIterations.Value,
                false,
                96),
            ArtworkDefinition.CreateDefault().Mandelbrot,
            ArtworkDefinition.CreateDefault().RecursiveTree,
            ArtworkDefinition.CreateDefault().LSystem,
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                dto.Presentation.SelectedSection,
                dto.Presentation.HighQualityPreview.Value),
            ArtworkExplorationDefinition.CreateDefault(),
            ArtworkGraphFactory.Create(FractalGeneratorKind.Julia),
            EffectChainDefinition.Empty);
        validator.Validate(migrated);
        return migrated.ClearLegacyGraphOverride();
    }

    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record SnapshotDto(
        int? FormatVersion,
        long? Seed,
        CanvasDto? Canvas,
        int? GeneratorKind,
        JuliaDto? Julia,
        MandelbrotDto? Mandelbrot,
        RecursiveTreeDto? RecursiveTree,
        LSystemDto? LSystem,
        GradientDto? Gradient,
        PresentationDto? Presentation,
        ExplorationDto? Exploration,
        GraphDto? Graph,
        EffectChainDto? Effects);

    private sealed record CanvasDto(int? Width, int? Height, string? Background);
    private sealed record JuliaDto(
        string? CenterX,
        string? CenterY,
        string? Scale,
        string? ConstantReal,
        string? ConstantImaginary,
        int? MaxIterations,
        bool? ForceHighPrecision,
        int? PrecisionDigits);
    private sealed record MandelbrotDto(
        string? CenterX,
        string? CenterY,
        string? Scale,
        int? MaxIterations,
        bool? ForceHighPrecision,
        int? PrecisionDigits);
    private sealed record RecursiveTreeDto(
        int? Depth,
        int? Branches,
        double? BranchAngleDegrees,
        double? LengthDecay,
        double? Randomness,
        double? TrunkLength,
        double? StrokeWidth);
    private sealed record LSystemRuleDto(string? Symbol, string? Replacement);
    private sealed record LSystemDto(
        string? Axiom,
        LSystemRuleDto?[]? Rules,
        int? Iterations,
        double? TurnAngleDegrees,
        double? InitialHeadingDegrees,
        double? StepLength,
        double? LengthDecay,
        double? StrokeWidth,
        double? StrokeWidthDecay);
    private sealed record StrangeAttractorDto(
        int? Formula,
        double? A,
        double? B,
        double? C,
        double? D,
        int? BurnInIterations,
        int? SampleCount,
        double? Exposure,
        double? Gamma,
        bool? GlowEnabled,
        double? GlowSigma,
        double? GlowStrength);
    private sealed record GradientDto(string? Start, string? End, string? Interior);
    private sealed record PresentationDto(
        string? SelectedSection,
        bool? HighQualityPreview,
        string? SelectedLayerId = null);
    private sealed record ExplorationDto(
        double? MutationStrength,
        int? Locks,
        int? Mode,
        int? Generation,
        VariationCandidateDto?[]? Candidates,
        FavoriteVariationDto?[]? Favorites);
    private sealed record VariationCandidateDto(string? Id, int? Number, VariationRecipeDto? Recipe);
    private sealed record FavoriteVariationDto(string? Id, string? Name, VariationRecipeDto? Recipe);
    private sealed record VariationRecipeDto(
        long? Seed,
        int? GeneratorKind,
        JuliaDto? Julia,
        MandelbrotDto? Mandelbrot,
        RecursiveTreeDto? RecursiveTree,
        LSystemDto? LSystem,
        StrangeAttractorDto? StrangeAttractor,
        GradientDto? Gradient);
    private sealed record GraphDto(
        int? Version,
        GraphNodeDto?[]? Nodes,
        GraphConnectionDto?[]? Connections,
        string? OutputNodeId);
    private sealed record GraphNodeDto(string? Id, int? Operation, int? Version);
    private sealed record GraphConnectionDto(
        string? SourceNodeId,
        string? SourcePort,
        string? TargetNodeId,
        string? TargetPort);
    private sealed record EffectChainDto(int? Version, EffectDto?[]? Effects);
    private sealed record EffectDto(
        string? TypeId,
        int? Version,
        bool? IsEnabled,
        double? Brightness = null,
        double? Contrast = null,
        double? Saturation = null,
        double? Threshold = null,
        double? Sigma = null,
        double? Strength = null,
        JsonElement? Payload = null);

    private sealed record SnapshotLayeredDto(
        int? FormatVersion,
        CanvasDto? Canvas,
        PresentationDto? Presentation,
        LayerDto?[]? Layers,
        EffectChainDto? MasterEffects);

    private sealed record LayerDto(
        string? TypeId,
        int? Version,
        string? Id,
        string? Name,
        bool? IsVisible,
        double? Opacity,
        int? BlendMode,
        TransformDto? Transform,
        MaskDto? Mask,
        FractalDto? Fractal,
        LayerDto?[]? Children,
        JsonElement? Payload);

    private sealed record TransformDto(
        double? PositionXPercent,
        double? PositionYPercent,
        double? ScalePercent,
        double? RotationDegrees,
        double? AnchorXPercent,
        double? AnchorYPercent);

    private sealed record MaskDto(string? SourceLayerId, double? Threshold, double? Softness, bool? IsInverted);

    private sealed record FractalDto(
        long? Seed,
        int? GeneratorKind,
        JuliaDto? Julia,
        MandelbrotDto? Mandelbrot,
        RecursiveTreeDto? RecursiveTree,
        LSystemDto? LSystem,
        StrangeAttractorDto? StrangeAttractor,
        GradientDto? Gradient,
        ExplorationDto? Exploration);

    private sealed record LegacySnapshotDto(
        int? FormatVersion,
        long? Seed,
        CanvasDto? Canvas,
        LegacyJuliaDto? Julia,
        GradientDto? Gradient,
        PresentationDto? Presentation);

    private sealed record LegacyJuliaDto(
        double? CenterX,
        double? CenterY,
        double? Scale,
        double? ConstantReal,
        double? ConstantImaginary,
        int? MaxIterations);

    private static LayerDto EncodeLayer(ArtworkLayerDefinition layer)
    {
        var transform = new TransformDto(
            layer.Transform.PositionXPercent,
            layer.Transform.PositionYPercent,
            layer.Transform.ScalePercent,
            layer.Transform.RotationDegrees,
            layer.Transform.AnchorXPercent,
            layer.Transform.AnchorYPercent);
        var mask = layer.Mask is null
            ? null
            : new MaskDto(layer.Mask.SourceLayerId, layer.Mask.Threshold, layer.Mask.Softness, layer.Mask.IsInverted);
        return layer switch
        {
            FractalLayerDefinition fractal => new LayerDto(
                "fractal", 1, fractal.Id, fractal.Name, fractal.IsVisible, fractal.Opacity,
                (int)fractal.BlendMode, transform, mask,
                new FractalDto(
                    fractal.Seed,
                    (int)fractal.GeneratorKind,
                    new JuliaDto(fractal.Julia.CenterX, fractal.Julia.CenterY, fractal.Julia.Scale,
                        fractal.Julia.ConstantReal, fractal.Julia.ConstantImaginary, fractal.Julia.MaxIterations,
                        fractal.Julia.ForceHighPrecision, fractal.Julia.PrecisionDigits),
                    EncodeMandelbrot(fractal.Mandelbrot),
                    EncodeRecursiveTree(fractal.RecursiveTree),
                    EncodeLSystem(fractal.LSystem),
                    EncodeStrangeAttractor(fractal.StrangeAttractor),
                    new GradientDto(fractal.Gradient.Start.ToHex(), fractal.Gradient.End.ToHex(), fractal.Gradient.Interior.ToHex()),
                    EncodeExploration(fractal.Exploration)),
                null,
                null),
            LayerGroupDefinition group => new LayerDto(
                "group", 1, group.Id, group.Name, group.IsVisible, group.Opacity,
                (int)group.BlendMode, transform, mask, null,
                group.Children.Select(child => EncodeLayer(child)).ToArray(), null),
            UnavailableLayerDefinition unavailable => new LayerDto(
                unavailable.TypeId, unavailable.Version, unavailable.Id, unavailable.Name,
                unavailable.IsVisible, unavailable.Opacity, (int)unavailable.BlendMode,
                transform, mask, null, null, ParseOpaque(unavailable.OpaquePayload)),
            _ => throw new NotSupportedException($"不能编码图层类型 {layer.GetType().Name}。")
        };
    }

    private static ArtworkLayerDefinition DecodeLayer(LayerDto dto, int sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(dto.TypeId) || dto.Version is null || string.IsNullOrWhiteSpace(dto.Id) ||
            string.IsNullOrWhiteSpace(dto.Name) || dto.IsVisible is null || dto.Opacity is null ||
            dto.BlendMode is null || dto.Transform is null)
        {
            throw new InvalidDataException("v7 图层缺少类型、版本、身份、名称或合成字段。");
        }

        var transform = DecodeTransform(dto.Transform);
        var mask = DecodeMask(dto.Mask);
        if (dto.TypeId == "fractal" && dto.Version == 1)
        {
            var fractal = dto.Fractal;
            if (fractal?.Seed is null || fractal.GeneratorKind is null || fractal.Julia is null ||
                fractal.Mandelbrot is null || fractal.RecursiveTree is null || fractal.LSystem is null ||
                sourceVersion >= 8 && !HasAllFields(fractal.StrangeAttractor) ||
                fractal.Gradient is null || fractal.Exploration is null ||
                string.IsNullOrWhiteSpace(fractal.Julia.CenterX) || string.IsNullOrWhiteSpace(fractal.Julia.CenterY) ||
                string.IsNullOrWhiteSpace(fractal.Julia.Scale) || string.IsNullOrWhiteSpace(fractal.Julia.ConstantReal) ||
                string.IsNullOrWhiteSpace(fractal.Julia.ConstantImaginary) || fractal.Julia.MaxIterations is null ||
                fractal.Julia.ForceHighPrecision is null || fractal.Julia.PrecisionDigits is null ||
                !HasAllFields(fractal.Mandelbrot) || !HasAllFields(fractal.RecursiveTree) ||
                !HasAllFields(fractal.LSystem) ||
                !RgbaColor.TryParse(fractal.Gradient.Start, out var start) ||
                !RgbaColor.TryParse(fractal.Gradient.End, out var end) ||
                !RgbaColor.TryParse(fractal.Gradient.Interior, out var interior))
            {
                throw new InvalidDataException($"分形图层 {dto.Name} 包含缺失或非法的生成器字段。");
            }

            return new FractalLayerDefinition(
                dto.Id,
                dto.Name,
                dto.IsVisible.Value,
                dto.Opacity.Value,
                (LayerBlendMode)dto.BlendMode.Value,
                transform,
                mask,
                fractal.Seed.Value,
                (FractalGeneratorKind)fractal.GeneratorKind.Value,
                new JuliaDefinition(
                    fractal.Julia.CenterX,
                    fractal.Julia.CenterY,
                    fractal.Julia.Scale,
                    fractal.Julia.ConstantReal,
                    fractal.Julia.ConstantImaginary,
                    fractal.Julia.MaxIterations.Value,
                    fractal.Julia.ForceHighPrecision.Value,
                    fractal.Julia.PrecisionDigits.Value),
                DecodeMandelbrot(fractal.Mandelbrot),
                DecodeRecursiveTree(fractal.RecursiveTree),
                DecodeLSystem(fractal.LSystem),
                sourceVersion >= 8
                    ? DecodeStrangeAttractor(fractal.StrangeAttractor!)
                    : ArtworkDefinition.CreateDefaultAttractor(),
                new GradientDefinition(start, end, interior),
                DecodeExploration(fractal.Exploration, sourceVersion));
        }

        if (dto.TypeId == "group" && dto.Version == 1)
        {
            if (dto.Children is null)
            {
                throw new InvalidDataException($"分组 {dto.Name} 缺少子图层集合。");
            }

            var children = dto.Children.Select(child => DecodeLayer(child ??
                throw new InvalidDataException($"分组 {dto.Name} 包含 null 子项。"), sourceVersion)).ToArray();
            if (children.Any(child => child is LayerGroupDefinition))
            {
                throw new InvalidDataException($"分组 {dto.Name} 不能嵌套分组。");
            }

            return new LayerGroupDefinition(
                dto.Id, dto.Name, dto.IsVisible.Value, dto.Opacity.Value,
                (LayerBlendMode)dto.BlendMode.Value, transform, mask,
                children);
        }

        if (dto.Payload is null)
        {
            throw new InvalidDataException($"未知图层 {dto.TypeId} v{dto.Version} 缺少需保留的 payload。");
        }

        return new UnavailableLayerDefinition(
            dto.Id, dto.Name, dto.IsVisible.Value, dto.Opacity.Value,
            (LayerBlendMode)dto.BlendMode.Value, transform, mask,
            dto.TypeId, dto.Version.Value, dto.Payload.Value.GetRawText());
    }

    private static LayerTransformDefinition DecodeTransform(TransformDto dto)
    {
        if (dto.PositionXPercent is null || dto.PositionYPercent is null || dto.ScalePercent is null ||
            dto.RotationDegrees is null || dto.AnchorXPercent is null || dto.AnchorYPercent is null)
        {
            throw new InvalidDataException("图层变换包含缺失字段。");
        }

        return new LayerTransformDefinition(
            dto.PositionXPercent.Value, dto.PositionYPercent.Value, dto.ScalePercent.Value,
            dto.RotationDegrees.Value, dto.AnchorXPercent.Value, dto.AnchorYPercent.Value);
    }

    private static ScalarMaskDefinition? DecodeMask(MaskDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.SourceLayerId) || dto.Threshold is null ||
            dto.Softness is null || dto.IsInverted is null)
        {
            throw new InvalidDataException("图层遮罩包含缺失字段。");
        }

        return new ScalarMaskDefinition(
            dto.SourceLayerId, dto.Threshold.Value, dto.Softness.Value, dto.IsInverted.Value);
    }

    private static EffectChainDto EncodeEffects(EffectChainDefinition effects) => new(
        effects.Version,
        effects.Effects.Select(effect => effect switch
        {
            ToneEffectDefinition tone => new EffectDto(
                tone.TypeId, tone.Version, tone.IsEnabled,
                Brightness: tone.Brightness, Contrast: tone.Contrast, Saturation: tone.Saturation),
            BloomEffectDefinition bloom => new EffectDto(
                bloom.TypeId, bloom.Version, bloom.IsEnabled,
                Threshold: bloom.Threshold, Sigma: bloom.Sigma, Strength: bloom.Strength),
            UnavailableEffectDefinition unavailable => new EffectDto(
                unavailable.TypeId, unavailable.Version, unavailable.IsEnabled,
                Payload: ParseOpaque(unavailable.OpaquePayload)),
            _ => throw new NotSupportedException($"不能编码效果类型 {effect.GetType().Name}。")
        }).ToArray());

    private static EffectChainDefinition DecodeMasterEffects(EffectChainDto dto)
    {
        if (dto.Version is null || dto.Effects is null)
        {
            throw new InvalidDataException("Master Effects 缺少版本或效果集合。");
        }

        var effects = dto.Effects.Select(item =>
        {
            if (item?.TypeId is null || item.Version is null || item.IsEnabled is null)
            {
                throw new InvalidDataException("Master Effect 包含缺失字段。");
            }

            return item switch
            {
                { TypeId: "tone", Version: 1, Brightness: not null, Contrast: not null, Saturation: not null } =>
                    (ArtworkEffectDefinition)new ToneEffectDefinition(
                        item.IsEnabled.Value, item.Brightness.Value, item.Contrast.Value, item.Saturation.Value),
                { TypeId: "bloom", Version: 1, Threshold: not null, Sigma: not null, Strength: not null } =>
                    new BloomEffectDefinition(
                        item.IsEnabled.Value, item.Threshold.Value, item.Sigma.Value, item.Strength.Value),
                { Payload: not null } => new UnavailableEffectDefinition(
                    item.TypeId, item.Version.Value, item.IsEnabled.Value, item.Payload.Value.GetRawText()),
                _ => throw new InvalidDataException($"未知效果 {item.TypeId} v{item.Version} 缺少需保留的 payload。")
            };
        }).ToArray();
        return new EffectChainDefinition(dto.Version.Value, effects);
    }

    private static JsonElement ParseOpaque(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("不可用能力的原始 payload 不是合法 JSON。", exception);
        }
    }

    private static ExplorationDto EncodeExploration(ArtworkExplorationDefinition exploration) => new(
        exploration.MutationStrength,
        (int)exploration.Locks,
        (int)exploration.Mode,
        exploration.Generation,
        exploration.Candidates.Select(candidate => new VariationCandidateDto(
            candidate.Id,
            candidate.Number,
            EncodeRecipe(candidate.Recipe))).ToArray(),
        exploration.Favorites.Select(favorite => new FavoriteVariationDto(
            favorite.Id,
            favorite.Name,
            EncodeRecipe(favorite.Recipe))).ToArray());

    private static VariationRecipeDto EncodeRecipe(VariationRecipeDefinition recipe) => new(
        recipe.Seed,
        (int)recipe.GeneratorKind,
        new JuliaDto(recipe.Julia.CenterX, recipe.Julia.CenterY, recipe.Julia.Scale,
            recipe.Julia.ConstantReal, recipe.Julia.ConstantImaginary, recipe.Julia.MaxIterations,
            recipe.Julia.ForceHighPrecision, recipe.Julia.PrecisionDigits),
        EncodeMandelbrot(recipe.Mandelbrot),
        EncodeRecursiveTree(recipe.RecursiveTree),
        EncodeLSystem(recipe.LSystem),
        EncodeStrangeAttractor(recipe.StrangeAttractor),
        new GradientDto(recipe.Gradient.Start.ToHex(), recipe.Gradient.End.ToHex(), recipe.Gradient.Interior.ToHex()));

    private static ArtworkExplorationDefinition DecodeExploration(ExplorationDto dto, int sourceVersion)
    {
        if (dto.MutationStrength is null || dto.Locks is null || dto.Mode is null || dto.Generation is null ||
            dto.Candidates is null || dto.Favorites is null)
        {
            throw new InvalidDataException("探索状态包含缺失字段。");
        }

        var candidates = dto.Candidates.Select(candidate =>
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Id) || candidate.Number is null || candidate.Recipe is null)
            {
                throw new InvalidDataException("变体候选包含缺失字段。");
            }

            return new VariationCandidateDefinition(candidate.Id, candidate.Number.Value, DecodeRecipe(candidate.Recipe, sourceVersion));
        }).ToArray();
        var favorites = dto.Favorites.Select(favorite =>
        {
            if (favorite is null || string.IsNullOrWhiteSpace(favorite.Id) || string.IsNullOrWhiteSpace(favorite.Name) || favorite.Recipe is null)
            {
                throw new InvalidDataException("收藏变体包含缺失字段。");
            }

            return new FavoriteVariationDefinition(favorite.Id, favorite.Name, DecodeRecipe(favorite.Recipe, sourceVersion));
        }).ToArray();

        return new ArtworkExplorationDefinition(
            dto.MutationStrength.Value,
            (VariationLockGroups)dto.Locks.Value,
            (VariationMode)dto.Mode.Value,
            dto.Generation.Value,
            candidates,
            favorites);
    }

    private static VariationRecipeDefinition DecodeRecipe(VariationRecipeDto dto, int sourceVersion)
    {
        var isVersion3 = sourceVersion == 3;
        var supportsVersion5Fields = sourceVersion >= 5;
        if (dto.Seed is null || dto.Julia is null || dto.Gradient is null ||
            string.IsNullOrWhiteSpace(dto.Julia.CenterX) || string.IsNullOrWhiteSpace(dto.Julia.CenterY) ||
            string.IsNullOrWhiteSpace(dto.Julia.Scale) || string.IsNullOrWhiteSpace(dto.Julia.ConstantReal) ||
            string.IsNullOrWhiteSpace(dto.Julia.ConstantImaginary) || dto.Julia.MaxIterations is null ||
            dto.Julia.ForceHighPrecision is null || dto.Julia.PrecisionDigits is null ||
            (!isVersion3 && (dto.GeneratorKind is null || dto.RecursiveTree is null || !HasAllFields(dto.RecursiveTree))) ||
            (supportsVersion5Fields && (dto.Mandelbrot is null || !HasAllFields(dto.Mandelbrot) ||
                dto.LSystem is null || !HasAllFields(dto.LSystem))) ||
            (sourceVersion >= 8 && !HasAllFields(dto.StrangeAttractor)) ||
            !RgbaColor.TryParse(dto.Gradient.Start, out var start) ||
            !RgbaColor.TryParse(dto.Gradient.End, out var end) ||
            !RgbaColor.TryParse(dto.Gradient.Interior, out var interior))
        {
            throw new InvalidDataException("候选渲染配方包含缺失或非法字段。");
        }

        return new VariationRecipeDefinition(
            dto.Seed.Value,
            isVersion3 ? FractalGeneratorKind.Julia : (FractalGeneratorKind)dto.GeneratorKind!.Value,
            new JuliaDefinition(dto.Julia.CenterX, dto.Julia.CenterY, dto.Julia.Scale,
                dto.Julia.ConstantReal, dto.Julia.ConstantImaginary, dto.Julia.MaxIterations.Value,
                dto.Julia.ForceHighPrecision.Value, dto.Julia.PrecisionDigits.Value),
            supportsVersion5Fields ? DecodeMandelbrot(dto.Mandelbrot!) : ArtworkDefinition.CreateDefault().Mandelbrot,
            isVersion3 ? ArtworkDefinition.CreateDefault().RecursiveTree : DecodeRecursiveTree(dto.RecursiveTree!),
            supportsVersion5Fields ? DecodeLSystem(dto.LSystem!) : ArtworkDefinition.CreateDefault().LSystem,
            sourceVersion >= 8
                ? DecodeStrangeAttractor(dto.StrangeAttractor!)
                : ArtworkDefinition.CreateDefaultAttractor(),
            new GradientDefinition(start, end, interior));
    }

    private static MandelbrotDto EncodeMandelbrot(MandelbrotDefinition mandelbrot) => new(
        mandelbrot.CenterX,
        mandelbrot.CenterY,
        mandelbrot.Scale,
        mandelbrot.MaxIterations,
        mandelbrot.ForceHighPrecision,
        mandelbrot.PrecisionDigits);

    private static bool HasAllFields(MandelbrotDto mandelbrot) =>
        !string.IsNullOrWhiteSpace(mandelbrot.CenterX) &&
        !string.IsNullOrWhiteSpace(mandelbrot.CenterY) &&
        !string.IsNullOrWhiteSpace(mandelbrot.Scale) &&
        mandelbrot.MaxIterations is not null && mandelbrot.ForceHighPrecision is not null &&
        mandelbrot.PrecisionDigits is not null;

    private static MandelbrotDefinition DecodeMandelbrot(MandelbrotDto mandelbrot) => new(
        mandelbrot.CenterX!,
        mandelbrot.CenterY!,
        mandelbrot.Scale!,
        mandelbrot.MaxIterations!.Value,
        mandelbrot.ForceHighPrecision!.Value,
        mandelbrot.PrecisionDigits!.Value);

    private static RecursiveTreeDto EncodeRecursiveTree(RecursiveTreeDefinition tree) => new(
        tree.Depth,
        tree.Branches,
        tree.BranchAngleDegrees,
        tree.LengthDecay,
        tree.Randomness,
        tree.TrunkLength,
        tree.StrokeWidth);

    private static bool HasAllFields(RecursiveTreeDto tree) =>
        tree.Depth is not null && tree.Branches is not null && tree.BranchAngleDegrees is not null &&
        tree.LengthDecay is not null && tree.Randomness is not null && tree.TrunkLength is not null &&
        tree.StrokeWidth is not null;

    private static RecursiveTreeDefinition DecodeRecursiveTree(RecursiveTreeDto tree) => new(
        tree.Depth!.Value,
        tree.Branches!.Value,
        tree.BranchAngleDegrees!.Value,
        tree.LengthDecay!.Value,
        tree.Randomness!.Value,
        tree.TrunkLength!.Value,
        tree.StrokeWidth!.Value);

    private static LSystemDto EncodeLSystem(LSystemDefinition lSystem) => new(
        lSystem.Axiom,
        lSystem.Rules.Select(rule => new LSystemRuleDto(rule.Symbol.ToString(), rule.Replacement)).ToArray(),
        lSystem.Iterations,
        lSystem.TurnAngleDegrees,
        lSystem.InitialHeadingDegrees,
        lSystem.StepLength,
        lSystem.LengthDecay,
        lSystem.StrokeWidth,
        lSystem.StrokeWidthDecay);

    private static bool HasAllFields(LSystemDto lSystem) =>
        lSystem.Axiom is not null && lSystem.Rules is not null &&
        lSystem.Rules.All(rule => rule is not null && rule.Symbol?.Length == 1 && rule.Replacement is not null) &&
        lSystem.Iterations is not null && lSystem.TurnAngleDegrees is not null &&
        lSystem.InitialHeadingDegrees is not null && lSystem.StepLength is not null &&
        lSystem.LengthDecay is not null && lSystem.StrokeWidth is not null && lSystem.StrokeWidthDecay is not null;

    private static LSystemDefinition DecodeLSystem(LSystemDto lSystem) => new(
        lSystem.Axiom!,
        lSystem.Rules!.Select(rule => new LSystemRuleDefinition(rule!.Symbol![0], rule.Replacement!)).ToArray(),
        lSystem.Iterations!.Value,
        lSystem.TurnAngleDegrees!.Value,
        lSystem.InitialHeadingDegrees!.Value,
        lSystem.StepLength!.Value,
        lSystem.LengthDecay!.Value,
        lSystem.StrokeWidth!.Value,
        lSystem.StrokeWidthDecay!.Value);

    private static StrangeAttractorDto EncodeStrangeAttractor(StrangeAttractorDefinition definition) => new(
        (int)definition.Formula,
        definition.A,
        definition.B,
        definition.C,
        definition.D,
        definition.BurnInIterations,
        definition.SampleCount,
        definition.Exposure,
        definition.Gamma,
        definition.GlowEnabled,
        definition.GlowSigma,
        definition.GlowStrength);

    private static bool HasAllFields(StrangeAttractorDto? definition) =>
        definition?.Formula is not null && definition.A is not null && definition.B is not null &&
        definition.C is not null && definition.D is not null && definition.BurnInIterations is not null &&
        definition.SampleCount is not null && definition.Exposure is not null && definition.Gamma is not null &&
        definition.GlowEnabled is not null && definition.GlowSigma is not null && definition.GlowStrength is not null;

    private static StrangeAttractorDefinition DecodeStrangeAttractor(StrangeAttractorDto definition) => new(
        (AttractorFormula)definition.Formula!.Value,
        definition.A!.Value,
        definition.B!.Value,
        definition.C!.Value,
        definition.D!.Value,
        definition.BurnInIterations!.Value,
        definition.SampleCount!.Value,
        definition.Exposure!.Value,
        definition.Gamma!.Value,
        definition.GlowEnabled!.Value,
        definition.GlowSigma!.Value,
        definition.GlowStrength!.Value);

    private static GraphDto EncodeGraph(ArtworkGraphDefinition graph) => new(
        graph.Version,
        graph.Nodes.Select(node => new GraphNodeDto(node.Id, (int)node.Operation, node.Version)).ToArray(),
        graph.Connections.Select(connection => new GraphConnectionDto(
            connection.SourceNodeId,
            connection.SourcePort,
            connection.TargetNodeId,
            connection.TargetPort)).ToArray(),
        graph.OutputNodeId);

    private static ArtworkGraphDefinition DecodeGraph(GraphDto graph)
    {
        if (graph.Version is null || graph.Nodes is null || graph.Connections is null ||
            string.IsNullOrWhiteSpace(graph.OutputNodeId) ||
            graph.Nodes.Any(node => node is null || string.IsNullOrWhiteSpace(node.Id) ||
                node.Operation is null || node.Version is null) ||
            graph.Connections.Any(connection => connection is null ||
                string.IsNullOrWhiteSpace(connection.SourceNodeId) ||
                string.IsNullOrWhiteSpace(connection.SourcePort) ||
                string.IsNullOrWhiteSpace(connection.TargetNodeId) ||
                string.IsNullOrWhiteSpace(connection.TargetPort)))
        {
            throw new InvalidDataException("创作图包含缺失的版本、节点、端口或连接字段。");
        }

        return new ArtworkGraphDefinition(
            graph.Version.Value,
            graph.Nodes.Select(node => new ArtworkGraphNodeDefinition(
                node!.Id!,
                (ArtworkGraphOperation)node.Operation!.Value,
                node.Version!.Value)),
            graph.Connections.Select(connection => new ArtworkGraphConnectionDefinition(
                connection!.SourceNodeId!,
                connection.SourcePort!,
                connection.TargetNodeId!,
                connection.TargetPort!)),
            graph.OutputNodeId);
    }

    private static EffectChainDefinition DecodeEffects(EffectChainDto effects)
    {
        if (effects.Version is null || effects.Effects is null)
        {
            throw new InvalidDataException("效果链缺少版本或效果集合。");
        }

        if (effects.Effects.Length > 0)
        {
            var first = effects.Effects[0];
            var typeId = first?.TypeId;
            throw new NotSupportedException(string.IsNullOrWhiteSpace(typeId)
                ? "G0006 不支持包含未知效果的作品。"
                : $"G0006 不支持效果类型 {typeId}。");
        }

        return new EffectChainDefinition(effects.Version.Value, []);
    }
}
