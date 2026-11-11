namespace PrAgent.Domain.Rules;

/// <summary>
/// PLACEHOLDER rule table. The real 16 combinations have not been written down yet (open item 3 in
/// docs/system-design.md). These rows assume the four dimensions are budget type, item type, cost
/// object and stock flag, and use SAP-standard codes (K cost center, P project, A asset, Q project
/// stock, D service). Document type ZCAP and the G/L accounts are invented. Replace every row in the
/// admin view once the real table is known; nothing else in the code depends on these values.
/// </summary>
public static class PlaceholderRules
{
    public const string AucAccount = "16900000";

    public static List<PrRule> Create(DateTimeOffset now)
    {
        var rules = new List<PrRule>();
        var n = 0;

        void Add(BudgetType budget, ItemType item, CostObjectType costObject, bool stock, Action<PrRule> configure)
        {
            n++;
            var rule = new PrRule
            {
                Id = Guid.NewGuid(),
                Code = $"R{n:00}",
                Conditions = RuleFacts.From(budget, item, costObject, stock),
                IsPlaceholder = true,
                IsActive = true,
                ValidFrom = new DateOnly(2000, 1, 1),
                UpdatedAt = now,
                UpdatedBy = "seed",
            };
            rule.Description = $"{budget.ToString().ToUpperInvariant()} {item.ToString().ToLowerInvariant()}, " +
                               $"{(costObject == CostObjectType.Wbs ? "WBS element" : "cost center")}, " +
                               $"{(stock ? "stock" : "non-stock")}";
            configure(rule);
            rules.Add(rule);
        }

        static Action<PrRule> Allow(string docType, string acctCat, string itemCat, GlMode glMode, string? fixedGl, params string[] required) => r =>
        {
            r.Allowed = true;
            r.PrDocType = docType;
            r.AcctAssignmentCategory = acctCat;
            r.ItemCategory = itemCat;
            r.GlMode = glMode;
            r.GlFixedAccount = fixedGl;
            r.RequiredFields = [.. required];
        };

        static Action<PrRule> Forbid(string message) => r =>
        {
            r.Allowed = false;
            r.Message = message;
        };

        const string serviceStock = "A service cannot be a stock item.";
        const string capexStock = "CAPEX purchases cannot go to stock. Charge them to an asset or a CAPEX WBS element.";

        // OPEX material
        Add(BudgetType.Opex, ItemType.Material, CostObjectType.CostCenter, false, Allow("NB", "K", "", GlMode.FromMaterialGroup, null, Fields.CostCenter));
        Add(BudgetType.Opex, ItemType.Material, CostObjectType.CostCenter, true, Allow("NB", "", "", GlMode.None, null, Fields.MaterialNumber));
        Add(BudgetType.Opex, ItemType.Material, CostObjectType.Wbs, false, Allow("NB", "P", "", GlMode.FromMaterialGroup, null, Fields.Wbs));
        Add(BudgetType.Opex, ItemType.Material, CostObjectType.Wbs, true, Allow("NB", "Q", "", GlMode.None, null, Fields.MaterialNumber, Fields.Wbs));

        // OPEX service
        Add(BudgetType.Opex, ItemType.Service, CostObjectType.CostCenter, false, Allow("NB", "K", "D", GlMode.FromMaterialGroup, null, Fields.CostCenter));
        Add(BudgetType.Opex, ItemType.Service, CostObjectType.CostCenter, true, Forbid(serviceStock));
        Add(BudgetType.Opex, ItemType.Service, CostObjectType.Wbs, false, Allow("NB", "P", "D", GlMode.FromMaterialGroup, null, Fields.Wbs));
        Add(BudgetType.Opex, ItemType.Service, CostObjectType.Wbs, true, Forbid(serviceStock));

        // CAPEX material
        Add(BudgetType.Capex, ItemType.Material, CostObjectType.CostCenter, false, Allow("ZCAP", "A", "", GlMode.None, null, Fields.AssetNumber, Fields.CostCenter));
        Add(BudgetType.Capex, ItemType.Material, CostObjectType.CostCenter, true, Forbid(capexStock));
        Add(BudgetType.Capex, ItemType.Material, CostObjectType.Wbs, false, Allow("ZCAP", "P", "", GlMode.Fixed, AucAccount, Fields.Wbs));
        Add(BudgetType.Capex, ItemType.Material, CostObjectType.Wbs, true, Forbid(capexStock));

        // CAPEX service
        Add(BudgetType.Capex, ItemType.Service, CostObjectType.CostCenter, false, Allow("ZCAP", "A", "D", GlMode.None, null, Fields.AssetNumber, Fields.CostCenter));
        Add(BudgetType.Capex, ItemType.Service, CostObjectType.CostCenter, true, Forbid(serviceStock));
        Add(BudgetType.Capex, ItemType.Service, CostObjectType.Wbs, false, Allow("ZCAP", "P", "D", GlMode.Fixed, AucAccount, Fields.Wbs));
        Add(BudgetType.Capex, ItemType.Service, CostObjectType.Wbs, true, Forbid(serviceStock));

        return rules;
    }
}
