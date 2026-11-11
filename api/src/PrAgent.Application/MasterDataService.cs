using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrAgent.Application.Data;
using PrAgent.Application.Sap;
using PrAgent.Domain;
using PrAgent.Domain.Rules;

namespace PrAgent.Application;

public sealed record MasterDataOption(string Code, string Name, IReadOnlyDictionary<string, object?>? Meta = null);

/// <summary>Lets an admin drop every cached SAP read at once.</summary>
public sealed class MasterDataCacheState
{
    private int _generation;
    public int Generation => _generation;
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public void Invalidate(DateTimeOffset now)
    {
        Interlocked.Increment(ref _generation);
        InvalidatedAt = now;
    }
}

/// <summary>
/// The application's view of SAP master data: reads go through <see cref="ISapReadGateway"/> and are
/// cached in memory for a short time. Budget figures are cached for one minute only.
/// </summary>
public sealed class MasterDataService(ISapReadGateway sap, AppDbContext db, IMemoryCache cache, MasterDataCacheState state)
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ItemTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BudgetTtl = TimeSpan.FromMinutes(1);

    public static readonly string[] Kinds =
        ["plants", "uoms", "purchasing-groups", "gl-accounts", "material-groups", "materials", "cost-centers", "wbs", "assets"];

    public Task<IReadOnlyList<SapCodeName>> PlantsAsync(CancellationToken ct) => Cached("plants", ListTtl, () => sap.GetPlantsAsync(ct));
    public Task<IReadOnlyList<SapCodeName>> UnitsOfMeasureAsync(CancellationToken ct) => Cached("uoms", ListTtl, () => sap.GetUnitsOfMeasureAsync(ct));
    public Task<IReadOnlyList<SapCodeName>> PurchasingGroupsAsync(CancellationToken ct) => Cached("pgroups", ListTtl, () => sap.GetPurchasingGroupsAsync(ct));
    public Task<IReadOnlyList<SapCodeName>> GlAccountsAsync(CancellationToken ct) => Cached("gl", ListTtl, () => sap.GetGlAccountsAsync(ct));

    public Task<SapMaterial?> MaterialAsync(string number, CancellationToken ct) => Cached($"mat:{Key(number)}", ItemTtl, () => sap.GetMaterialAsync(number.Trim(), ct));
    public Task<SapCostCenter?> CostCenterAsync(string code, CancellationToken ct) => Cached($"cc:{Key(code)}", ItemTtl, () => sap.GetCostCenterAsync(code.Trim(), ct));
    public Task<SapWbsElement?> WbsElementAsync(string code, CancellationToken ct) => Cached($"wbs:{Key(code)}", ItemTtl, () => sap.GetWbsElementAsync(code.Trim(), ct));
    public Task<SapAsset?> AssetAsync(string number, CancellationToken ct) => Cached($"asset:{Key(number)}", ItemTtl, () => sap.GetAssetAsync(number.Trim(), ct));

    public Task<SapBudget?> BudgetAsync(CostObjectType type, string code, CancellationToken ct) =>
        Cached($"budget:{type}:{Key(code)}", BudgetTtl, () => sap.GetBudgetAsync(type, code.Trim(), ct));

    /// <summary>SAP material groups joined with the admin-maintained mapping (item type, default G/L, purchasing group).</summary>
    public async Task<IReadOnlyList<MaterialGroupInfo>> MaterialGroupsAsync(CancellationToken ct)
    {
        var groups = await Cached("mgroups", ListTtl, () => sap.GetMaterialGroupsAsync(ct));
        var mappings = await db.MaterialGroupMappings.AsNoTracking().ToDictionaryAsync(m => m.Code, StringComparer.OrdinalIgnoreCase, ct);
        return groups
            .Select(g => mappings.TryGetValue(g.Code, out var m)
                ? new MaterialGroupInfo(g.Code, g.Name, m.ItemType, m.DefaultGlAccount, m.DefaultPurchasingGroup)
                : new MaterialGroupInfo(g.Code, g.Name, null, null, null))
            .ToList();
    }

    /// <summary>One search entry point for the form's pickers and the agent's lookup tool.</summary>
    public async Task<IReadOnlyList<MasterDataOption>> SearchAsync(string kind, string? query, int max, CancellationToken ct)
    {
        max = Math.Clamp(max, 1, 200);
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        switch (kind.ToLowerInvariant())
        {
            case "plants": return Filter(await PlantsAsync(ct), query, max);
            case "uoms": return Filter(await UnitsOfMeasureAsync(ct), query, max);
            case "purchasing-groups": return Filter(await PurchasingGroupsAsync(ct), query, max);
            case "gl-accounts": return Filter(await GlAccountsAsync(ct), query, max);
            case "material-groups":
                return (await MaterialGroupsAsync(ct))
                    .Where(g => Matches(query, g.Code, g.Name))
                    .Take(max)
                    .Select(g => new MasterDataOption(g.Code, g.Name, new Dictionary<string, object?>
                    {
                        ["itemType"] = g.ItemType?.ToString(),
                        ["defaultGlAccount"] = g.DefaultGlAccount,
                        ["defaultPurchasingGroup"] = g.DefaultPurchasingGroup,
                    }))
                    .ToList();
            case "materials":
                return (await sap.SearchMaterialsAsync(query, max, ct))
                    .Select(m => new MasterDataOption(m.Number, m.Description, new Dictionary<string, object?>
                    {
                        ["materialGroup"] = m.MaterialGroup,
                        ["baseUom"] = m.BaseUom,
                        ["isStock"] = m.IsStock,
                    }))
                    .ToList();
            case "cost-centers":
                return (await sap.SearchCostCentersAsync(query, max, ct))
                    .Select(c => new MasterDataOption(c.Code, c.Name, new Dictionary<string, object?> { ["companyCode"] = c.CompanyCode }))
                    .ToList();
            case "wbs":
                return (await sap.SearchWbsElementsAsync(query, max, ct))
                    .Select(w => new MasterDataOption(w.Code, w.Name, new Dictionary<string, object?> { ["budgetType"] = w.BudgetType?.ToString() }))
                    .ToList();
            case "assets":
                return (await sap.SearchAssetsAsync(query, max, ct))
                    .Select(a => new MasterDataOption(a.Number, a.Description, new Dictionary<string, object?>
                    {
                        ["assetClass"] = a.AssetClass,
                        ["costCenter"] = a.CostCenter,
                    }))
                    .ToList();
            default:
                throw new NotFoundException($"Unknown master data type '{kind}'. Valid types: {string.Join(", ", Kinds)}.");
        }
    }

    /// <summary>Loads exactly the master data the rules engine needs to evaluate these lines.</summary>
    public async Task<MasterDataSnapshot> SnapshotForAsync(IEnumerable<LineInput> lines, CancellationToken ct)
    {
        var list = lines.ToList();
        var snapshot = new MasterDataSnapshot();

        foreach (var group in await MaterialGroupsAsync(ct)) snapshot.MaterialGroups[group.Code] = group;
        foreach (var uom in await UnitsOfMeasureAsync(ct)) snapshot.UnitsOfMeasure.Add(uom.Code);

        foreach (var number in Distinct(list.Select(l => l.MaterialNumber)))
            if (await MaterialAsync(number, ct) is { } m)
                snapshot.Materials[number] = new MaterialInfo(m.Number, m.Description, m.MaterialGroup, m.BaseUom, m.IsStock);

        foreach (var code in Distinct(list.Select(l => l.CostCenter)))
            if (await CostCenterAsync(code, ct) is { } c)
                snapshot.CostCenters[code] = c.Name;

        foreach (var code in Distinct(list.Select(l => l.Wbs)))
            if (await WbsElementAsync(code, ct) is { } w)
                snapshot.WbsElements[code] = new WbsInfo(w.Code, w.Name, w.BudgetType);

        foreach (var number in Distinct(list.Select(l => l.AssetNumber)))
            if (await AssetAsync(number, ct) is { } a)
                snapshot.Assets[number] = a.Description;

        return snapshot;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    private static List<MasterDataOption> Filter(IEnumerable<SapCodeName> items, string? query, int max) =>
        items.Where(i => Matches(query, i.Code, i.Name)).Take(max).Select(i => new MasterDataOption(i.Code, i.Name)).ToList();

    private static bool Matches(string? query, string code, string name) =>
        query is null
        || code.Contains(query, StringComparison.OrdinalIgnoreCase)
        || name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string Key(string code) => code.Trim().ToUpperInvariant();

    private async Task<T> Cached<T>(string key, TimeSpan ttl, Func<Task<T>> load)
    {
        var cacheKey = $"sap:{state.Generation}:{key}";
        if (cache.TryGetValue(cacheKey, out T? hit)) return hit!;
        var value = await load();
        cache.Set(cacheKey, value, ttl);
        return value;
    }
}
