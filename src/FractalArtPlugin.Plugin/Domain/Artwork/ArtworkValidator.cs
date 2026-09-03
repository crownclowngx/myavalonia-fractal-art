using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Artwork;

public interface IArtworkValidator
{
    void Validate(ArtworkDefinition artwork);
}

/// <summary>集中维护作品资源预算；UI 限制、渲染策略与持久化均不能绕过这一领域边界。</summary>
internal sealed class ArtworkValidator : IArtworkValidator
{
    private readonly ILSystemValidator _lSystemValidator;

    public ArtworkValidator(ILSystemValidator? lSystemValidator = null)
    {
        _lSystemValidator = lSystemValidator ?? new LSystemValidator();
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

        if (!Enum.IsDefined(artwork.GeneratorKind))
        {
            throw new InvalidDataException("作品生成器类型非法。");
        }

        ValidateJulia(artwork.Julia);
        ValidateMandelbrot(artwork.Mandelbrot);
        ValidateRecursiveTree(artwork.RecursiveTree);
        _lSystemValidator.Validate(artwork.LSystem);

        if (string.IsNullOrWhiteSpace(artwork.Presentation.SelectedSection) ||
            artwork.Presentation.SelectedSection.Length > 32)
        {
            throw new InvalidDataException("呈现区域名称不能为空且不能超过 32 个字符。");
        }

        ValidateExploration(artwork.Exploration);
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
