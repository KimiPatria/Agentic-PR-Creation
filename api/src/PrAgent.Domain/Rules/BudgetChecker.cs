namespace PrAgent.Domain.Rules;

public sealed record BudgetPosition(CostObjectType ObjectType, string ObjectCode, string Currency, decimal Budget, decimal Committed, decimal Actual)
{
    public decimal Remaining => Budget - Committed - Actual;
}

public sealed record BudgetCheck(
    CostObjectType ObjectType,
    string ObjectCode,
    string Currency,
    decimal Requested,
    decimal? Remaining,
    bool IsOver,
    string Text,
    IReadOnlyList<int> LineNos);

/// <summary>
/// Compares what a request asks for against the remaining budget of each cost object it charges.
/// It warns; it never blocks. The binding budget check stays in SAP.
/// </summary>
public static class BudgetChecker
{
    public static (CostObjectType Type, string Code)? BudgetObject(ResolvedLine line) =>
        line.Wbs is not null ? (CostObjectType.Wbs, line.Wbs)
        : line.CostCenter is not null ? (CostObjectType.CostCenter, line.CostCenter)
        : null;

    public static IReadOnlyList<BudgetCheck> Check(
        IEnumerable<ResolvedLine> lines,
        string requestCurrency,
        IReadOnlyDictionary<(CostObjectType, string), BudgetPosition?> positions)
    {
        var checks = new List<BudgetCheck>();

        var grouped = lines
            .Select(l => (Line: l, Object: BudgetObject(l)))
            .Where(x => x.Object is not null)
            .GroupBy(x => (x.Object!.Value.Type, Code: x.Object!.Value.Code.ToUpperInvariant()));

        foreach (var g in grouped)
        {
            var (type, code) = g.Key;
            var requested = g.Sum(x => x.Line.Total);
            var lineNos = g.Select(x => x.Line.LineNo).ToList();
            var name = type == CostObjectType.Wbs ? "WBS element" : "cost center";

            positions.TryGetValue((type, code), out var position);
            if (position is null)
            {
                checks.Add(new(type, code, requestCurrency, requested, null, false,
                    $"No budget figure is available for {name} {code}.", lineNos));
            }
            else if (!string.Equals(position.Currency, requestCurrency, StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new(type, code, position.Currency, requested, position.Remaining, false,
                    $"Budget for {name} {code} is in {position.Currency}, the request is in {requestCurrency}. Not compared.", lineNos));
            }
            else if (requested > position.Remaining)
            {
                checks.Add(new(type, code, position.Currency, requested, position.Remaining, true,
                    $"This request asks for {requested:N0} {requestCurrency} from {name} {code}, which has {position.Remaining:N0} left. Over by {requested - position.Remaining:N0}.", lineNos));
            }
            else
            {
                checks.Add(new(type, code, position.Currency, requested, position.Remaining, false,
                    $"{name} {code} has {position.Remaining:N0} {requestCurrency} left, enough for the {requested:N0} requested.", lineNos));
            }
        }

        return checks;
    }
}
