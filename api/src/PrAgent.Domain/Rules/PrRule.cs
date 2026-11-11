namespace PrAgent.Domain.Rules;

/// <summary>
/// One row of the rule table. A rule matches a line when every condition equals the line's facts.
/// When several rules match, the one with the most conditions wins, then the highest priority.
/// </summary>
public sealed class PrRule
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Fact name → required value, for example budgetType → Opex. See <see cref="RuleFacts"/>.</summary>
    public Dictionary<string, string> Conditions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int Priority { get; set; }

    /// <summary>False means the combination is forbidden and <see cref="Message"/> says why.</summary>
    public bool Allowed { get; set; } = true;
    public string? Message { get; set; }

    public string? PrDocType { get; set; }

    /// <summary>SAP account assignment category. Empty string is SAP's "blank" (stock, no assignment).</summary>
    public string? AcctAssignmentCategory { get; set; }

    /// <summary>SAP item category. Empty string is SAP's "blank" (standard item).</summary>
    public string? ItemCategory { get; set; }

    public GlMode GlMode { get; set; }
    public string? GlFixedAccount { get; set; }

    /// <summary>Field names from <see cref="Fields"/> the requestor must fill for this combination.</summary>
    public List<string> RequiredFields { get; set; } = [];

    /// <summary>True for seeded rows that stand in for the real, not yet documented rules.</summary>
    public bool IsPlaceholder { get; set; }

    public bool IsActive { get; set; } = true;
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    public bool IsInForce(DateOnly on) => IsActive && ValidFrom <= on && (ValidTo is null || ValidTo >= on);
}

/// <summary>The fact dimensions rules can match on, and every value each can take.</summary>
public static class RuleFacts
{
    public static readonly IReadOnlyDictionary<string, string[]> Domains = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [Fields.BudgetType] = Enum.GetNames<BudgetType>(),
        [Fields.ItemType] = Enum.GetNames<ItemType>(),
        [Fields.CostObjectType] = Enum.GetNames<CostObjectType>(),
        [Fields.IsStock] = ["true", "false"],
    };

    public static Dictionary<string, string> From(BudgetType? budgetType, ItemType? itemType, CostObjectType? costObjectType, bool? isStock)
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (budgetType is not null) facts[Fields.BudgetType] = budgetType.Value.ToString();
        if (itemType is not null) facts[Fields.ItemType] = itemType.Value.ToString();
        if (costObjectType is not null) facts[Fields.CostObjectType] = costObjectType.Value.ToString();
        if (isStock is not null) facts[Fields.IsStock] = isStock.Value ? "true" : "false";
        return facts;
    }
}
