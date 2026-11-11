namespace PrAgent.Domain.Rules;

/// <summary>
/// Deterministic evaluation of one PR line: applies material master data, finds the rule for the
/// line's combination, derives and locks the SAP backend fields, and validates what is left.
/// It never calls out; the caller supplies the rules and a master data snapshot.
/// </summary>
public sealed class RulesEngine
{
    public LineEvaluation EvaluateLine(LineInput input, IReadOnlyCollection<PrRule> rules, MasterDataSnapshot md, DateOnly today)
    {
        var messages = new List<ValidationMessage>();
        var locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Fields.BudgetType, Fields.ItemType, Fields.CostObjectType, Fields.ShortText, Fields.MaterialGroup,
            Fields.Quantity, Fields.Uom, Fields.UnitPrice, Fields.DeliveryDate,
        };

        void Error(string field, string text) => messages.Add(new(Severity.Error, field, text, input.LineNo));
        void Warn(string field, string text) => messages.Add(new(Severity.Warning, field, text, input.LineNo));

        var itemType = input.ItemType;
        var isStock = input.IsStock;
        var materialGroup = Clean(input.MaterialGroup);
        var uom = Clean(input.Uom);
        var shortText = Clean(input.ShortText);
        var materialNumber = Clean(input.MaterialNumber);

        // 1. Material master overrides what the requestor typed.
        if (materialNumber is not null)
        {
            if (md.Materials.TryGetValue(materialNumber, out var material))
            {
                var reason = $"Taken from material master {material.Number}.";
                itemType = ItemType.Material;
                isStock = material.IsStock;
                materialGroup = material.MaterialGroup;
                uom = material.BaseUom;
                shortText ??= material.Description;
                locked[Fields.ItemType] = reason;
                locked[Fields.IsStock] = reason;
                locked[Fields.MaterialGroup] = reason;
                locked[Fields.Uom] = reason;
            }
            else
            {
                Error(Fields.MaterialNumber, $"Material {materialNumber} was not found in SAP.");
            }
        }
        else if (itemType == ItemType.Service)
        {
            isStock = false;
            locked[Fields.IsStock] = "Services are never stock items.";
        }
        else
        {
            isStock = false;
            locked[Fields.IsStock] = "Only items with a SAP material number can be stock items.";
        }

        // 2. Material group mapping gives the purchasing group and confirms the item type.
        string? purchasingGroup = null;
        MaterialGroupInfo? group = null;
        if (materialGroup is not null)
        {
            if (md.MaterialGroups.TryGetValue(materialGroup, out group))
            {
                if (group.ItemType is not null && itemType is not null && group.ItemType != itemType)
                    Error(Fields.MaterialGroup, $"Material group {group.Code} ({group.Name}) is for {group.ItemType} items, but this line is a {itemType}.");
                else if (group.ItemType is not null && itemType is null)
                    itemType = group.ItemType;

                if (group.DefaultPurchasingGroup is not null)
                {
                    purchasingGroup = group.DefaultPurchasingGroup;
                    locked[Fields.PurchasingGroup] = $"Mapped from material group {group.Code}.";
                }
                else
                {
                    Error(Fields.PurchasingGroup, $"No purchasing group is mapped to material group {group.Code}. Ask the PR admin to map one.");
                }
            }
            else
            {
                Error(Fields.MaterialGroup, $"Material group {materialGroup} is not known.");
            }
        }

        // 3. Find the rule for this combination.
        var active = rules.Where(r => r.IsInForce(today)).ToList();
        var facts = RuleFacts.From(input.BudgetType, itemType, input.CostObjectType, isStock);
        var allowedOptions = AllowedOptions(facts, locked.Keys, active);

        string? prDocType = null, acctCat = null, itemCat = null, glAccount = null;
        var costCenter = Clean(input.CostCenter);
        var wbs = Clean(input.Wbs);
        var assetNumber = Clean(input.AssetNumber);
        PrRule? rule = null;

        if (facts.Count == RuleFacts.Domains.Count)
        {
            rule = Resolve(facts, active);
            if (rule is null)
            {
                Error(Fields.BudgetType, "No rule covers this combination of budget type, item type, cost object and stock flag. Ask the PR admin.");
            }
            else if (!rule.Allowed)
            {
                Error(Fields.BudgetType, rule.Message ?? $"This combination is not allowed ({rule.Code}).");
                rule = null;
            }
        }

        if (rule is not null)
        {
            var reason = $"Set by rule {rule.Code}: {rule.Description}";
            prDocType = rule.PrDocType;
            acctCat = rule.AcctAssignmentCategory;
            itemCat = rule.ItemCategory;
            locked[Fields.PrDocType] = reason;
            locked[Fields.AcctAssignmentCategory] = reason;
            locked[Fields.ItemCategory] = reason;
            locked[Fields.GlAccount] = reason;

            switch (rule.GlMode)
            {
                case GlMode.Fixed:
                    glAccount = rule.GlFixedAccount;
                    break;
                case GlMode.FromMaterialGroup:
                    glAccount = group?.DefaultGlAccount;
                    if (group is not null && glAccount is null)
                        Error(Fields.GlAccount, $"No G/L account is mapped to material group {group.Code}. Ask the PR admin to map one.");
                    break;
            }

            foreach (var field in rule.RequiredFields) required.Add(field);

            // Cost-object fields this combination does not use are cleared so they cannot reach SAP.
            foreach (var field in Fields.CostObjectFields.Where(f => !required.Contains(f)))
            {
                locked[field] = $"Not used for this combination (rule {rule.Code}).";
                if (field == Fields.CostCenter) costCenter = null;
                else if (field == Fields.Wbs) wbs = null;
                else assetNumber = null;
            }
        }

        var resolved = new ResolvedLine
        {
            LineNo = input.LineNo,
            BudgetType = input.BudgetType,
            ItemType = itemType,
            CostObjectType = input.CostObjectType,
            IsStock = isStock,
            MaterialNumber = materialNumber,
            ShortText = shortText,
            MaterialGroup = materialGroup,
            Quantity = input.Quantity,
            Uom = uom,
            UnitPrice = input.UnitPrice,
            DeliveryDate = input.DeliveryDate,
            PurchasingGroup = purchasingGroup,
            PrDocType = prDocType,
            AcctAssignmentCategory = acctCat,
            ItemCategory = itemCat,
            GlAccount = glAccount,
            CostCenter = costCenter,
            Wbs = wbs,
            AssetNumber = assetNumber,
        };

        // 4. Required fields.
        foreach (var field in required)
        {
            if (IsBlank(resolved, field))
                Error(field, $"{Label(field)} is required.");
        }

        // 5. Value checks.
        if (resolved.Quantity is <= 0) Error(Fields.Quantity, "Quantity must be greater than zero.");
        if (resolved.UnitPrice is < 0) Error(Fields.UnitPrice, "Unit price cannot be negative.");
        if (resolved.UnitPrice is 0) Warn(Fields.UnitPrice, "Unit price is zero. SAP budget checks will not see this line.");
        if (resolved.DeliveryDate is { } date && date < today) Error(Fields.DeliveryDate, "Delivery date is in the past.");
        if (uom is not null && md.UnitsOfMeasure.Count > 0 && !md.UnitsOfMeasure.Contains(uom))
            Error(Fields.Uom, $"Unit of measure {uom} is not known in SAP.");
        if (shortText is { Length: > 40 })
            Warn(Fields.ShortText, "SAP short text holds 40 characters. The rest will be cut off.");

        if (costCenter is not null && !md.CostCenters.ContainsKey(costCenter))
            Error(Fields.CostCenter, $"Cost center {costCenter} was not found in SAP.");
        if (assetNumber is not null && !md.Assets.ContainsKey(assetNumber))
            Error(Fields.AssetNumber, $"Asset {assetNumber} was not found in SAP.");
        if (wbs is not null)
        {
            if (!md.WbsElements.TryGetValue(wbs, out var wbsInfo))
                Error(Fields.Wbs, $"WBS element {wbs} was not found in SAP.");
            else if (wbsInfo.BudgetType is not null && input.BudgetType is not null && wbsInfo.BudgetType != input.BudgetType)
                Error(Fields.Wbs, $"WBS element {wbs} is a {wbsInfo.BudgetType.Value.ToString().ToUpperInvariant()} element, but this line is {input.BudgetType.Value.ToString().ToUpperInvariant()}.");
        }

        return new LineEvaluation(
            input.LineNo,
            resolved,
            locked.Keys.ToList(),
            locked,
            required.ToList(),
            allowedOptions,
            messages,
            rule?.Code,
            rule?.IsPlaceholder ?? false);
    }

    /// <summary>The winning rule for a complete set of facts, or null when none matches.</summary>
    public static PrRule? Resolve(IReadOnlyDictionary<string, string> facts, IEnumerable<PrRule> rules) =>
        rules
            .Where(r => r.Conditions.All(c => facts.TryGetValue(c.Key, out var value) && string.Equals(value, c.Value, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Conditions.Count)
            .ThenByDescending(r => r.Priority)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// For each fact the requestor can still choose, the values that can lead to an allowed combination
    /// given everything else already chosen. This is what drives the cascading dropdowns.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> AllowedOptions(
        Dictionary<string, string> facts, IEnumerable<string> lockedFields, List<PrRule> rules)
    {
        var lockedSet = lockedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dimension, domain) in RuleFacts.Domains)
        {
            if (lockedSet.Contains(dimension) && facts.TryGetValue(dimension, out var fixedValue))
            {
                result[dimension] = [fixedValue];
                continue;
            }

            var others = facts.Where(f => !string.Equals(f.Key, dimension, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            result[dimension] = domain.Where(value => AnyAllowedCompletion(Extend(others, dimension, value), rules)).ToList();
        }

        return result;
    }

    private static bool AnyAllowedCompletion(Dictionary<string, string> facts, List<PrRule> rules)
    {
        var missing = RuleFacts.Domains.Keys.FirstOrDefault(k => !facts.ContainsKey(k));
        if (missing is null)
            return Resolve(facts, rules) is { Allowed: true };

        return RuleFacts.Domains[missing].Any(value => AnyAllowedCompletion(Extend(facts, missing, value), rules));
    }

    private static Dictionary<string, string> Extend(Dictionary<string, string> facts, string key, string value) =>
        new(facts, StringComparer.OrdinalIgnoreCase) { [key] = value };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsBlank(ResolvedLine line, string field) => field switch
    {
        Fields.BudgetType => line.BudgetType is null,
        Fields.ItemType => line.ItemType is null,
        Fields.CostObjectType => line.CostObjectType is null,
        Fields.IsStock => line.IsStock is null,
        Fields.MaterialNumber => line.MaterialNumber is null,
        Fields.ShortText => line.ShortText is null,
        Fields.MaterialGroup => line.MaterialGroup is null,
        Fields.Quantity => line.Quantity is null,
        Fields.Uom => line.Uom is null,
        Fields.UnitPrice => line.UnitPrice is null,
        Fields.DeliveryDate => line.DeliveryDate is null,
        Fields.PurchasingGroup => line.PurchasingGroup is null,
        Fields.CostCenter => line.CostCenter is null,
        Fields.Wbs => line.Wbs is null,
        Fields.AssetNumber => line.AssetNumber is null,
        _ => false,
    };

    public static string Label(string field) => field switch
    {
        Fields.BudgetType => "Budget type",
        Fields.ItemType => "Item type",
        Fields.CostObjectType => "Cost object",
        Fields.IsStock => "Stock flag",
        Fields.MaterialNumber => "Material number",
        Fields.ShortText => "Description",
        Fields.MaterialGroup => "Material group",
        Fields.Quantity => "Quantity",
        Fields.Uom => "Unit of measure",
        Fields.UnitPrice => "Unit price",
        Fields.DeliveryDate => "Delivery date",
        Fields.PurchasingGroup => "Purchasing group",
        Fields.PrDocType => "PR document type",
        Fields.AcctAssignmentCategory => "Account assignment category",
        Fields.ItemCategory => "Item category",
        Fields.GlAccount => "G/L account",
        Fields.CostCenter => "Cost center",
        Fields.Wbs => "WBS element",
        Fields.AssetNumber => "Asset number",
        _ => field,
    };
}
