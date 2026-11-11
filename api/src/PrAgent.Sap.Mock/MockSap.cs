using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrAgent.Application.Sap;
using PrAgent.Domain;

namespace PrAgent.Sap.Mock;

public sealed class MockSapOptions
{
    public const string Section = "Sap:Mock";

    /// <summary>
    /// File where simulated PRs are kept, so idempotency survives a restart the way it would in SAP.
    /// Null keeps them in memory only.
    /// </summary>
    public string? StatePath { get; set; }

    /// <summary>A PR whose item text contains this marker is rejected, to demonstrate failure and retry. Null disables it.</summary>
    public string? FailWhenShortTextContains { get; set; } = "#SAPFAIL";
}

public static class MockSapRegistration
{
    public static IServiceCollection AddMockSap(this IServiceCollection services)
    {
        services.AddOptions<MockSapOptions>().BindConfiguration(MockSapOptions.Section);
        services.AddSingleton(MockSapData.LoadEmbedded());
        services.AddSingleton<ISapReadGateway, MockSapReadGateway>();
        services.AddSingleton<MockSapLedger>();
        services.AddSingleton<ISapPrWriter, MockSapPrWriter>();
        return services;
    }
}

/// <summary>The fictional SAP data set in Seed/sap-seed.json.</summary>
public sealed class MockSapData
{
    public List<SapCodeName> Plants { get; init; } = [];
    public List<SapCodeName> UnitsOfMeasure { get; init; } = [];
    public List<SapCodeName> PurchasingGroups { get; init; } = [];
    public List<GlAccountRow> GlAccounts { get; init; } = [];
    public List<SapCodeName> MaterialGroups { get; init; } = [];
    public List<SapMaterial> Materials { get; init; } = [];
    public List<SapCostCenter> CostCenters { get; init; } = [];
    public List<SapWbsElement> WbsElements { get; init; } = [];
    public List<SapAsset> Assets { get; init; } = [];
    public List<BudgetRow> Budgets { get; init; } = [];

    public sealed record GlAccountRow(string Number, string Name);
    public sealed record BudgetRow(CostObjectType ObjectType, string ObjectCode, string Currency, decimal Budget, decimal Committed, decimal Actual);

    public static MockSapData LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("sap-seed.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<MockSapData>(stream, options) ?? throw new InvalidOperationException("sap-seed.json is empty.");
    }
}

/// <summary>SIMULATED SAP reads from seed data. Stands in for the real gateway until the connection exists.</summary>
public sealed class MockSapReadGateway(MockSapData data, TimeProvider time) : ISapReadGateway
{
    public Task<IReadOnlyList<SapCodeName>> GetPlantsAsync(CancellationToken ct) => List(data.Plants);
    public Task<IReadOnlyList<SapCodeName>> GetUnitsOfMeasureAsync(CancellationToken ct) => List(data.UnitsOfMeasure);
    public Task<IReadOnlyList<SapCodeName>> GetPurchasingGroupsAsync(CancellationToken ct) => List(data.PurchasingGroups);
    public Task<IReadOnlyList<SapCodeName>> GetMaterialGroupsAsync(CancellationToken ct) => List(data.MaterialGroups);
    public Task<IReadOnlyList<SapCodeName>> GetGlAccountsAsync(CancellationToken ct) =>
        List(data.GlAccounts.Select(g => new SapCodeName(g.Number, g.Name)).ToList());

    public Task<IReadOnlyList<SapMaterial>> SearchMaterialsAsync(string? query, int max, CancellationToken ct) =>
        Search(data.Materials, query, max, m => m.Number, m => m.Description);
    public Task<SapMaterial?> GetMaterialAsync(string number, CancellationToken ct) => Find(data.Materials, number, m => m.Number);

    public Task<IReadOnlyList<SapCostCenter>> SearchCostCentersAsync(string? query, int max, CancellationToken ct) =>
        Search(data.CostCenters, query, max, c => c.Code, c => c.Name);
    public Task<SapCostCenter?> GetCostCenterAsync(string code, CancellationToken ct) => Find(data.CostCenters, code, c => c.Code);

    public Task<IReadOnlyList<SapWbsElement>> SearchWbsElementsAsync(string? query, int max, CancellationToken ct) =>
        Search(data.WbsElements, query, max, w => w.Code, w => w.Name);
    public Task<SapWbsElement?> GetWbsElementAsync(string code, CancellationToken ct) => Find(data.WbsElements, code, w => w.Code);

    public Task<IReadOnlyList<SapAsset>> SearchAssetsAsync(string? query, int max, CancellationToken ct) =>
        Search(data.Assets, query, max, a => a.Number, a => a.Description);
    public Task<SapAsset?> GetAssetAsync(string number, CancellationToken ct) => Find(data.Assets, number, a => a.Number);

    public Task<SapBudget?> GetBudgetAsync(CostObjectType objectType, string code, CancellationToken ct)
    {
        var row = data.Budgets.FirstOrDefault(b => b.ObjectType == objectType && b.ObjectCode.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(row is null
            ? null
            : new SapBudget(row.ObjectType, row.ObjectCode, row.Currency, row.Budget, row.Committed, row.Actual, time.GetUtcNow()));
    }

    private static Task<IReadOnlyList<T>> List<T>(List<T> items) => Task.FromResult<IReadOnlyList<T>>(items);

    private static Task<T?> Find<T>(List<T> items, string code, Func<T, string> key) where T : class =>
        Task.FromResult(items.FirstOrDefault(i => key(i).Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static Task<IReadOnlyList<T>> Search<T>(List<T> items, string? query, int max, Func<T, string> code, Func<T, string> name)
    {
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        IReadOnlyList<T> result = items
            .Where(i => q is null || code(i).Contains(q, StringComparison.OrdinalIgnoreCase) || name(i).Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(0, max))
            .ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
/// SIMULATED SAP PR creation. It validates the payload roughly the way SAP would, hands out PR numbers
/// in the 90xxxxxxxx range, and honours the idempotency key. No purchase requisition exists anywhere.
/// </summary>
public sealed class MockSapPrWriter(MockSapData data, MockSapLedger ledger, IOptions<MockSapOptions> options) : ISapPrWriter
{
    private readonly MockSapData _data = data;
    private readonly MockSapOptions _options = options.Value;

    public bool IsSimulated => true;

    public Task<SapPrCreateResult> CreatePurchaseRequisitionAsync(SapPrCreateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var number = ledger.GetOrCreate(request, () => Validate(request));
        return Task.FromResult(new SapPrCreateResult(number, true));
    }

    public Task LinkAttachmentAsync(string prNumber, SapAttachment attachment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ledger.LinkAttachment(prNumber, attachment.FileName);
        return Task.CompletedTask;
    }

    private void Validate(SapPrCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentType)) throw new SapException("Document type is missing.");
        if (request.Items.Count == 0) throw new SapException("A purchase requisition needs at least one item.");

        foreach (var item in request.Items)
        {
            string At(string text) => $"Item {item.ItemNo}: {text}";

            if (_options.FailWhenShortTextContains is { Length: > 0 } marker && item.ShortText.Contains(marker, StringComparison.OrdinalIgnoreCase))
                throw new SapException(At($"simulated SAP failure (text contains {marker})."), isTransient: true);

            if (item.Quantity <= 0) throw new SapException(At("quantity must be greater than zero."));
            if (_data.Plants.All(p => p.Code != item.Plant)) throw new SapException(At($"plant {item.Plant} does not exist."));
            if (_data.PurchasingGroups.All(p => p.Code != item.PurchasingGroup)) throw new SapException(At($"purchasing group {item.PurchasingGroup} does not exist."));
            if (_data.MaterialGroups.All(g => g.Code != item.MaterialGroup)) throw new SapException(At($"material group {item.MaterialGroup} does not exist."));
            if (item.MaterialNumber is not null && _data.Materials.All(m => m.Number != item.MaterialNumber))
                throw new SapException(At($"material {item.MaterialNumber} does not exist."));

            switch (item.AcctAssignmentCategory)
            {
                case "K" when item.CostCenter is null: throw new SapException(At("account assignment K needs a cost center."));
                case "K" or "P" when item.GlAccount is null: throw new SapException(At($"account assignment {item.AcctAssignmentCategory} needs a G/L account."));
                case "P" or "Q" when item.Wbs is null: throw new SapException(At($"account assignment {item.AcctAssignmentCategory} needs a WBS element."));
                case "A" when item.AssetNumber is null: throw new SapException(At("account assignment A needs an asset number."));
                case "" or "Q" when item.MaterialNumber is null: throw new SapException(At("a stock item needs a material number."));
            }
        }
    }

}

/// <summary>
/// The simulated PRs the mock has handed out: its stand-in for SAP's own database. Kept apart from
/// the writer so the admin monitor can show it without touching the write port.
/// </summary>
public sealed class MockSapLedger
{
    private readonly ConcurrentDictionary<Guid, StoredPr> _byKey = new();
    private readonly Lock _gate = new();
    private readonly string? _statePath;
    private long _lastNumber = 9_000_000_000;

    public MockSapLedger(IOptions<MockSapOptions> options)
    {
        _statePath = string.IsNullOrWhiteSpace(options.Value.StatePath) ? null : options.Value.StatePath;
        Load();
    }

    /// <summary>Everything "created" so far: the exact payloads that would have gone to SAP.</summary>
    public IReadOnlyList<StoredPr> Created => _byKey.Values.OrderBy(p => p.PrNumber, StringComparer.Ordinal).ToList();

    /// <summary>Returns the PR number already issued for this idempotency key, or validates and issues a new one.</summary>
    public string GetOrCreate(SapPrCreateRequest request, Action validate)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
                return existing.PrNumber;

            validate();
            var number = (++_lastNumber).ToString();
            _byKey[request.IdempotencyKey] = new StoredPr(number, request, []);
            Save();
            return number;
        }
    }

    public void LinkAttachment(string prNumber, string fileName)
    {
        lock (_gate)
        {
            var entry = _byKey.FirstOrDefault(p => p.Value.PrNumber == prNumber);
            if (entry.Value is null) throw new SapException($"PR {prNumber} does not exist.");
            if (entry.Value.Attachments.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return;

            _byKey[entry.Key] = entry.Value with { Attachments = [.. entry.Value.Attachments, fileName] };
            Save();
        }
    }

    private void Load()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), StateJson);
        if (state is null) return;
        _lastNumber = Math.Max(_lastNumber, state.LastNumber);
        foreach (var pr in state.Prs) _byKey[pr.Request.IdempotencyKey] = pr;
    }

    private void Save()
    {
        if (_statePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(new State(_lastNumber, [.. _byKey.Values]), StateJson));
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record StoredPr(string PrNumber, SapPrCreateRequest Request, IReadOnlyList<string> Attachments);

    private sealed record State(long LastNumber, List<StoredPr> Prs);
}
