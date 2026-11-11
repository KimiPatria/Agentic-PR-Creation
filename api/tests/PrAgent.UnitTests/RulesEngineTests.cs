using PrAgent.Domain;
using PrAgent.Domain.Rules;

namespace PrAgent.UnitTests;

public static class TestData
{
    public static readonly DateOnly Today = new(2026, 11, 10);
    public static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    public static List<PrRule> Rules() => PlaceholderRules.Create(Now);

    public static MasterDataSnapshot Snapshot() => new()
    {
        Materials =
        {
            ["100000101"] = new("100000101", "Laptop 14in", "IT01", "EA", false),
            ["100000401"] = new("100000401", "A4 Copy Paper", "GA01", "BOX", true),
        },
        MaterialGroups =
        {
            ["IT01"] = new("IT01", "Computers", ItemType.Material, "61100000", "P01"),
            ["IT10"] = new("IT10", "IT Services", ItemType.Service, "61300000", "P02"),
            ["GA01"] = new("GA01", "Office Supplies", ItemType.Material, "62100000", "P03"),
            ["XX99"] = new("XX99", "Unmapped", null, null, null),
        },
        CostCenters = { ["CC1101"] = "IT Infrastructure" },
        WbsElements =
        {
            ["C-2026-IT-001"] = new("C-2026-IT-001", "Data Centre Refresh", BudgetType.Capex),
            ["O-2026-IT-001"] = new("O-2026-IT-001", "Cyber Campaign", BudgetType.Opex),
        },
        Assets = { ["200000001"] = "EUC Pool" },
        UnitsOfMeasure = { "EA", "BOX", "AU" },
    };

    public static LineInput Line(
        BudgetType? budget = BudgetType.Opex, ItemType? item = ItemType.Material, CostObjectType? costObject = CostObjectType.CostCenter,
        string? materialNumber = null, string? materialGroup = "IT01", string? costCenter = "CC1101", string? wbs = null, string? asset = null) => new()
    {
        LineNo = 1,
        BudgetType = budget,
        ItemType = item,
        CostObjectType = costObject,
        MaterialNumber = materialNumber,
        ShortText = "Test item",
        MaterialGroup = materialGroup,
        Quantity = 2,
        Uom = "EA",
        UnitPrice = 1_000_000,
        DeliveryDate = Today.AddDays(14),
        CostCenter = costCenter,
        Wbs = wbs,
        AssetNumber = asset,
    };
}

public class RulesEngineTests
{
    private readonly RulesEngine _engine = new();

    private LineEvaluation Evaluate(LineInput line, List<PrRule>? rules = null) =>
        _engine.EvaluateLine(line, rules ?? TestData.Rules(), TestData.Snapshot(), TestData.Today);

    public static TheoryData<string, string, string, bool> AllCombinations()
    {
        var data = new TheoryData<string, string, string, bool>();
        foreach (var budget in Enum.GetNames<BudgetType>())
        foreach (var item in Enum.GetNames<ItemType>())
        foreach (var costObject in Enum.GetNames<CostObjectType>())
        foreach (var stock in new[] { false, true })
            data.Add(budget, item, costObject, stock);
        return data;
    }

    [Fact]
    public void Placeholder_table_has_sixteen_rows_all_marked_placeholder()
    {
        var rules = TestData.Rules();
        Assert.Equal(16, rules.Count);
        Assert.All(rules, r => Assert.True(r.IsPlaceholder));
        Assert.Equal(16, rules.Select(r => string.Join('|', r.Conditions.OrderBy(c => c.Key).Select(c => c.Value))).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_combination_resolves_to_exactly_its_own_rule(string budget, string item, string costObject, bool stock)
    {
        var facts = RuleFacts.From(Enum.Parse<BudgetType>(budget), Enum.Parse<ItemType>(item), Enum.Parse<CostObjectType>(costObject), stock);
        var rule = RulesEngine.Resolve(facts, TestData.Rules());

        Assert.NotNull(rule);
        Assert.Equal(4, rule.Conditions.Count);
        Assert.All(facts, f => Assert.Equal(f.Value, rule.Conditions[f.Key]));

        // Forbidden rows explain themselves; allowed rows carry every locked SAP field.
        if (rule.Allowed)
        {
            Assert.NotNull(rule.PrDocType);
            Assert.NotNull(rule.AcctAssignmentCategory);
            Assert.NotNull(rule.ItemCategory);
            Assert.NotEmpty(rule.RequiredFields);
            if (rule.GlMode == GlMode.Fixed) Assert.NotNull(rule.GlFixedAccount);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Message));
        }

        // Services and CAPEX never go to stock.
        if (stock && (item == "Service" || budget == "Capex")) Assert.False(rule.Allowed);
    }

    [Fact]
    public void Opex_material_on_cost_center_derives_and_locks_backend_fields()
    {
        var result = Evaluate(TestData.Line());

        Assert.True(result.IsValid, string.Join("; ", result.Messages.Select(m => m.Text)));
        Assert.Equal("R01", result.MatchedRuleCode);
        Assert.True(result.MatchedRuleIsPlaceholder);
        Assert.Equal("NB", result.Resolved.PrDocType);
        Assert.Equal("K", result.Resolved.AcctAssignmentCategory);
        Assert.Equal("", result.Resolved.ItemCategory);
        Assert.Equal("61100000", result.Resolved.GlAccount);
        Assert.Equal("P01", result.Resolved.PurchasingGroup);
        Assert.False(result.Resolved.IsStock);
        Assert.Contains(Fields.PrDocType, result.LockedFields);
        Assert.Contains(Fields.GlAccount, result.LockedFields);
        Assert.Contains(Fields.PurchasingGroup, result.LockedFields);
        Assert.Contains(Fields.CostCenter, result.RequiredFields);
        Assert.Contains("R01", result.LockReasons[Fields.PrDocType]);
    }

    [Fact]
    public void Opex_service_gets_item_category_D()
    {
        var result = Evaluate(TestData.Line(item: ItemType.Service, materialGroup: "IT10") with { Uom = "AU" });

        Assert.True(result.IsValid);
        Assert.Equal("D", result.Resolved.ItemCategory);
        Assert.Equal("61300000", result.Resolved.GlAccount);
        Assert.Equal("P02", result.Resolved.PurchasingGroup);
    }

    [Fact]
    public void Capex_on_wbs_uses_the_fixed_auc_account()
    {
        var result = Evaluate(TestData.Line(budget: BudgetType.Capex, costObject: CostObjectType.Wbs, costCenter: null, wbs: "C-2026-IT-001"));

        Assert.True(result.IsValid, string.Join("; ", result.Messages.Select(m => m.Text)));
        Assert.Equal("ZCAP", result.Resolved.PrDocType);
        Assert.Equal("P", result.Resolved.AcctAssignmentCategory);
        Assert.Equal(PlaceholderRules.AucAccount, result.Resolved.GlAccount);
    }

    [Fact]
    public void Capex_on_cost_center_requires_an_asset_number()
    {
        var result = Evaluate(TestData.Line(budget: BudgetType.Capex));

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Field == Fields.AssetNumber && m.Severity == Severity.Error);
        Assert.Equal("A", result.Resolved.AcctAssignmentCategory);
        Assert.Null(result.Resolved.GlAccount);
    }

    [Fact]
    public void Material_master_overrides_what_the_requestor_typed()
    {
        // Requestor claims a non-stock service in group IT10; the material master says stock material in GA01.
        var result = Evaluate(TestData.Line(item: ItemType.Service, materialNumber: "100000401", materialGroup: "IT10") with { IsStock = false, Uom = "EA" });

        Assert.Equal(ItemType.Material, result.Resolved.ItemType);
        Assert.True(result.Resolved.IsStock);
        Assert.Equal("GA01", result.Resolved.MaterialGroup);
        Assert.Equal("BOX", result.Resolved.Uom);
        Assert.Equal("R02", result.MatchedRuleCode);
        Assert.Equal("", result.Resolved.AcctAssignmentCategory);
        Assert.Null(result.Resolved.GlAccount);
        Assert.Contains(Fields.IsStock, result.LockedFields);
        Assert.Contains(Fields.MaterialGroup, result.LockedFields);
    }

    [Fact]
    public void A_wrong_suggestion_cannot_produce_a_forbidden_combination()
    {
        // CAPEX for a stock material is forbidden however the line is filled in.
        var result = Evaluate(TestData.Line(budget: BudgetType.Capex, materialNumber: "100000401"));

        Assert.False(result.IsValid);
        Assert.Null(result.MatchedRuleCode);
        Assert.Null(result.Resolved.PrDocType);
        Assert.Contains(result.Messages, m => m.Severity == Severity.Error && m.Text.Contains("CAPEX purchases cannot go to stock"));
    }

    [Fact]
    public void Free_text_lines_are_never_stock()
    {
        var result = Evaluate(TestData.Line() with { IsStock = true });

        Assert.False(result.Resolved.IsStock);
        Assert.Contains(Fields.IsStock, result.LockedFields);
        Assert.Equal("R01", result.MatchedRuleCode);
    }

    [Fact]
    public void Material_group_of_the_wrong_item_type_is_an_error()
    {
        var result = Evaluate(TestData.Line(item: ItemType.Service, materialGroup: "IT01"));

        Assert.Contains(result.Messages, m => m.Field == Fields.MaterialGroup && m.Severity == Severity.Error);
    }

    [Fact]
    public void Unmapped_material_group_is_an_error_not_a_silent_blank()
    {
        var result = Evaluate(TestData.Line(materialGroup: "XX99"));

        Assert.Contains(result.Messages, m => m.Field == Fields.PurchasingGroup && m.Severity == Severity.Error);
        Assert.Contains(result.Messages, m => m.Field == Fields.GlAccount && m.Severity == Severity.Error);
    }

    [Fact]
    public void Wbs_of_the_other_budget_type_is_an_error()
    {
        var result = Evaluate(TestData.Line(costObject: CostObjectType.Wbs, costCenter: null, wbs: "C-2026-IT-001"));

        Assert.Contains(result.Messages, m => m.Field == Fields.Wbs && m.Text.Contains("CAPEX element"));
    }

    [Fact]
    public void Unknown_master_data_is_rejected()
    {
        var result = Evaluate(TestData.Line(materialNumber: "999", costCenter: "NOPE") with { Uom = "ZZ" });

        Assert.Contains(result.Messages, m => m.Field == Fields.MaterialNumber && m.Severity == Severity.Error);
        Assert.Contains(result.Messages, m => m.Field == Fields.CostCenter && m.Severity == Severity.Error);
        Assert.Contains(result.Messages, m => m.Field == Fields.Uom && m.Severity == Severity.Error);
    }

    [Fact]
    public void Cost_object_fields_the_rule_does_not_use_are_cleared_and_locked()
    {
        var result = Evaluate(TestData.Line(costObject: CostObjectType.Wbs, costCenter: "CC1101", wbs: "O-2026-IT-001", asset: "200000001"));

        Assert.True(result.IsValid, string.Join("; ", result.Messages.Select(m => m.Text)));
        Assert.Equal("O-2026-IT-001", result.Resolved.Wbs);
        Assert.Null(result.Resolved.CostCenter);
        Assert.Null(result.Resolved.AssetNumber);
        Assert.Contains(Fields.CostCenter, result.LockedFields);
        Assert.Contains(Fields.AssetNumber, result.LockedFields);
    }

    [Fact]
    public void Incomplete_line_reports_missing_fields_and_matches_no_rule()
    {
        var result = Evaluate(new LineInput { LineNo = 3 });

        Assert.False(result.IsValid);
        Assert.Null(result.MatchedRuleCode);
        Assert.Contains(result.Messages, m => m.Field == Fields.BudgetType);
        Assert.Contains(result.Messages, m => m.Field == Fields.Quantity);
        Assert.All(result.Messages, m => Assert.Equal(3, m.LineNo));
    }

    [Fact]
    public void Past_delivery_date_and_bad_quantity_are_errors()
    {
        var result = Evaluate(TestData.Line() with { Quantity = 0, DeliveryDate = TestData.Today.AddDays(-1) });

        Assert.Contains(result.Messages, m => m.Field == Fields.Quantity && m.Severity == Severity.Error);
        Assert.Contains(result.Messages, m => m.Field == Fields.DeliveryDate && m.Severity == Severity.Error);
    }

    [Fact]
    public void Allowed_options_cascade_from_what_is_already_known()
    {
        // A stock material can only be OPEX: CAPEX is offered for nothing.
        var stock = Evaluate(new LineInput { LineNo = 1, MaterialNumber = "100000401" });
        Assert.Equal(["Opex"], stock.AllowedOptions[Fields.BudgetType]);
        Assert.Equal(["Material"], stock.AllowedOptions[Fields.ItemType]);
        Assert.Equal(["true"], stock.AllowedOptions[Fields.IsStock]);
        Assert.Equal(["CostCenter", "Wbs"], stock.AllowedOptions[Fields.CostObjectType]);

        // A free-text line is non-stock, so everything else stays open.
        var freeText = Evaluate(new LineInput { LineNo = 1 });
        Assert.Equal(["Opex", "Capex"], freeText.AllowedOptions[Fields.BudgetType]);
        Assert.Equal(["Material", "Service"], freeText.AllowedOptions[Fields.ItemType]);
        Assert.Equal(["false"], freeText.AllowedOptions[Fields.IsStock]);
    }

    [Fact]
    public void More_specific_rule_wins_then_priority()
    {
        var rules = new List<PrRule>
        {
            Rule("GENERIC", 0, r => r.PrDocType = "GEN"),
            Rule("OPEX", 0, r => { r.PrDocType = "OPX"; r.Conditions[Fields.BudgetType] = "Opex"; }),
            Rule("OPEX-HI", 5, r => { r.PrDocType = "OPH"; r.Conditions[Fields.BudgetType] = "Opex"; }),
        };

        Assert.Equal("OPH", Evaluate(TestData.Line(), rules).Resolved.PrDocType);
        Assert.Equal("GEN", Evaluate(TestData.Line(budget: BudgetType.Capex), rules).Resolved.PrDocType);
    }

    [Fact]
    public void Rules_outside_their_validity_or_inactive_are_ignored()
    {
        var rules = new List<PrRule>
        {
            Rule("OLD", 9, r => { r.PrDocType = "OLD"; r.ValidTo = TestData.Today.AddDays(-1); }),
            Rule("FUTURE", 9, r => { r.PrDocType = "FUT"; r.ValidFrom = TestData.Today.AddDays(1); }),
            Rule("OFF", 9, r => { r.PrDocType = "OFF"; r.IsActive = false; }),
            Rule("LIVE", 0, r => r.PrDocType = "LIV"),
        };

        Assert.Equal("LIV", Evaluate(TestData.Line(), rules).Resolved.PrDocType);
    }

    [Fact]
    public void No_matching_rule_is_an_error()
    {
        var result = Evaluate(TestData.Line(), []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Text.StartsWith("No rule covers"));
    }

    private static PrRule Rule(string code, int priority, Action<PrRule> configure)
    {
        var rule = new PrRule
        {
            Id = Guid.NewGuid(), Code = code, Description = code, Priority = priority, Allowed = true,
            AcctAssignmentCategory = "K", ItemCategory = "", GlMode = GlMode.None,
            RequiredFields = [Fields.CostCenter], ValidFrom = new DateOnly(2000, 1, 1),
        };
        configure(rule);
        return rule;
    }
}

public class SplitPlannerTests
{
    private static ResolvedLine Line(int no, ItemType item, string purchasingGroup, BudgetType budget = BudgetType.Opex, string docType = "NB", decimal price = 100) => new()
    {
        LineNo = no, ItemType = item, PurchasingGroup = purchasingGroup, BudgetType = budget, PrDocType = docType, Quantity = 1, UnitPrice = price,
    };

    [Fact]
    public void Lines_sharing_every_key_stay_in_one_pr()
    {
        var groups = SplitPlanner.Plan([Line(1, ItemType.Material, "P01"), Line(2, ItemType.Material, "P01", price: 250)]);

        var group = Assert.Single(groups);
        Assert.Equal([1, 2], group.LineNos);
        Assert.Equal(350, group.Total);
    }

    [Fact]
    public void Mixing_materials_and_services_splits()
    {
        var groups = SplitPlanner.Plan([Line(1, ItemType.Material, "P01"), Line(2, ItemType.Service, "P01"), Line(3, ItemType.Material, "P01")]);

        Assert.Equal(2, groups.Count);
        Assert.Equal([1, 3], groups[0].LineNos);
        Assert.Equal([2], groups[1].LineNos);
    }

    [Fact]
    public void Different_purchasing_group_or_budget_type_splits()
    {
        var groups = SplitPlanner.Plan([
            Line(1, ItemType.Material, "P01"),
            Line(2, ItemType.Material, "P03"),
            Line(3, ItemType.Material, "P01", BudgetType.Capex, "ZCAP"),
        ]);

        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void Document_type_always_splits_even_when_not_listed_as_a_key()
    {
        var groups = SplitPlanner.Plan([Line(1, ItemType.Material, "P01", docType: "NB"), Line(2, ItemType.Material, "P01", docType: "ZX")], [Fields.ItemType]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Keys_are_configurable_and_unknown_keys_are_refused()
    {
        var lines = new[] { Line(1, ItemType.Material, "P01"), Line(2, ItemType.Material, "P03") };

        Assert.Single(SplitPlanner.Plan(lines, [Fields.ItemType]));
        Assert.Throws<DomainException>(() => SplitPlanner.Plan(lines, ["shortText"]));
    }
}

public class BudgetCheckerTests
{
    private static ResolvedLine Line(int no, decimal amount, string? costCenter = null, string? wbs = null) => new()
    {
        LineNo = no, Quantity = 1, UnitPrice = amount, CostCenter = costCenter, Wbs = wbs,
    };

    private static Dictionary<(CostObjectType, string), BudgetPosition?> Positions(params BudgetPosition[] positions) =>
        positions.ToDictionary(p => (p.ObjectType, p.ObjectCode), p => (BudgetPosition?)p);

    [Fact]
    public void Lines_on_the_same_object_are_summed_and_flagged_when_over()
    {
        var checks = BudgetChecker.Check(
            [Line(1, 60, "CC1"), Line(2, 50, "CC1")], "IDR",
            Positions(new BudgetPosition(CostObjectType.CostCenter, "CC1", "IDR", 500, 200, 200)));

        var check = Assert.Single(checks);
        Assert.True(check.IsOver);
        Assert.Equal(110, check.Requested);
        Assert.Equal(100, check.Remaining);
        Assert.Equal([1, 2], check.LineNos);
    }

    [Fact]
    public void Within_budget_is_not_flagged()
    {
        var checks = BudgetChecker.Check([Line(1, 100, wbs: "W1")], "IDR", Positions(new BudgetPosition(CostObjectType.Wbs, "W1", "IDR", 500, 200, 200)));

        Assert.False(Assert.Single(checks).IsOver);
    }

    [Fact]
    public void Missing_figure_or_other_currency_is_reported_but_never_flagged_over()
    {
        var checks = BudgetChecker.Check(
            [Line(1, 999, "CC1"), Line(2, 999, wbs: "W1")], "IDR",
            Positions(new BudgetPosition(CostObjectType.Wbs, "W1", "USD", 10, 0, 0)));

        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.False(c.IsOver));
        Assert.Null(checks.Single(c => c.ObjectCode == "CC1").Remaining);
    }

    [Fact]
    public void Lines_without_a_cost_object_are_skipped()
    {
        Assert.Empty(BudgetChecker.Check([Line(1, 100)], "IDR", Positions()));
    }
}
