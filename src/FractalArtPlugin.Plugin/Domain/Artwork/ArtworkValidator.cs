using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Artwork;

public interface IArtworkValidator
{
    void Validate(ArtworkDefinition artwork);
}

public interface IArtworkRenderabilityValidator
{
    void EnsureRenderable(ArtworkDefinition artwork);
}

/// <summary>集中维护作品资源预算；UI 限制、渲染策略与持久化均不能绕过这一领域边界。</summary>
internal sealed class ArtworkValidator : IArtworkValidator, IArtworkRenderabilityValidator
{
    private readonly ILSystemValidator _lSystemValidator;
    private readonly IArtworkGraphValidator _graphValidator;

    public ArtworkValidator(
        ILSystemValidator? lSystemValidator = null,
        IArtworkGraphValidator? graphValidator = null)
    {
        _lSystemValidator = lSystemValidator ?? new LSystemValidator();
        _graphValidator = graphValidator ?? new ArtworkGraphValidator();
    }

    public void Validate(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.FormatVersion != ArtworkDefinition.CurrentFormatVersion)
        {
            throw new NotSupportedException($"不支持作品格式版本 {artwork.FormatVersion}。");
        }

        if (artwork.Canvas.Width is < 64 or > 8192 || artwork.Canvas.Height is < 64 or > 8192 ||
            (long)artwork.Canvas.Width * artwork.Canvas.Height > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("画布尺寸必须位于 64–8192，且总像素不能超过 64M。");
        }

        if (artwork.Layers is null || artwork.Layers.Count == 0)
        {
            throw new InvalidDataException("作品至少需要一个分形图层。");
        }

        ValidateLayers(artwork);
        ValidateMasterEffects(artwork);

        if (string.IsNullOrWhiteSpace(artwork.Presentation.SelectedSection) ||
            artwork.Presentation.SelectedSection.Length > 32)
        {
            throw new InvalidDataException("呈现区域名称不能为空且不能超过 32 个字符。");
        }

        if (artwork.HasLegacyGraphOverride)
        {
            _graphValidator.ValidateAndSort(artwork.Graph, artwork.GeneratorKind, EffectChainDefinition.Empty);
        }

    }

    /// <summary>
    /// 结构合法与当前机器可渲染是两个不同问题。未知能力仍可安全打开、整理和原样保存，
    /// 但任何像素输出都必须在这里失败，避免跳过节点后产生一张看似正常的错误作品。
    /// </summary>
    public void EnsureRenderable(ArtworkDefinition artwork)
    {
        Validate(artwork);
        var missingLayers = artwork.Layers
            .Concat(artwork.Layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children))
            .OfType<UnavailableLayerDefinition>().ToArray();
        var missingEffects = artwork.MasterEffects.Effects.OfType<UnavailableEffectDefinition>().ToArray();
        if (missingLayers.Length == 0 && missingEffects.Length == 0)
        {
            return;
        }

        var identities = missingLayers.Select(layer => $"图层 {layer.Name}（{layer.TypeId} v{layer.Version}）")
            .Concat(missingEffects.Select(effect => $"效果 {effect.TypeId} v{effect.Version}"));
        throw new NotSupportedException($"作品包含当前不可用能力：{string.Join("、", identities)}；已保留配置，但禁止预览或导出。");
    }

    private void ValidateLayers(ArtworkDefinition artwork)
    {
        if (artwork.Layers.Count > 12)
        {
            throw new InvalidDataException("顶层图层与分组总数不能超过 12。");
        }

        var all = artwork.Layers.Concat(artwork.Layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children)).ToArray();
        var fractals = all.OfType<FractalLayerDefinition>().ToArray();
        var groups = artwork.Layers.OfType<LayerGroupDefinition>().ToArray();
        if (fractals.Length is < 1 or > 8 || groups.Length > 4)
        {
            throw new InvalidDataException("作品必须包含 1–8 个分形层，且分组不能超过 4 个。");
        }

        var nestedGroup = groups.SelectMany(group => group.Children).OfType<LayerGroupDefinition>().FirstOrDefault();
        if (nestedGroup is not null)
        {
            throw new InvalidDataException($"分组 {nestedGroup.Name} 不能嵌套在另一个分组中。");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in all)
        {
            ValidateLayerCommon(layer);
            if (!ids.Add(layer.Id))
            {
                throw new InvalidDataException($"图层 ID {layer.Id} 重复。");
            }
        }

        if (!ids.Contains(artwork.Presentation.SelectedLayerId))
        {
            throw new InvalidDataException($"当前选择的图层 {artwork.Presentation.SelectedLayerId} 不存在。");
        }

        foreach (var layer in fractals)
        {
            if (!Enum.IsDefined(layer.GeneratorKind))
            {
                throw new InvalidDataException($"图层 {layer.Name} 的生成器类型非法。");
            }

            ValidateJulia(layer.Julia);
            ValidateMandelbrot(layer.Mandelbrot);
            ValidateRecursiveTree(layer.RecursiveTree);
            _lSystemValidator.Validate(layer.LSystem);
            ValidateExploration(layer.Exploration);
            _graphValidator.ValidateAndSort(
                ArtworkGraphFactory.Create(layer), layer.GeneratorKind, EffectChainDefinition.Empty);
        }

        foreach (var layer in all.Where(layer => layer.Mask is not null))
        {
            var mask = layer.Mask!;
            var source = fractals.FirstOrDefault(candidate => candidate.Id == mask.SourceLayerId);
            if (source is null)
            {
                throw new InvalidDataException($"图层 {layer.Name} 的遮罩源 {mask.SourceLayerId} 不存在。");
            }

            if (source.Id == layer.Id)
            {
                throw new InvalidDataException($"图层 {layer.Name} 不能引用自身作为遮罩。");
            }

            if (source.GeneratorKind is not (FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot))
            {
                throw new InvalidDataException($"图层 {layer.Name} 的遮罩源 {source.Name} 不产生 ScalarField。");
            }

            if (!double.IsFinite(mask.Threshold) || mask.Threshold is < 0 or > 1 ||
                !double.IsFinite(mask.Softness) || mask.Softness is < 0 or > 1)
            {
                throw new InvalidDataException($"图层 {layer.Name} 的遮罩阈值和柔化必须位于 0–1。");
            }
        }

        // 预算按真正会进入执行器的分形层计算：可见根层、可见组中的可见子层，以及这些可见目标引用的遮罩源。
        // 不能只把“组 ID”计作一个分形，也不能让隐藏且无人引用的分支白白占用预算。
        var visibleTargets = artwork.Layers.SelectMany(layer => layer switch
        {
            FractalLayerDefinition fractal when fractal.IsVisible => new ArtworkLayerDefinition[] { fractal },
            LayerGroupDefinition group when group.IsVisible => new ArtworkLayerDefinition[] { group }
                .Concat(group.Children.Where(child => child.IsVisible)),
            UnavailableLayerDefinition unavailable when unavailable.IsVisible => [unavailable],
            _ => []
        }).ToArray();
        var requiredIds = visibleTargets.OfType<FractalLayerDefinition>()
            .Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var sourceId in visibleTargets.Where(layer => layer.Mask is not null)
                     .Select(layer => layer.Mask!.SourceLayerId))
        {
            requiredIds.Add(sourceId);
        }

        var requiredFractals = fractals.Count(layer => requiredIds.Contains(layer.Id));
        var finalWorkPixels = checked((long)artwork.Canvas.Width * artwork.Canvas.Height * requiredFractals);
        if (finalWorkPixels > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("可见图层与遮罩源的最终像素工作量不能超过 64M。");
        }
    }

    private static void ValidateLayerCommon(ArtworkLayerDefinition layer)
    {
        if (!IsValidIdentity(layer.Id) || string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Length > 64)
        {
            throw new InvalidDataException("图层 ID 或名称非法；名称不能超过 64 个字符。");
        }

        if (!Enum.IsDefined(layer.BlendMode) || !double.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1)
        {
            throw new InvalidDataException($"图层 {layer.Name} 的混合模式或不透明度非法。");
        }

        var transform = layer.Transform;
        if (transform is null || !double.IsFinite(transform.PositionXPercent) || transform.PositionXPercent is < -200 or > 200 ||
            !double.IsFinite(transform.PositionYPercent) || transform.PositionYPercent is < -200 or > 200 ||
            !double.IsFinite(transform.ScalePercent) || transform.ScalePercent is < 1 or > 800 ||
            !double.IsFinite(transform.RotationDegrees) || transform.RotationDegrees is < -180 or > 180 ||
            !double.IsFinite(transform.AnchorXPercent) || transform.AnchorXPercent is < 0 or > 100 ||
            !double.IsFinite(transform.AnchorYPercent) || transform.AnchorYPercent is < 0 or > 100)
        {
            throw new InvalidDataException($"图层 {layer.Name} 的位置、缩放、旋转或锚点超出安全范围。");
        }

        if (layer is UnavailableLayerDefinition unavailable &&
            (string.IsNullOrWhiteSpace(unavailable.TypeId) || unavailable.Version <= 0 ||
             string.IsNullOrWhiteSpace(unavailable.OpaquePayload)))
        {
            throw new InvalidDataException($"不可用图层 {layer.Name} 缺少类型、版本或原始配置。");
        }
    }

    private static void ValidateMasterEffects(ArtworkDefinition artwork)
    {
        if (artwork.MasterEffects.Version != EffectChainDefinition.CurrentVersion || artwork.MasterEffects.Effects.Count > 8)
        {
            throw new InvalidDataException("Master Effects 版本非法或效果数量超过 8。");
        }

        foreach (var effect in artwork.MasterEffects.Effects)
        {
            switch (effect)
            {
                case ToneEffectDefinition tone when !double.IsFinite(tone.Brightness) || tone.Brightness is < -1 or > 1 ||
                                                   !double.IsFinite(tone.Contrast) || tone.Contrast is < -1 or > 1 ||
                                                   !double.IsFinite(tone.Saturation) || tone.Saturation is < 0 or > 2:
                    throw new InvalidDataException("色调效果的亮度、对比度或饱和度超出范围。");
                case BloomEffectDefinition bloom when !double.IsFinite(bloom.Threshold) || bloom.Threshold is < 0 or > 1 ||
                                                     !double.IsFinite(bloom.Sigma) || bloom.Sigma is < 0.1 or > 10 ||
                                                     !double.IsFinite(bloom.Strength) || bloom.Strength is < 0 or > 4:
                    throw new InvalidDataException("Bloom 的阈值、Sigma 或强度超出范围。");
                case BloomEffectDefinition { IsEnabled: true } when
                    (long)artwork.Canvas.Width * artwork.Canvas.Height > 16_777_216:
                    throw new InvalidDataException("启用 Bloom 时最终画布不能超过 16,777,216 像素。");
            }
        }

        var known = artwork.MasterEffects.Effects.Where(effect => effect is not UnavailableEffectDefinition).ToArray();
        if (known.Length != known.Select(effect => effect.TypeId).Distinct(StringComparer.Ordinal).Count() ||
            known.OfType<ToneEffectDefinition>().Any() && known.OfType<BloomEffectDefinition>().Any() &&
            Array.FindIndex(known, effect => effect is ToneEffectDefinition) > Array.FindIndex(known, effect => effect is BloomEffectDefinition))
        {
            throw new InvalidDataException("Master Effects 必须唯一并按 Tone → Bloom 的固定顺序排列。");
        }
    }

    /// <summary>
    /// 递归树的参数会指数级放大线段数量，不能只验证每个字段的独立范围。这里同时计算完整几何预算，
    /// 让 UI、变体、恢复和渲染共用同一条 50,000 线段硬边界。
    /// </summary>
    private static void ValidateRecursiveTree(RecursiveTreeDefinition tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Depth is < 1 or > 12 || tree.Branches is < 2 or > 3 ||
            !double.IsFinite(tree.BranchAngleDegrees) || tree.BranchAngleDegrees is < 5 or > 85 ||
            !double.IsFinite(tree.LengthDecay) || tree.LengthDecay is < 0.45 or > 0.85 ||
            !double.IsFinite(tree.Randomness) || tree.Randomness is < 0 or > 1 ||
            !double.IsFinite(tree.TrunkLength) || tree.TrunkLength is < 0.05 or > 0.6 ||
            !double.IsFinite(tree.StrokeWidth) || tree.StrokeWidth is < 0.5 or > 40)
        {
            throw new InvalidDataException("递归树参数超出深度、分叉、角度、衰减、随机度、长度或线宽预算。");
        }

        var segmentCount = 0L;
        var levelCount = 1L;
        for (var level = 0; level < tree.Depth; level++)
        {
            segmentCount += levelCount;
            levelCount *= tree.Branches;
        }

        if (segmentCount > 50_000)
        {
            throw new InvalidDataException("递归树线段总量不能超过 50,000。");
        }
    }

    private static void ValidateJulia(JuliaDefinition julia)
    {
        ArgumentNullException.ThrowIfNull(julia);
        if (julia.PrecisionDigits is < 32 or > 1024 ||
            !ArbitraryDecimal.TryParse(julia.CenterX, out var centerX) ||
            !ArbitraryDecimal.TryParse(julia.CenterY, out var centerY) ||
            !ArbitraryDecimal.TryParse(julia.Scale, out var scale) ||
            !ArbitraryDecimal.TryParse(julia.ConstantReal, out var constantReal) ||
            !ArbitraryDecimal.TryParse(julia.ConstantImaginary, out var constantImaginary))
        {
            throw new InvalidDataException("Julia 高精度参数格式非法，或精度不在 32–1024 位范围内。");
        }

        var minimumStoredExponent = -(julia.PrecisionDigits + 16);
        if (!IsRepresentable(centerX, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(centerY, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(scale, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(constantReal, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(constantImaginary, julia.PrecisionDigits, minimumStoredExponent) ||
            centerX.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerX.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            scale.CompareTo(ArbitraryDecimal.Zero) <= 0 ||
            scale.CompareTo(ArbitraryDecimal.Parse("10")) > 0 ||
            scale.AdjustedExponent < -(julia.PrecisionDigits - 8) ||
            constantReal.CompareTo(ArbitraryDecimal.Parse("-2")) < 0 ||
            constantReal.CompareTo(ArbitraryDecimal.Parse("2")) > 0 ||
            constantImaginary.CompareTo(ArbitraryDecimal.Parse("-2")) < 0 ||
            constantImaginary.CompareTo(ArbitraryDecimal.Parse("2")) > 0 ||
            julia.MaxIterations is < 16 or > 4096)
        {
            throw new InvalidDataException(
                "Julia 参数格式非法或超出安全预算；精度允许 32–1024 位，尺度最小指数必须为 -(精度-8)。");
        }
    }

    private static void ValidateMandelbrot(MandelbrotDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.PrecisionDigits is < 32 or > 1024 ||
            !ArbitraryDecimal.TryParse(definition.CenterX, out var centerX) ||
            !ArbitraryDecimal.TryParse(definition.CenterY, out var centerY) ||
            !ArbitraryDecimal.TryParse(definition.Scale, out var scale))
        {
            throw new InvalidDataException("Mandelbrot 高精度参数格式非法，或精度不在 32–1024 位范围内。");
        }

        var minimumStoredExponent = -(definition.PrecisionDigits + 16);
        if (!IsRepresentable(centerX, definition.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(centerY, definition.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(scale, definition.PrecisionDigits, minimumStoredExponent) ||
            centerX.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerX.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            scale.CompareTo(ArbitraryDecimal.Zero) <= 0 ||
            scale.CompareTo(ArbitraryDecimal.Parse("10")) > 0 ||
            scale.AdjustedExponent < -(definition.PrecisionDigits - 8) ||
            definition.MaxIterations is < 16 or > 4096)
        {
            throw new InvalidDataException("Mandelbrot 参数超出中心、尺度、迭代或精度安全预算。");
        }
    }

    /// <summary>
    /// 候选和收藏会随外部作品文件进入渲染管线，因此这里逐项验证真实配方、数量、身份与枚举范围。
    /// 任何一项失败都会阻止整个快照发布，不能让部分非法候选潜伏到稍后的点击操作。
    /// </summary>
    private void ValidateExploration(ArtworkExplorationDefinition exploration)
    {
        ArgumentNullException.ThrowIfNull(exploration);
        if (!double.IsFinite(exploration.MutationStrength) || exploration.MutationStrength is < 0.05 or > 1 ||
            exploration.Generation is < 0 or > 1_000_000 ||
            !Enum.IsDefined(exploration.Mode) ||
            (exploration.Locks & ~(VariationLockGroups.Seed | VariationLockGroups.Composition |
                VariationLockGroups.Shape | VariationLockGroups.Color)) != 0)
        {
            throw new InvalidDataException("变体强度、轮次、模式或锁定分组非法。");
        }

        if (exploration.Candidates is null || exploration.Favorites is null ||
            exploration.Candidates.Count is not (0 or >= 9 and <= 12) || exploration.Favorites.Count > 64)
        {
            throw new InvalidDataException("变体候选必须为空或包含 9–12 项，收藏不能超过 64 项。");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in exploration.Candidates)
        {
            if (candidate.Number is < 1 or > 12 || !IsValidIdentity(candidate.Id) || !identities.Add(candidate.Id))
            {
                throw new InvalidDataException("变体候选序号或身份非法、重复。");
            }

            ValidateRecipe(candidate.Recipe);
        }

        identities.Clear();
        foreach (var favorite in exploration.Favorites)
        {
            if (!IsValidIdentity(favorite.Id) || !identities.Add(favorite.Id) ||
                string.IsNullOrWhiteSpace(favorite.Name) || favorite.Name.Length > 64)
            {
                throw new InvalidDataException("收藏身份或名称非法、重复。");
            }

            ValidateRecipe(favorite.Recipe);
        }
    }

    private void ValidateRecipe(VariationRecipeDefinition recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!Enum.IsDefined(recipe.GeneratorKind))
        {
            throw new InvalidDataException("候选生成器类型非法。");
        }

        ValidateJulia(recipe.Julia);
        ValidateMandelbrot(recipe.Mandelbrot);
        ValidateRecursiveTree(recipe.RecursiveTree);
        _lSystemValidator.Validate(recipe.LSystem);
    }

    private static bool IsValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRepresentable(ArbitraryDecimal value, int precisionDigits, int minimumExponent) =>
        value.IsZero || (value.SignificantDigits <= precisionDigits && value.Exponent >= minimumExponent);
}
