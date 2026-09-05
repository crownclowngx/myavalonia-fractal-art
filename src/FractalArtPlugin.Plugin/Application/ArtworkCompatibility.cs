namespace FractalArtPlugin.Application;

public enum ArtworkCompatibilityIssueKind
{
    Layer,
    Effect
}

/// <summary>
/// 当前运行环境无法解释、但作品文件仍能无损保存的一项能力。
/// Key 只服务当前报告中的显式修复命令，不写入作品，也不充当插件能力的永久身份。
/// </summary>
public sealed record ArtworkCompatibilityIssue(
    string Key,
    ArtworkCompatibilityIssueKind Kind,
    string Name,
    string TypeId,
    int Version,
    string Location)
{
    public string KindName => Kind == ArtworkCompatibilityIssueKind.Layer ? "图层" : "效果";
    public string Description => $"{KindName}“{Name}” · {TypeId} v{Version} · {Location}";
}

public sealed record ArtworkCompatibilityReport
{
    public ArtworkCompatibilityReport(IEnumerable<ArtworkCompatibilityIssue> issues) =>
        Issues = Array.AsReadOnly(issues.ToArray());

    public static ArtworkCompatibilityReport Compatible { get; } = new([]);
    public IReadOnlyList<ArtworkCompatibilityIssue> Issues { get; }
    public bool CanRender => Issues.Count == 0;
}

public interface IArtworkCompatibilityService
{
    ArtworkCompatibilityReport Inspect(ArtworkDefinition artwork);
    ArtworkDefinition Remove(ArtworkDefinition artwork, string issueKey);
}

/// <summary>
/// 把“结构是否合法”和“当前是否具备渲染能力”之外的用户修复意图集中在一个窄服务中。
/// 解码器继续负责无损保留，渲染器继续坚持失败关闭；只有用户明确点击移除时，本服务才替换不可变作品。
/// 这样 Document 不需要认识快照中的未知 JSON 形状，也不会出现自动降级导致的静默画面漂移。
/// </summary>
internal sealed class ArtworkCompatibilityService(IArtworkValidator validator) : IArtworkCompatibilityService
{
    public ArtworkCompatibilityReport Inspect(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var issues = new List<ArtworkCompatibilityIssue>();
        foreach (var layer in artwork.Layers)
        {
            AddLayerIssue(layer, "顶层", issues);
            if (layer is LayerGroupDefinition group)
            {
                foreach (var child in group.Children)
                {
                    AddLayerIssue(child, $"分组“{group.Name}”", issues);
                }
            }
        }

        for (var index = 0; index < artwork.MasterEffects.Effects.Count; index++)
        {
            if (artwork.MasterEffects.Effects[index] is not UnavailableEffectDefinition effect)
            {
                continue;
            }

            issues.Add(new ArtworkCompatibilityIssue(
                $"effect:{index}:{effect.TypeId}:{effect.Version}",
                ArtworkCompatibilityIssueKind.Effect,
                effect.TypeId,
                effect.TypeId,
                effect.Version,
                "Master Effects"));
        }

        return issues.Count == 0
            ? ArtworkCompatibilityReport.Compatible
            : new ArtworkCompatibilityReport(issues.AsReadOnly());
    }

    public ArtworkDefinition Remove(ArtworkDefinition artwork, string issueKey)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            throw new ArgumentException("兼容问题键不能为空。", nameof(issueKey));
        }

        var issue = Inspect(artwork).Issues.SingleOrDefault(candidate => candidate.Key == issueKey)
            ?? throw new InvalidOperationException("该缺失能力已不存在，请刷新后重试。");
        var candidate = issue.Kind switch
        {
            ArtworkCompatibilityIssueKind.Layer => RemoveLayer(artwork, issue),
            ArtworkCompatibilityIssueKind.Effect => RemoveEffect(artwork, issueKey),
            _ => throw new ArgumentOutOfRangeException(nameof(issueKey), "未知兼容问题类型。")
        };

        validator.Validate(candidate);
        return candidate;
    }

    private static void AddLayerIssue(
        ArtworkLayerDefinition layer,
        string location,
        ICollection<ArtworkCompatibilityIssue> issues)
    {
        if (layer is UnavailableLayerDefinition unavailable)
        {
            issues.Add(new ArtworkCompatibilityIssue(
                $"layer:{unavailable.Id}",
                ArtworkCompatibilityIssueKind.Layer,
                unavailable.Name,
                unavailable.TypeId,
                unavailable.Version,
                location));
        }
    }

    private static ArtworkDefinition RemoveLayer(ArtworkDefinition artwork, ArtworkCompatibilityIssue issue)
    {
        var layerId = issue.Key["layer:".Length..];
        var layers = artwork.Layers
            .Where(layer => layer.Id != layerId)
            .Select(layer => layer is LayerGroupDefinition group
                ? group with
                {
                    Children = Array.AsReadOnly(group.Children.Where(child => child.Id != layerId).ToArray())
                }
                : layer)
            .ToArray();
        var selectedId = artwork.Presentation.SelectedLayerId == layerId
            ? Flatten(layers).First().Id
            : artwork.Presentation.SelectedLayerId;
        return artwork with
        {
            Layers = layers,
            Presentation = artwork.Presentation with { SelectedLayerId = selectedId }
        };
    }

    private static ArtworkDefinition RemoveEffect(ArtworkDefinition artwork, string issueKey)
    {
        var indexText = issueKey.Split(':', 4)[1];
        if (!int.TryParse(indexText, out var index) || index < 0 || index >= artwork.MasterEffects.Effects.Count ||
            artwork.MasterEffects.Effects[index] is not UnavailableEffectDefinition)
        {
            throw new InvalidOperationException("该缺失效果已发生变化，请刷新后重试。");
        }

        var effects = artwork.MasterEffects.Effects.Where((_, itemIndex) => itemIndex != index).ToArray();
        return artwork with
        {
            MasterEffects = new EffectChainDefinition(artwork.MasterEffects.Version, effects)
        };
    }

    private static IEnumerable<ArtworkLayerDefinition> Flatten(IEnumerable<ArtworkLayerDefinition> layers) =>
        layers.SelectMany(layer => layer is LayerGroupDefinition group
            ? new[] { layer }.Concat(group.Children)
            : [layer]);
}
