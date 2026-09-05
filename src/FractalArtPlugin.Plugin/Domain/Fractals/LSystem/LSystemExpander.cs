using System.Text;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.LSystem;

/// <summary>逐轮执行确定性并行替换；每次追加前检查硬预算并按有界间隔观察取消。</summary>
internal sealed class LSystemExpander(ILSystemValidator validator) : ILSystemExpander
{
    public string Expand(LSystemDefinition definition, CancellationToken cancellationToken) =>
        ExpandCore(definition, cancellationToken, null);

    /// <summary>
    /// 数学透镜需要第 0 到 N 轮，但验证仍只针对用户提交的最终定义执行一次。这样公理暂时没有 F/G、后续
    /// 才产生绘制符号的合法系统也能展示早期轮次，同时所有替换仍经过生产使用的同一个核心循环。
    /// </summary>
    public IReadOnlyList<string> ExpandGenerations(
        LSystemDefinition definition,
        CancellationToken cancellationToken)
    {
        var generations = new List<string>(definition.Iterations + 1);
        _ = ExpandCore(definition, cancellationToken, generations);
        return generations.AsReadOnly();
    }

    private string ExpandCore(
        LSystemDefinition definition,
        CancellationToken cancellationToken,
        ICollection<string>? generations)
    {
        validator.Validate(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var rules = definition.Rules.ToDictionary(rule => rule.Symbol, rule => rule.Replacement);
        var current = definition.Axiom;
        generations?.Add(current);
        for (var iteration = 0; iteration < definition.Iterations; iteration++)
        {
            var next = new StringBuilder(Math.Min(LSystemValidator.MaximumExpandedSymbols, current.Length * 2));
            for (var index = 0; index < current.Length; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var replacement = rules.GetValueOrDefault(current[index]);
                next.Append(replacement ?? current[index].ToString());
                if (next.Length > LSystemValidator.MaximumExpandedSymbols)
                {
                    throw new InvalidDataException("L-System 展开超过 250,000 个符号。");
                }
            }

            current = next.ToString();
            generations?.Add(current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return current;
    }
}
