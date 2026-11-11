using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Domain.Rules;

namespace PrAgent.Application;

/// <summary>A request as the requestor is filling it in. Every field may still be empty.</summary>
public sealed record DraftInput
{
    public string? Title { get; init; }
    public string? Justification { get; init; }
    public string? Plant { get; init; }
    public string? Currency { get; init; }
    public Guid? SectionId { get; init; }
    public IReadOnlyList<LineInput> Lines { get; init; } = [];
}

public sealed record EvaluationResult(
    IReadOnlyList<LineEvaluation> Lines,
    IReadOnlyList<ValidationMessage> Messages,
    IReadOnlyList<SplitGroup> Splits,
    IReadOnlyList<BudgetCheck> Budget,
    bool CanSubmit,
    bool UsesPlaceholderRules);

/// <summary>
/// Runs the rules engine over a whole draft and adds the split preview and the budget warning.
/// The form calls it on every change, the agent calls it to preview, and the server calls it again
/// on submit and on approval, so nothing the browser or the agent sends is trusted.
/// </summary>
public sealed class EvaluationService(
    AppDbContext db,
    MasterDataService masterData,
    RulesEngine engine,
    BusinessClock clock,
    IOptions<AppOptions> options)
{
    public const string HeaderTitle = "title";
    public const string HeaderPlant = "plant";
    public const string HeaderSection = "sectionId";
    public const string HeaderLines = "lines";

    public async Task<EvaluationResult> EvaluateAsync(DraftInput draft, CancellationToken ct)
    {
        var messages = new List<ValidationMessage>();
        var lines = draft.Lines ?? [];
        var today = clock.Today;
        var currency = string.IsNullOrWhiteSpace(draft.Currency) ? "IDR" : draft.Currency.Trim().ToUpperInvariant();

        // Header
        if (string.IsNullOrWhiteSpace(draft.Title))
            messages.Add(new(Severity.Error, HeaderTitle, "Give the request a short title."));

        if (string.IsNullOrWhiteSpace(draft.Plant))
            messages.Add(new(Severity.Error, HeaderPlant, "Plant is required."));
        else if ((await masterData.PlantsAsync(ct)).All(p => !p.Code.Equals(draft.Plant.Trim(), StringComparison.OrdinalIgnoreCase)))
            messages.Add(new(Severity.Error, HeaderPlant, $"Plant {draft.Plant.Trim()} was not found in SAP."));

        if (draft.SectionId is null)
        {
            messages.Add(new(Severity.Error, HeaderSection, "Choose the section this request belongs to."));
        }
        else
        {
            var section = await db.Sections.AsNoTracking().Include(s => s.Reviewers).FirstOrDefaultAsync(s => s.Id == draft.SectionId, ct);
            if (section is null)
                messages.Add(new(Severity.Error, HeaderSection, "That section no longer exists."));
            else if (section.Reviewers.Count == 0)
                messages.Add(new(Severity.Error, HeaderSection, $"Section {section.Name} has no reviewer yet. Ask the PR admin to assign one."));
        }

        if (lines.Count == 0)
            messages.Add(new(Severity.Error, HeaderLines, "Add at least one line."));
        else if (lines.GroupBy(l => l.LineNo).Any(g => g.Count() > 1))
            messages.Add(new(Severity.Error, HeaderLines, "Line numbers must be unique."));

        // Lines
        var rules = await db.Rules.AsNoTracking().ToListAsync(ct);
        var snapshot = await masterData.SnapshotForAsync(lines, ct);
        var evaluations = lines.OrderBy(l => l.LineNo).Select(l => engine.EvaluateLine(l, rules, snapshot, today)).ToList();
        var resolved = evaluations.Select(e => e.Resolved).ToList();

        // Split preview: only lines that already know what they are.
        var splits = SplitPlanner.Plan(
            resolved.Where(l => l.ItemType is not null && l.BudgetType is not null && l.PurchasingGroup is not null && l.PrDocType is not null),
            options.Value.SplitKeys);
        if (splits.Count > 1)
            messages.Add(new(Severity.Info, null, $"This request will be created as {splits.Count} separate SAP PRs, because its lines differ in item type, purchasing group, budget type or document type."));

        // Budget: warn, never block.
        var positions = new Dictionary<(CostObjectType, string), BudgetPosition?>();
        foreach (var target in resolved.Select(BudgetChecker.BudgetObject).Where(o => o is not null).Select(o => o!.Value).Distinct())
        {
            var key = (target.Type, target.Code.ToUpperInvariant());
            if (positions.ContainsKey(key)) continue;
            var budget = await masterData.BudgetAsync(target.Type, target.Code, ct);
            positions[key] = budget is null ? null : new BudgetPosition(budget.ObjectType, budget.ObjectCode, budget.Currency, budget.Budget, budget.Committed, budget.Actual);
        }

        var budgetChecks = BudgetChecker.Check(resolved, currency, positions);
        foreach (var over in budgetChecks.Where(b => b.IsOver))
            messages.Add(new(Severity.Warning, null, over.Text));

        var usesPlaceholders = evaluations.Any(e => e.MatchedRuleIsPlaceholder);
        if (usesPlaceholders)
            messages.Add(new(Severity.Info, null, "The SAP fields on this request come from placeholder rules. The real rule table has not been loaded yet."));

        var canSubmit = messages.All(m => m.Severity != Severity.Error) && evaluations.All(e => e.IsValid);
        return new EvaluationResult(evaluations, messages, splits, budgetChecks, canSubmit, usesPlaceholders);
    }
}
