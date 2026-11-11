using Microsoft.Extensions.Options;
using PrAgent.Application.Sap;
using PrAgent.Domain;
using PrAgent.Sap.Mock;
using PrAgent.Sap.S4Hana;

namespace PrAgent.SapContractTests;

// ---- Mock adapter: must always pass. ----------------------------------------------------------

internal static class MockSamples
{
    public static readonly SapSamples Value = new(
        Plant: "1100",
        PurchasingGroup: "P01",
        MaterialGroup: "IT01",
        GlAccount: "61100000",
        Uom: "EA",
        CostCenter: "CC1101",
        WbsElement: "C-2026-IT-001",
        Asset: "200000001",
        NonStockMaterial: "100000101",
        MaterialSearchTerm: "laptop");
}

public sealed class MockReadGatewayContractTests : SapReadGatewayContract
{
    protected override ISapReadGateway Gateway { get; } = new MockSapReadGateway(MockSapData.LoadEmbedded(), TimeProvider.System);
    protected override SapSamples Samples => MockSamples.Value;
}

public sealed class MockPrWriterContractTests : SapPrWriterContract
{
    private static readonly IOptions<MockSapOptions> NoPersistence = Options.Create(new MockSapOptions { StatePath = null });

    protected override ISapPrWriter Writer { get; } = new MockSapPrWriter(MockSapData.LoadEmbedded(), new MockSapLedger(NoPersistence), NoPersistence);
    protected override SapSamples Samples => MockSamples.Value;

    [Fact]
    public void Mock_declares_itself_simulated()
    {
        Assert.True(Writer.IsSimulated);
    }

    [Fact]
    public async Task Mock_remembers_issued_prs_across_a_restart_when_given_a_state_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pragent-mock-{Guid.NewGuid():N}.json");
        try
        {
            var options = Options.Create(new MockSapOptions { StatePath = path });
            var request = ValidRequest();

            var before = await new MockSapPrWriter(MockSapData.LoadEmbedded(), new MockSapLedger(options), options).CreatePurchaseRequisitionAsync(request, CancellationToken.None);
            var afterRestart = new MockSapPrWriter(MockSapData.LoadEmbedded(), new MockSapLedger(options), options);
            var again = await afterRestart.CreatePurchaseRequisitionAsync(request, CancellationToken.None);
            var next = await afterRestart.CreatePurchaseRequisitionAsync(ValidRequest(), CancellationToken.None);

            Assert.Equal(before.PrNumber, again.PrNumber);
            Assert.NotEqual(before.PrNumber, next.PrNumber);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

// ---- S/4HANA adapter: the slot for the real connection. ---------------------------------------

/// <summary>
/// Today the S/4HANA adapter is empty, and this proves it fails loudly instead of pretending.
/// When the adapter is implemented, delete this class and enable the two below.
/// </summary>
public sealed class S4HanaIsNotConnectedYetTests
{
    [Fact]
    public async Task Every_call_says_sap_is_not_connected()
    {
        var gateway = new S4HanaReadGateway();
        var writer = new S4HanaPrWriter();
        var ct = CancellationToken.None;

        await Assert.ThrowsAsync<SapNotConfiguredException>(() => gateway.GetPlantsAsync(ct));
        await Assert.ThrowsAsync<SapNotConfiguredException>(() => gateway.GetMaterialAsync("1", ct));
        await Assert.ThrowsAsync<SapNotConfiguredException>(() => gateway.GetBudgetAsync(CostObjectType.CostCenter, "1", ct));
        var error = await Assert.ThrowsAsync<SapNotConfiguredException>(() => writer.CreatePurchaseRequisitionAsync(
            new SapPrCreateRequest(Guid.NewGuid(), "NB", "REQ", "text", []), ct));

        Assert.Contains("docs/sap-hookup.md", error.Message);
        Assert.False(writer.IsSimulated);
    }
}

/*
 * ENABLE WHEN SAP IS CONNECTED
 *
 * 1. Implement PrAgent.Sap.S4Hana.
 * 2. Fill in Samples with data that exists in the SAP sandbox.
 * 3. Uncomment, and run:  dotnet test tests/PrAgent.SapContractTests
 *    The writer tests create real PRs in the system they point at. Run them against a sandbox only.
 *
public sealed class S4HanaReadGatewayContractTests : SapReadGatewayContract
{
    protected override ISapReadGateway Gateway { get; } = new S4HanaReadGateway(...);
    protected override SapSamples Samples => new(Plant: "...", PurchasingGroup: "...", MaterialGroup: "...", GlAccount: "...", Uom: "...",
        CostCenter: "...", WbsElement: "...", Asset: "...", NonStockMaterial: "...", MaterialSearchTerm: "...");
}

public sealed class S4HanaPrWriterContractTests : SapPrWriterContract
{
    protected override ISapPrWriter Writer { get; } = new S4HanaPrWriter(...);
    protected override SapSamples Samples => ...;
}
*/
