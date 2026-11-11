namespace PrAgent.Domain.Rules;

/// <summary>What the requestor (or the agent) has entered for one line so far. Everything is optional.</summary>
public sealed record LineInput
{
    public int LineNo { get; init; }
    public BudgetType? BudgetType { get; init; }
    public ItemType? ItemType { get; init; }
    public CostObjectType? CostObjectType { get; init; }
    public bool? IsStock { get; init; }
    public string? MaterialNumber { get; init; }
    public string? ShortText { get; init; }
    public string? MaterialGroup { get; init; }
    public decimal? Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal? UnitPrice { get; init; }
    public DateOnly? DeliveryDate { get; init; }
    public string? CostCenter { get; init; }
    public string? Wbs { get; init; }
    public string? AssetNumber { get; init; }
}

/// <summary>A line after the engine has applied master data and the matched rule.</summary>
public sealed record ResolvedLine
{
    public int LineNo { get; init; }
    public BudgetType? BudgetType { get; init; }
    public ItemType? ItemType { get; init; }
    public CostObjectType? CostObjectType { get; init; }
    public bool? IsStock { get; init; }
    public string? MaterialNumber { get; init; }
    public string? ShortText { get; init; }
    public string? MaterialGroup { get; init; }
    public decimal? Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal? UnitPrice { get; init; }
    public DateOnly? DeliveryDate { get; init; }
    public string? PurchasingGroup { get; init; }
    public string? PrDocType { get; init; }
    public string? AcctAssignmentCategory { get; init; }
    public string? ItemCategory { get; init; }
    public string? GlAccount { get; init; }
    public string? CostCenter { get; init; }
    public string? Wbs { get; init; }
    public string? AssetNumber { get; init; }

    public decimal Total => (Quantity ?? 0m) * (UnitPrice ?? 0m);
}

public sealed record ValidationMessage(Severity Severity, string? Field, string Text, int? LineNo = null);

public sealed record LineEvaluation(
    int LineNo,
    ResolvedLine Resolved,
    IReadOnlyList<string> LockedFields,
    IReadOnlyDictionary<string, string> LockReasons,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedOptions,
    IReadOnlyList<ValidationMessage> Messages,
    string? MatchedRuleCode,
    bool MatchedRuleIsPlaceholder)
{
    public bool IsValid => Messages.All(m => m.Severity != Severity.Error);
}

public sealed record MaterialInfo(string Number, string Description, string MaterialGroup, string BaseUom, bool IsStock);

public sealed record MaterialGroupInfo(string Code, string Name, ItemType? ItemType, string? DefaultGlAccount, string? DefaultPurchasingGroup);

public sealed record WbsInfo(string Code, string Name, BudgetType? BudgetType);

/// <summary>
/// The master data the engine needs to evaluate a set of lines, loaded up front so the engine itself
/// stays pure and synchronous. A null collection means "not loaded, skip the existence check".
/// </summary>
public sealed class MasterDataSnapshot
{
    public Dictionary<string, MaterialInfo> Materials { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MaterialGroupInfo> MaterialGroups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CostCenters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WbsInfo> WbsElements { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnitsOfMeasure { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
