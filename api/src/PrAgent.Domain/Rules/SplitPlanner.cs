namespace PrAgent.Domain.Rules;

public sealed record SplitGroup(string Key, string Label, IReadOnlyList<int> LineNos, decimal Total, string? PrDocType, string? PurchasingGroup);

/// <summary>
/// Decides how many SAP PRs one request becomes. Lines that differ in any split key cannot share a PR.
/// The PR document type is always a key, because a SAP PR has exactly one.
/// </summary>
public static class SplitPlanner
{
    public static readonly string[] DefaultKeys = [Fields.ItemType, Fields.PurchasingGroup, Fields.BudgetType];

    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        Fields.ItemType, Fields.PurchasingGroup, Fields.BudgetType, Fields.CostObjectType, Fields.MaterialGroup, Fields.PrDocType,
    };

    public static IReadOnlyList<SplitGroup> Plan(IEnumerable<ResolvedLine> lines, IReadOnlyList<string>? keys = null)
    {
        var effective = (keys is { Count: > 0 } ? keys : DefaultKeys)
            .Append(Fields.PrDocType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unsupported = effective.FirstOrDefault(k => !SupportedKeys.Contains(k));
        if (unsupported is not null)
            throw new DomainException($"'{unsupported}' cannot be used as a split key.");

        return lines
            .OrderBy(l => l.LineNo)
            .GroupBy(l => string.Join('|', effective.Select(k => Value(l, k) ?? "?")))
            .Select(g => new SplitGroup(
                g.Key,
                string.Join(" · ", effective.Select(k => $"{RulesEngine.Label(k)}: {Display(Value(g.First(), k))}")),
                g.Select(l => l.LineNo).ToList(),
                g.Sum(l => l.Total),
                g.First().PrDocType,
                g.First().PurchasingGroup))
            .ToList();
    }

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? "not set" : value;

    private static string? Value(ResolvedLine line, string key)
    {
        if (key.Equals(Fields.ItemType, StringComparison.OrdinalIgnoreCase)) return line.ItemType?.ToString();
        if (key.Equals(Fields.PurchasingGroup, StringComparison.OrdinalIgnoreCase)) return line.PurchasingGroup;
        if (key.Equals(Fields.BudgetType, StringComparison.OrdinalIgnoreCase)) return line.BudgetType?.ToString();
        if (key.Equals(Fields.CostObjectType, StringComparison.OrdinalIgnoreCase)) return line.CostObjectType?.ToString();
        if (key.Equals(Fields.MaterialGroup, StringComparison.OrdinalIgnoreCase)) return line.MaterialGroup;
        return line.PrDocType;
    }
}
