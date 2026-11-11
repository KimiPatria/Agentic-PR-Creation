using Microsoft.Extensions.DependencyInjection;
using PrAgent.Application.Sap;
using PrAgent.Domain;

namespace PrAgent.Sap.S4Hana;

// ---------------------------------------------------------------------------------------------
// EMPTY ON PURPOSE.
//
// This is where the real SAP connection goes. Every method throws SapNotConfiguredException until
// it is implemented. Nothing else in the solution needs to change when it is:
//
//   1. Fill in the methods below (OData, BAPI over RFC, or a mix; see docs/sap-hookup.md).
//   2. Point S4HanaContractTests at the SAP sandbox and make the contract suite pass.
//   3. Set Sap:Mode to "S4Hana".
// ---------------------------------------------------------------------------------------------

public sealed class S4HanaOptions
{
    public const string Section = "Sap:S4Hana";

    /// <summary>For example https://sap-gateway.internal:44300 (reached over VPN or Direct Connect).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>SAP client (Mandant), for example 100.</summary>
    public string? Client { get; set; }

    /// <summary>Name of the AWS Secrets Manager secret holding the technical user's credentials. Never put credentials in config.</summary>
    public string? CredentialsSecretId { get; set; }

    /// <summary>OData service or RFC used for the budget read. Open item 1: PS or CO, and whether a custom RFC is needed.</summary>
    public string? BudgetService { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public static class S4HanaRegistration
{
    public static IServiceCollection AddS4HanaSap(this IServiceCollection services)
    {
        services.AddOptions<S4HanaOptions>().BindConfiguration(S4HanaOptions.Section);
        services.AddSingleton<ISapReadGateway, S4HanaReadGateway>();
        services.AddSingleton<ISapPrWriter, S4HanaPrWriter>();
        return services;
    }
}

public sealed class S4HanaReadGateway : ISapReadGateway
{
    public Task<IReadOnlyList<SapCodeName>> GetPlantsAsync(CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetPlantsAsync));
    public Task<IReadOnlyList<SapCodeName>> GetUnitsOfMeasureAsync(CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetUnitsOfMeasureAsync));
    public Task<IReadOnlyList<SapCodeName>> GetPurchasingGroupsAsync(CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetPurchasingGroupsAsync));
    public Task<IReadOnlyList<SapCodeName>> GetMaterialGroupsAsync(CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetMaterialGroupsAsync));
    public Task<IReadOnlyList<SapCodeName>> GetGlAccountsAsync(CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetGlAccountsAsync));

    public Task<IReadOnlyList<SapMaterial>> SearchMaterialsAsync(string? query, int max, CancellationToken ct) => throw new SapNotConfiguredException(nameof(SearchMaterialsAsync));
    public Task<SapMaterial?> GetMaterialAsync(string number, CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetMaterialAsync));
    public Task<IReadOnlyList<SapCostCenter>> SearchCostCentersAsync(string? query, int max, CancellationToken ct) => throw new SapNotConfiguredException(nameof(SearchCostCentersAsync));
    public Task<SapCostCenter?> GetCostCenterAsync(string code, CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetCostCenterAsync));
    public Task<IReadOnlyList<SapWbsElement>> SearchWbsElementsAsync(string? query, int max, CancellationToken ct) => throw new SapNotConfiguredException(nameof(SearchWbsElementsAsync));
    public Task<SapWbsElement?> GetWbsElementAsync(string code, CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetWbsElementAsync));
    public Task<IReadOnlyList<SapAsset>> SearchAssetsAsync(string? query, int max, CancellationToken ct) => throw new SapNotConfiguredException(nameof(SearchAssetsAsync));
    public Task<SapAsset?> GetAssetAsync(string number, CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetAssetAsync));

    public Task<SapBudget?> GetBudgetAsync(CostObjectType objectType, string code, CancellationToken ct) => throw new SapNotConfiguredException(nameof(GetBudgetAsync));
}

public sealed class S4HanaPrWriter : ISapPrWriter
{
    public bool IsSimulated => false;

    /// <remarks>
    /// SAP's PR create has no idempotency of its own. Write the idempotency key into a PR field you can
    /// search on (for example the requirement tracking number), and look it up before creating.
    /// </remarks>
    public Task<SapPrCreateResult> CreatePurchaseRequisitionAsync(SapPrCreateRequest request, CancellationToken ct) =>
        throw new SapNotConfiguredException(nameof(CreatePurchaseRequisitionAsync));

    public Task LinkAttachmentAsync(string prNumber, SapAttachment attachment, CancellationToken ct) =>
        throw new SapNotConfiguredException(nameof(LinkAttachmentAsync));
}
