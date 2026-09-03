using System.Text.Json;
using System.Globalization;
using FractalArtPlugin.Domain;
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
            new JuliaDto(
                artwork.Julia.CenterX,
                artwork.Julia.CenterY,
                artwork.Julia.Scale,
                artwork.Julia.ConstantReal,
                artwork.Julia.ConstantImaginary,
                artwork.Julia.MaxIterations,
                artwork.Julia.ForceHighPrecision,
                artwork.Julia.PrecisionDigits),
            new GradientDto(
                artwork.Gradient.Start.ToHex(),
                artwork.Gradient.End.ToHex(),
                artwork.Gradient.Interior.ToHex()),
            new PresentationDto(
                artwork.Presentation.SelectedSection,
                artwork.Presentation.HighQualityPreview));
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
            ArtworkDefinition.CurrentFormatVersion => DecodeVersion2(content.Payload),
            _ => throw new NotSupportedException($"不支持作品格式版本 {formatVersion}。")
        };
    }

    private ArtworkDefinition DecodeVersion2(JsonElement payload)
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
            dto.Gradient is null || dto.Presentation is null)
        {
            throw new InvalidDataException("作品缺少 formatVersion、seed、canvas、julia、gradient 或 presentation。");
        }

        var canvas = dto.Canvas;
        var julia = dto.Julia;
        var gradient = dto.Gradient;
        var presentation = dto.Presentation;
        if (canvas.Width is null || canvas.Height is null || string.IsNullOrWhiteSpace(julia.CenterX) ||
            string.IsNullOrWhiteSpace(julia.CenterY) || string.IsNullOrWhiteSpace(julia.Scale) ||
            string.IsNullOrWhiteSpace(julia.ConstantReal) || string.IsNullOrWhiteSpace(julia.ConstantImaginary) ||
            julia.MaxIterations is null || julia.ForceHighPrecision is null || julia.PrecisionDigits is null ||
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
            dto.FormatVersion.Value,
            dto.Seed.Value,
            new CanvasDefinition(canvas.Width.Value, canvas.Height.Value, background),
            new JuliaDefinition(
                julia.CenterX,
                julia.CenterY,
                julia.Scale,
                julia.ConstantReal,
                julia.ConstantImaginary,
                julia.MaxIterations.Value,
                julia.ForceHighPrecision.Value,
                julia.PrecisionDigits.Value),
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                presentation.SelectedSection,
                presentation.HighQualityPreview.Value));
        validator.Validate(artwork);
        return artwork;
    }

    /// <summary>
    /// 把 G0003 初版的 IEEE 754 数值显式迁移为 round-trip 十进制文本。
    /// 迁移不会声称恢复 double 已经丢失的位数，但此后所有新平移和缩放都由 v2 高精度模型保存。
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
            new JuliaDefinition(
                FormatDouble(dto.Julia.CenterX.Value),
                FormatDouble(dto.Julia.CenterY.Value),
                FormatDouble(dto.Julia.Scale.Value),
                FormatDouble(dto.Julia.ConstantReal.Value),
                FormatDouble(dto.Julia.ConstantImaginary.Value),
                dto.Julia.MaxIterations.Value,
                false,
                96),
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                dto.Presentation.SelectedSection,
                dto.Presentation.HighQualityPreview.Value));
        validator.Validate(migrated);
        return migrated;
    }

    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record SnapshotDto(
        int? FormatVersion,
        long? Seed,
        CanvasDto? Canvas,
        JuliaDto? Julia,
        GradientDto? Gradient,
        PresentationDto? Presentation);

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
    private sealed record GradientDto(string? Start, string? End, string? Interior);
    private sealed record PresentationDto(string? SelectedSection, bool? HighQualityPreview);

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
}
