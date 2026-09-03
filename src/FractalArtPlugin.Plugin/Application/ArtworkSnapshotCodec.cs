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
        var dto = new SnapshotDto(
            artwork.FormatVersion,
            artwork.Seed,
            new CanvasDto(artwork.Canvas.Width, artwork.Canvas.Height, artwork.Canvas.Background.ToHex()),
            (int)artwork.GeneratorKind,
            new JuliaDto(
                artwork.Julia.CenterX,
                artwork.Julia.CenterY,
                artwork.Julia.Scale,
                artwork.Julia.ConstantReal,
                artwork.Julia.ConstantImaginary,
                artwork.Julia.MaxIterations,
                artwork.Julia.ForceHighPrecision,
                artwork.Julia.PrecisionDigits),
            EncodeRecursiveTree(artwork.RecursiveTree),
            new GradientDto(
                artwork.Gradient.Start.ToHex(),
                artwork.Gradient.End.ToHex(),
                artwork.Gradient.Interior.ToHex()),
            new PresentationDto(
                artwork.Presentation.SelectedSection,
                artwork.Presentation.HighQualityPreview),
            EncodeExploration(artwork.Exploration));
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
            ArtworkDefinition.CurrentFormatVersion => DecodeVersion4(content.Payload),
            _ => throw new NotSupportedException($"不支持作品格式版本 {formatVersion}。")
        };
    }

    private ArtworkDefinition DecodeVersion4(JsonElement payload) => DecodeVersionedSnapshot(payload, false);

    /// <summary>
    /// G0004 的 v3 文件只有 Julia 配方。迁移明确选择 Julia，并为尚未使用的递归树补入安全默认值；
    /// 候选和收藏也按相同规则升级，保证旧九宫格恢复后仍指向原来的 Julia 画面。
    /// </summary>
    private ArtworkDefinition DecodeVersion3(JsonElement payload) => DecodeVersionedSnapshot(payload, true);

    private ArtworkDefinition DecodeVersionedSnapshot(JsonElement payload, bool isVersion3)
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
        var recursiveTree = isVersion3 ? null : dto.RecursiveTree;
        var gradient = dto.Gradient;
        var presentation = dto.Presentation;
        if (canvas.Width is null || canvas.Height is null || string.IsNullOrWhiteSpace(julia.CenterX) ||
            string.IsNullOrWhiteSpace(julia.CenterY) || string.IsNullOrWhiteSpace(julia.Scale) ||
            string.IsNullOrWhiteSpace(julia.ConstantReal) || string.IsNullOrWhiteSpace(julia.ConstantImaginary) ||
            julia.MaxIterations is null || julia.ForceHighPrecision is null || julia.PrecisionDigits is null ||
            (!isVersion3 && (dto.GeneratorKind is null || recursiveTree is null || !HasAllFields(recursiveTree))) ||
            presentation.HighQualityPreview is null ||
            !RgbaColor.TryParse(canvas.Background, out var background) ||
            !RgbaColor.TryParse(gradient.Start, out var start) ||
            !RgbaColor.TryParse(gradient.End, out var end) ||
            !RgbaColor.TryParse(gradient.Interior, out var interior) ||
            string.IsNullOrWhiteSpace(presentation.SelectedSection))
        {
            throw new InvalidDataException("作品包含缺失或非法的画布、Julia、渐变或呈现字段。");
        }

        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            dto.Seed.Value,
            new CanvasDefinition(canvas.Width.Value, canvas.Height.Value, background),
            isVersion3 ? FractalGeneratorKind.Julia : (FractalGeneratorKind)dto.GeneratorKind!.Value,
            new JuliaDefinition(
                julia.CenterX,
                julia.CenterY,
                julia.Scale,
                julia.ConstantReal,
                julia.ConstantImaginary,
                julia.MaxIterations.Value,
                julia.ForceHighPrecision.Value,
                julia.PrecisionDigits.Value),
            isVersion3 ? ArtworkDefinition.CreateDefault().RecursiveTree : DecodeRecursiveTree(recursiveTree!),
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                presentation.SelectedSection,
                presentation.HighQualityPreview.Value),
            DecodeExploration(dto.Exploration, isVersion3));
        validator.Validate(artwork);
        return artwork;
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
        return migrated;
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
            ArtworkDefinition.CreateDefault().RecursiveTree,
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(dto.Presentation.SelectedSection, dto.Presentation.HighQualityPreview.Value),
            ArtworkExplorationDefinition.CreateDefault());
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
            ArtworkDefinition.CreateDefault().RecursiveTree,
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                dto.Presentation.SelectedSection,
                dto.Presentation.HighQualityPreview.Value),
            ArtworkExplorationDefinition.CreateDefault());
        validator.Validate(migrated);
        return migrated;
    }

    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record SnapshotDto(
        int? FormatVersion,
        long? Seed,
        CanvasDto? Canvas,
        int? GeneratorKind,
        JuliaDto? Julia,
        RecursiveTreeDto? RecursiveTree,
        GradientDto? Gradient,
        PresentationDto? Presentation,
        ExplorationDto? Exploration);

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
    private sealed record RecursiveTreeDto(
        int? Depth,
        int? Branches,
        double? BranchAngleDegrees,
        double? LengthDecay,
        double? Randomness,
        double? TrunkLength,
        double? StrokeWidth);
    private sealed record GradientDto(string? Start, string? End, string? Interior);
    private sealed record PresentationDto(string? SelectedSection, bool? HighQualityPreview);
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
        RecursiveTreeDto? RecursiveTree,
        GradientDto? Gradient);

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
        EncodeRecursiveTree(recipe.RecursiveTree),
        new GradientDto(recipe.Gradient.Start.ToHex(), recipe.Gradient.End.ToHex(), recipe.Gradient.Interior.ToHex()));

    private static ArtworkExplorationDefinition DecodeExploration(ExplorationDto dto, bool isVersion3)
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

            return new VariationCandidateDefinition(candidate.Id, candidate.Number.Value, DecodeRecipe(candidate.Recipe, isVersion3));
        }).ToArray();
        var favorites = dto.Favorites.Select(favorite =>
        {
            if (favorite is null || string.IsNullOrWhiteSpace(favorite.Id) || string.IsNullOrWhiteSpace(favorite.Name) || favorite.Recipe is null)
            {
                throw new InvalidDataException("收藏变体包含缺失字段。");
            }

            return new FavoriteVariationDefinition(favorite.Id, favorite.Name, DecodeRecipe(favorite.Recipe, isVersion3));
        }).ToArray();

        return new ArtworkExplorationDefinition(
            dto.MutationStrength.Value,
            (VariationLockGroups)dto.Locks.Value,
            (VariationMode)dto.Mode.Value,
            dto.Generation.Value,
            candidates,
            favorites);
    }

    private static VariationRecipeDefinition DecodeRecipe(VariationRecipeDto dto, bool isVersion3)
    {
        if (dto.Seed is null || dto.Julia is null || dto.Gradient is null ||
            string.IsNullOrWhiteSpace(dto.Julia.CenterX) || string.IsNullOrWhiteSpace(dto.Julia.CenterY) ||
            string.IsNullOrWhiteSpace(dto.Julia.Scale) || string.IsNullOrWhiteSpace(dto.Julia.ConstantReal) ||
            string.IsNullOrWhiteSpace(dto.Julia.ConstantImaginary) || dto.Julia.MaxIterations is null ||
            dto.Julia.ForceHighPrecision is null || dto.Julia.PrecisionDigits is null ||
            (!isVersion3 && (dto.GeneratorKind is null || dto.RecursiveTree is null || !HasAllFields(dto.RecursiveTree))) ||
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
            isVersion3 ? ArtworkDefinition.CreateDefault().RecursiveTree : DecodeRecursiveTree(dto.RecursiveTree!),
            new GradientDefinition(start, end, interior));
    }

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
}
