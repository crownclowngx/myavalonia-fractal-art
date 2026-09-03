using System.Text.Json;
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
                artwork.Julia.MaxIterations),
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

        SnapshotDto? dto;
        try
        {
            dto = content.Payload.Deserialize<SnapshotDto>(JsonOptions);
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
        if (canvas.Width is null || canvas.Height is null || julia.CenterX is null || julia.CenterY is null ||
            julia.Scale is null || julia.ConstantReal is null || julia.ConstantImaginary is null ||
            julia.MaxIterations is null || presentation.HighQualityPreview is null ||
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
                julia.CenterX.Value,
                julia.CenterY.Value,
                julia.Scale.Value,
                julia.ConstantReal.Value,
                julia.ConstantImaginary.Value,
                julia.MaxIterations.Value),
            new GradientDefinition(start, end, interior),
            new ArtworkPresentationDefinition(
                presentation.SelectedSection,
                presentation.HighQualityPreview.Value));
        validator.Validate(artwork);
        return artwork;
    }

    private sealed record SnapshotDto(
        int? FormatVersion,
        long? Seed,
        CanvasDto? Canvas,
        JuliaDto? Julia,
        GradientDto? Gradient,
        PresentationDto? Presentation);

    private sealed record CanvasDto(int? Width, int? Height, string? Background);
    private sealed record JuliaDto(
        double? CenterX,
        double? CenterY,
        double? Scale,
        double? ConstantReal,
        double? ConstantImaginary,
        int? MaxIterations);
    private sealed record GradientDto(string? Start, string? End, string? Interior);
    private sealed record PresentationDto(string? SelectedSection, bool? HighQualityPreview);
}
