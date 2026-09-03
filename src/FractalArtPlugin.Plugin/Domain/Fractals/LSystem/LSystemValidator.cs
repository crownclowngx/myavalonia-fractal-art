using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.LSystem;

public sealed record LSystemValidationError(string Field, string Code, string Message);

public sealed record LSystemValidationResult(
    bool IsValid,
    long ExpandedSymbolCount,
    long EstimatedSegmentCount,
    int EstimatedStackDepth,
    IReadOnlyList<LSystemValidationError> Errors);

/// <summary>
/// L-System 的唯一静态规则边界。它用符号计数推演预算，不必先分配完整展开串；UI、快照和渲染均复用
/// 同一结果，避免控件上限与真实资源规则分叉。
/// </summary>
internal sealed class LSystemValidator : ILSystemValidator
{
    public const int MaximumExpandedSymbols = 250_000;
    public const int MaximumSegments = 50_000;
    public const int MaximumStackDepth = 1_024;

    public LSystemValidationResult Analyze(LSystemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<LSystemValidationError>();
        if (string.IsNullOrEmpty(definition.Axiom) || definition.Axiom.Length > 1_024)
        {
            errors.Add(new("axiom", "axiom.length", "公理长度必须位于 1–1,024。"));
        }

        if (definition.Rules is null || definition.Rules.Count is < 1 or > 32)
        {
            errors.Add(new("rules", "rules.count", "产生式数量必须位于 1–32。"));
        }

        if (definition.Iterations is < 0 or > 12)
        {
            errors.Add(new("iterations", "iterations.range", "迭代层级必须位于 0–12。"));
        }

        if (!double.IsFinite(definition.TurnAngleDegrees) || definition.TurnAngleDegrees is <= 0 or > 360 ||
            !double.IsFinite(definition.InitialHeadingDegrees) || Math.Abs(definition.InitialHeadingDegrees) > 3_600 ||
            !double.IsFinite(definition.StepLength) || definition.StepLength is <= 0 or > 1 ||
            !double.IsFinite(definition.LengthDecay) || definition.LengthDecay is <= 0 or > 1 ||
            !double.IsFinite(definition.StrokeWidth) || definition.StrokeWidth is < 0.5 or > 40 ||
            !double.IsFinite(definition.StrokeWidthDecay) || definition.StrokeWidthDecay is <= 0 or > 1)
        {
            errors.Add(new("drawing", "drawing.range", "角度、方向、步长、长度衰减、线宽或线宽衰减超出安全范围。"));
        }

        var rules = new Dictionary<char, string>();
        if (definition.Rules is not null)
        {
            for (var index = 0; index < definition.Rules.Count; index++)
            {
                var rule = definition.Rules[index];
                if (!IsVariable(rule.Symbol) || rule.Replacement is null || rule.Replacement.Length > 4_096)
                {
                    errors.Add(new($"rules[{index}]", "rule.invalid", "规则左侧必须是单个 A–Z，替换内容不能超过 4,096 个字符。"));
                    continue;
                }

                if (!rules.TryAdd(rule.Symbol, rule.Replacement))
                {
                    errors.Add(new($"rules[{index}]", "rule.duplicate", $"符号 {rule.Symbol} 存在重复产生式。"));
                }

                ValidateSymbols(rule.Replacement, $"rules[{index}].replacement", errors);
                ValidateBrackets(rule.Replacement, $"rules[{index}].replacement", errors);
            }
        }

        ValidateSymbols(definition.Axiom ?? string.Empty, "axiom", errors);
        ValidateBrackets(definition.Axiom ?? string.Empty, "axiom", errors);
        if (errors.Count > 0)
        {
            return new(false, 0, 0, 0, errors);
        }

        var counts = CountSymbols(definition.Axiom!);
        for (var iteration = 0; iteration < definition.Iterations; iteration++)
        {
            var next = new Dictionary<char, long>();
            foreach (var pair in counts)
            {
                var replacement = rules.GetValueOrDefault(pair.Key);
                if (replacement is null)
                {
                    AddCount(next, pair.Key, pair.Value);
                    continue;
                }

                foreach (var replacementCount in CountSymbols(replacement))
                {
                    AddCount(next, replacementCount.Key, MultiplyBounded(pair.Value, replacementCount.Value));
                }
            }

            counts = next;
            if (counts.Values.Sum() > MaximumExpandedSymbols)
            {
                errors.Add(new("iterations", "budget.symbols", $"规则展开超过 {MaximumExpandedSymbols:N0} 个符号，请降低迭代层级。"));
                break;
            }
        }

        var symbolCount = Math.Min(MaximumExpandedSymbols + 1L, counts.Values.Sum());
        var segmentCount = counts.GetValueOrDefault('F') + counts.GetValueOrDefault('G');
        var stackDepth = EstimateStackDepth(definition.Axiom!, rules, definition.Iterations);
        if (segmentCount == 0)
        {
            errors.Add(new("rules", "rules.no_draw", "展开结果没有 F 或 G 绘制指令。"));
        }
        else if (segmentCount > MaximumSegments)
        {
            errors.Add(new("iterations", "budget.segments", $"展开结果超过 {MaximumSegments:N0} 条绘制线段，请降低迭代层级。"));
        }

        if (stackDepth > MaximumStackDepth)
        {
            errors.Add(new("iterations", "budget.stack", $"展开结果的分支栈深度超过 {MaximumStackDepth:N0} 层，请降低迭代层级。"));
        }

        return new(errors.Count == 0, symbolCount, segmentCount, stackDepth, errors);
    }

    public void Validate(LSystemDefinition definition)
    {
        var result = Analyze(definition);
        if (!result.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", result.Errors.Select(error => error.Message)));
        }
    }

    private static void ValidateSymbols(string text, string field, ICollection<LSystemValidationError> errors)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!IsAllowed(text[index]))
            {
                errors.Add(new(field, "symbol.unsupported", $"第 {index + 1} 个字符“{text[index]}”不是支持的 L-System 符号。"));
                return;
            }
        }
    }

    private static bool IsAllowed(char symbol) => IsVariable(symbol) || symbol is 'f' or '+' or '-' or '[' or ']';
    private static bool IsVariable(char symbol) => symbol is >= 'A' and <= 'Z';

    private static void ValidateBrackets(string text, string field, ICollection<LSystemValidationError> errors)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[')
            {
                depth++;
            }
            else if (text[index] == ']' && --depth < 0)
            {
                errors.Add(new(field, "branch.unmatched_close", $"第 {index + 1} 个字符存在没有对应左括号的 ]。"));
                return;
            }
        }

        if (depth != 0)
        {
            errors.Add(new(field, "branch.unclosed", "存在未闭合的 [ 分支括号。"));
        }
    }

    private static Dictionary<char, long> CountSymbols(string text)
    {
        var counts = new Dictionary<char, long>();
        foreach (var symbol in text)
        {
            AddCount(counts, symbol, 1);
        }

        return counts;
    }

    private static void AddCount(IDictionary<char, long> counts, char symbol, long value)
    {
        var current = counts.TryGetValue(symbol, out var existing) ? existing : 0;
        var total = current + value;
        counts[symbol] = Math.Min(MaximumExpandedSymbols + 1L, total);
    }

    private static long MultiplyBounded(long left, long right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return left > MaximumExpandedSymbols / right ? MaximumExpandedSymbols + 1L : left * right;
    }

    /// <summary>
    /// 每条产生式已经单独保证括号净深度为零，因此只需逐轮传播“该变量展开后的最大附加深度”，
    /// 就能在不分配完整字符串的情况下得到 Turtle 栈上界。
    /// </summary>
    private static int EstimateStackDepth(string axiom, IReadOnlyDictionary<char, string> rules, int iterations)
    {
        var depths = Enumerable.Range('A', 26).ToDictionary(value => (char)value, _ => 0);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = new Dictionary<char, int>();
            foreach (var symbol in depths.Keys)
            {
                next[symbol] = rules.TryGetValue(symbol, out var replacement)
                    ? AnalyzeMaximumDepth(replacement, depths)
                    : 0;
            }

            depths = next;
        }

        return AnalyzeMaximumDepth(axiom, depths);
    }

    private static int AnalyzeMaximumDepth(string text, IReadOnlyDictionary<char, int> variableDepths)
    {
        var depth = 0;
        var maximum = 0;
        foreach (var symbol in text)
        {
            if (symbol == '[')
            {
                depth++;
                maximum = Math.Max(maximum, depth);
            }
            else if (symbol == ']')
            {
                depth--;
            }
            else if (IsVariable(symbol))
            {
                maximum = Math.Max(maximum, depth + variableDepths.GetValueOrDefault(symbol));
            }
        }

        return maximum;
    }
}
