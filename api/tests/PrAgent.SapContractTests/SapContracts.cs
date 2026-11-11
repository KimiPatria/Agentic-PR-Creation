using PrAgent.Application.Sap;
using PrAgent.Domain;

namespace PrAgent.SapContractTests;

// -------------------------------------------------------------------------------------------------
// THE SAP CONTRACT
//
// Any implementation of ISapReadGateway and ISapPrWriter must pass these tests. They are written
// against the interfaces only. To certify a new adapter, add a subclass that builds the adapter and
// names sample data that exists in the system it talks to (see MockSapContractTests, and the skipped
// S4HanaContractTests for where the real one goes).
// -------------------------------------------------------------------------------------------------

/// <summary>Sample data the contract tests need to exist in the SAP system under test.</summary>
public sealed record SapSamples(
    string Plant,
    string PurchasingGroup,
    string MaterialGroup,
    string GlAccount,
    string Uom,
    string CostCenter,
    string WbsElement,
    string Asset,
    string NonStockMaterial,
    string MaterialSearchTerm);

public abstract class SapReadGatewayContract
{
    protected abstract ISapReadGateway Gateway { get; }
    protected abstract SapSamples Samples { get; }
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Small_lists_are_not_empty_and_have_unique_codes()
    {
        var lists = new Dictionary<string, IReadOnlyList<SapCodeName>>
        {
            ["plants"] = await Gateway.GetPlantsAsync(Ct),
            ["units of measure"] = await Gateway.GetUnitsOfMeasureAsync(Ct),
            ["purchasing groups"] = await Gateway.GetPurchasingGroupsAsync(Ct),
            ["material groups"] = await Gateway.GetMaterialGroupsAsync(Ct),
            ["G/L accounts"] = await Gateway.GetGlAccountsAsync(Ct),
        };

        foreach (var (name, list) in lists)
        {
            Assert.True(list.Count > 0, $"{name} is empty");
            Assert.All(list, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Code), $"{name} has an entry without a code");
                Assert.False(string.IsNullOrWhiteSpace(item.Name), $"{name} {item.Code} has no name");
            });
            Assert.Equal(list.Count, list.Select(i => i.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        Assert.Contains(lists["plants"], p => p.Code == Samples.Plant);
        Assert.Contains(lists["units of measure"], u => u.Code == Samples.Uom);
        Assert.Contains(lists["purchasing groups"], p => p.Code == Samples.PurchasingGroup);
        Assert.Contains(lists["material groups"], g => g.Code == Samples.MaterialGroup);
        Assert.Contains(lists["G/L accounts"], g => g.Code == Samples.GlAccount);
    }

    [Fact]
    public async Task Get_returns_the_entry_for_a_known_key_ignoring_case_and_padding()
    {
        Assert.Equal(Samples.NonStockMaterial, (await Gateway.GetMaterialAsync(Samples.NonStockMaterial, Ct))?.Number);
        Assert.Equal(Samples.CostCenter, (await Gateway.GetCostCenterAsync($" {Samples.CostCenter.ToLowerInvariant()} ", Ct))?.Code);
        Assert.Equal(Samples.WbsElement, (await Gateway.GetWbsElementAsync(Samples.WbsElement.ToLowerInvariant(), Ct))?.Code);
        Assert.Equal(Samples.Asset, (await Gateway.GetAssetAsync(Samples.Asset, Ct))?.Number);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_key_and_does_not_throw()
    {
        Assert.Null(await Gateway.GetMaterialAsync("NO-SUCH-MATERIAL", Ct));
        Assert.Null(await Gateway.GetCostCenterAsync("NO-SUCH-CC", Ct));
        Assert.Null(await Gateway.GetWbsElementAsync("NO-SUCH-WBS", Ct));
        Assert.Null(await Gateway.GetAssetAsync("NO-SUCH-ASSET", Ct));
    }

    [Fact]
    public async Task Search_matches_code_or_name_case_insensitively_and_honours_max()
    {
        var byName = await Gateway.SearchMaterialsAsync(Samples.MaterialSearchTerm.ToUpperInvariant(), 50, Ct);
        Assert.Contains(byName, m => m.Number == Samples.NonStockMaterial);

        var byCode = await Gateway.SearchCostCentersAsync(Samples.CostCenter[..^1], 50, Ct);
        Assert.Contains(byCode, c => c.Code == Samples.CostCenter);

        Assert.Single(await Gateway.SearchMaterialsAsync(null, 1, Ct));
        Assert.Single(await Gateway.SearchCostCentersAsync("", 1, Ct));
        Assert.Single(await Gateway.SearchWbsElementsAsync(null, 1, Ct));
        Assert.Single(await Gateway.SearchAssetsAsync(null, 1, Ct));

        Assert.Empty(await Gateway.SearchMaterialsAsync("zzz-no-such-thing-zzz", 50, Ct));
    }

    [Fact]
    public async Task Materials_reference_a_known_material_group_and_unit()
    {
        var groups = (await Gateway.GetMaterialGroupsAsync(Ct)).Select(g => g.Code).ToHashSet();
        var units = (await Gateway.GetUnitsOfMeasureAsync(Ct)).Select(u => u.Code).ToHashSet();

        var material = await Gateway.GetMaterialAsync(Samples.NonStockMaterial, Ct);
        Assert.NotNull(material);
        Assert.False(material.IsStock);
        Assert.Contains(material.MaterialGroup, groups);
        Assert.Contains(material.BaseUom, units);
    }

    [Fact]
    public async Task Budget_is_returned_for_a_budgeted_object_and_null_otherwise()
    {
        var budget = await Gateway.GetBudgetAsync(CostObjectType.CostCenter, Samples.CostCenter, Ct);
        Assert.NotNull(budget);
        Assert.Equal(CostObjectType.CostCenter, budget.ObjectType);
        Assert.Equal(Samples.CostCenter, budget.ObjectCode);
        Assert.Equal(3, budget.Currency.Length);
        Assert.True(budget.Budget >= 0 && budget.Committed >= 0 && budget.Actual >= 0);

        Assert.NotNull(await Gateway.GetBudgetAsync(CostObjectType.Wbs, Samples.WbsElement, Ct));
        Assert.Null(await Gateway.GetBudgetAsync(CostObjectType.CostCenter, "NO-SUCH-CC", Ct));
        // The object type matters: a WBS code is not a cost center.
        Assert.Null(await Gateway.GetBudgetAsync(CostObjectType.CostCenter, Samples.WbsElement, Ct));
    }
}

public abstract class SapPrWriterContract
{
    protected abstract ISapPrWriter Writer { get; }
    protected abstract SapSamples Samples { get; }

    /// <summary>The document type to create test PRs with.</summary>
    protected virtual string DocumentType => "NB";
    private static CancellationToken Ct => CancellationToken.None;

    protected SapPrCreateRequest ValidRequest(Guid? key = null, int items = 1) => new(
        key ?? Guid.NewGuid(),
        DocumentType,
        "REQ-CONTRACT-TEST",
        "PR Agent contract test",
        Enumerable.Range(1, items).Select(n => new SapPrItem(
            ItemNo: n * 10,
            ShortText: $"Contract test item {n}",
            MaterialNumber: null,
            MaterialGroup: Samples.MaterialGroup,
            Quantity: 1,
            Uom: Samples.Uom,
            UnitPrice: 1000,
            Currency: "IDR",
            DeliveryDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            Plant: Samples.Plant,
            PurchasingGroup: Samples.PurchasingGroup,
            AcctAssignmentCategory: "K",
            ItemCategory: "",
            GlAccount: Samples.GlAccount,
            CostCenter: Samples.CostCenter,
            Wbs: null,
            AssetNumber: null,
            Requisitioner: "Contract Test")).ToList());

    [Fact]
    public async Task Create_returns_a_pr_number()
    {
        var result = await Writer.CreatePurchaseRequisitionAsync(ValidRequest(items: 2), Ct);

        Assert.False(string.IsNullOrWhiteSpace(result.PrNumber));
        Assert.Equal(Writer.IsSimulated, result.IsSimulated);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_pr_and_creates_nothing_new()
    {
        var request = ValidRequest();

        var first = await Writer.CreatePurchaseRequisitionAsync(request, Ct);
        var second = await Writer.CreatePurchaseRequisitionAsync(request, Ct);

        Assert.Equal(first.PrNumber, second.PrNumber);
    }

    [Fact]
    public async Task Different_keys_create_different_prs()
    {
        var first = await Writer.CreatePurchaseRequisitionAsync(ValidRequest(), Ct);
        var second = await Writer.CreatePurchaseRequisitionAsync(ValidRequest(), Ct);

        Assert.NotEqual(first.PrNumber, second.PrNumber);
    }

    [Fact]
    public async Task Concurrent_calls_with_one_key_create_one_pr()
    {
        var request = ValidRequest();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Writer.CreatePurchaseRequisitionAsync(request, Ct))));

        Assert.Single(results.Select(r => r.PrNumber).Distinct());
    }

    [Fact]
    public async Task Invalid_pr_is_rejected_with_a_sap_exception_that_is_not_transient()
    {
        var noItems = ValidRequest() with { Items = [] };
        var unknownPlant = ValidRequest();
        unknownPlant = unknownPlant with { Items = [unknownPlant.Items[0] with { Plant = "ZZZZ" }] };
        var noCostCenter = ValidRequest();
        noCostCenter = noCostCenter with { Items = [noCostCenter.Items[0] with { CostCenter = null }] };

        foreach (var bad in new[] { noItems, unknownPlant, noCostCenter })
        {
            var error = await Assert.ThrowsAnyAsync<SapException>(() => Writer.CreatePurchaseRequisitionAsync(bad, Ct));
            Assert.False(error.IsTransient);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
    }

    [Fact]
    public async Task A_rejected_pr_can_be_corrected_and_sent_again_under_the_same_key()
    {
        var key = Guid.NewGuid();
        var bad = ValidRequest(key);
        bad = bad with { Items = [bad.Items[0] with { CostCenter = null }] };

        await Assert.ThrowsAnyAsync<SapException>(() => Writer.CreatePurchaseRequisitionAsync(bad, Ct));
        var result = await Writer.CreatePurchaseRequisitionAsync(ValidRequest(key), Ct);

        Assert.False(string.IsNullOrWhiteSpace(result.PrNumber));
    }

    [Fact]
    public async Task Attachment_links_to_an_existing_pr_and_linking_twice_is_harmless()
    {
        var pr = await Writer.CreatePurchaseRequisitionAsync(ValidRequest(), Ct);
        var file = new SapAttachment("quotation.pdf", "application/pdf", 4, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])));

        await Writer.LinkAttachmentAsync(pr.PrNumber, file, Ct);
        await Writer.LinkAttachmentAsync(pr.PrNumber, file, Ct);
    }

    [Fact]
    public async Task Attachment_to_an_unknown_pr_is_a_sap_exception()
    {
        var file = new SapAttachment("quotation.pdf", "application/pdf", 4, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])));

        await Assert.ThrowsAnyAsync<SapException>(() => Writer.LinkAttachmentAsync("0000000000", file, Ct));
    }
}
