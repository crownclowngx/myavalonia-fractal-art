using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FractalArtPlugin.Features.Artwork;

/// <summary>
/// 仅供当前 Document 呈现的候选项。持久化身份和配方来自领域对象，Bitmap 与收藏高亮属于可丢弃的 UI 状态。
/// </summary>
public sealed partial class VariationCandidateItem(
    VariationCandidateDefinition definition,
    Bitmap? previewImage,
    bool isFavorite) : ObservableObject, IDisposable
{
    public VariationCandidateDefinition Definition { get; } = definition;
    public string Title => $"变体 {Definition.Number}";
    public Bitmap? PreviewImage { get; } = previewImage;
    [ObservableProperty] private bool _isFavorite = isFavorite;

    public void Dispose() => PreviewImage?.Dispose();
}
