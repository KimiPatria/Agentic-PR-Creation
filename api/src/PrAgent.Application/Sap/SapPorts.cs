using PrAgent.Domain;

namespace PrAgent.Application.Sap;

// ---------------------------------------------------------------------------------------------
// THE SAP SEAM
//
// Everything the application needs from SAP goes through the two interfaces in this file.
// Sap:Mode in configuration picks the implementation:
//   Mock    PrAgent.Sap.Mock    seed data and simulated PR numbers (today)
//   S4Hana  PrAgent.Sap.S4Hana  the real system (empty until the connection exists)
//
// An implementation is correct when tests/PrAgent.SapContractTests passes against it.
// See docs/sap-hookup.md.
// ---------------------------------------------------------------------------------------------

/// <summary>Read-only access to SAP master data and budget. Safe to call from anywhere, including the agent's tools.</summary>
public interface ISapReadGateway
{
    // Small lists: returned whole, cached by the caller.
    Task<IReadOnlyList<SapCodeName>> GetPlantsAsync(CancellationToken ct);
    Task<IReadOnlyList<SapCodeName>> GetUnitsOfMeasureAsync(CancellationToken ct);
    Task<IReadOnlyList<SapCodeName>> GetPurchasingGroupsAsync(CancellationToken ct);
    Task<IReadOnlyList<SapCodeName>> GetMaterialGroupsAsync(CancellationToken ct);
    Task<IReadOnlyList<SapCodeName>> GetGlAccountsAsync(CancellationToken ct);

    // Large sets: searched by code or text, and fetched one by one. Search matches the query against
    // code and name, case-insensitively; a null or empty query returns the first <paramref name="max"/> entries.
    Task<IReadOnlyList<SapMaterial>> SearchMaterialsAsync(string? query, int max, CancellationToken ct);
    Task<SapMaterial?> GetMaterialAsync(string number, CancellationToken ct);
    Task<IReadOnlyList<SapCostCenter>> SearchCostCentersAsync(string? query, int max, CancellationToken ct);
    Task<SapCostCenter?> GetCostCenterAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<SapWbsElement>> SearchWbsElementsAsync(string? query, int max, CancellationToken ct);
    Task<SapWbsElement?> GetWbsElementAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<SapAsset>> SearchAssetsAsync(string? query, int max, CancellationToken ct);
    Task<SapAsset?> GetAssetAsync(string number, CancellationToken ct);

    /// <summary>Budget, commitments and actuals of a cost object, or null when SAP holds no budget for it.</summary>
    Task<SapBudget?> GetBudgetAsync(CostObjectType objectType, string code, CancellationToken ct);
}

/// <summary>
/// Creates purchase requisitions in SAP. This is the only write path to SAP in the system, and the
/// only class allowed to depend on it is PrCreationWorker, which runs after a reviewer has approved.
/// </summary>
public interface ISapPrWriter
{
    /// <summary>True when PR numbers are simulated and nothing is created in SAP.</summary>
    bool IsSimulated { get; }

    /// <summary>
    /// Creates one PR. Must be idempotent on <see cref="SapPrCreateRequest.IdempotencyKey"/>: a second
    /// call with the same key returns the PR number of the first and creates nothing.
    /// Throws <see cref="SapException"/> when SAP rejects the PR or cannot be reached.
    /// </summary>
    Task<SapPrCreateResult> CreatePurchaseRequisitionAsync(SapPrCreateRequest request, CancellationToken ct);

    /// <summary>Links a file to an existing PR. Linking the same file name twice must not duplicate it.</summary>
    Task LinkAttachmentAsync(string prNumber, SapAttachment attachment, CancellationToken ct);
}

public sealed record SapCodeName(string Code, string Name);

public sealed record SapMaterial(string Number, string Description, string MaterialGroup, string BaseUom, bool IsStock);

public sealed record SapCostCenter(string Code, string Name, string CompanyCode);

/// <param name="BudgetType">Whether the element carries CAPEX or OPEX budget, when SAP can tell. Null skips the check.</param>
public sealed record SapWbsElement(string Code, string Name, BudgetType? BudgetType);

public sealed record SapAsset(string Number, string Description, string AssetClass, string? CostCenter);

public sealed record SapBudget(
    CostObjectType ObjectType,
    string ObjectCode,
    string Currency,
    decimal Budget,
    decimal Committed,
    decimal Actual,
    DateTimeOffset AsOf);

/// <param name="IdempotencyKey">Stable per PR across retries.</param>
/// <param name="DocumentType">SAP PR document type, for example NB.</param>
/// <param name="RequestReference">The app's request reference, for the PR header text or tracking number.</param>
public sealed record SapPrCreateRequest(
    Guid IdempotencyKey,
    string DocumentType,
    string RequestReference,
    string HeaderText,
    IReadOnlyList<SapPrItem> Items);

/// <param name="AcctAssignmentCategory">Empty string means SAP blank (stock, unassigned).</param>
/// <param name="ItemCategory">Empty string means SAP blank (standard item).</param>
public sealed record SapPrItem(
    int ItemNo,
    string ShortText,
    string? MaterialNumber,
    string MaterialGroup,
    decimal Quantity,
    string Uom,
    decimal UnitPrice,
    string Currency,
    DateOnly DeliveryDate,
    string Plant,
    string PurchasingGroup,
    string AcctAssignmentCategory,
    string ItemCategory,
    string? GlAccount,
    string? CostCenter,
    string? Wbs,
    string? AssetNumber,
    string Requisitioner);

public sealed record SapPrCreateResult(string PrNumber, bool IsSimulated);

public sealed record SapAttachment(string FileName, string ContentType, long SizeBytes, Func<CancellationToken, Task<Stream>> OpenRead);

/// <summary>SAP rejected a call or could not be reached.</summary>
public class SapException(string message, bool isTransient = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when trying again later may succeed (timeout, lock, SAP down).</summary>
    public bool IsTransient { get; } = isTransient;
}

/// <summary>Sap:Mode is S4Hana but the adapter has not been implemented or configured yet.</summary>
public sealed class SapNotConfiguredException(string operation)
    : SapException($"SAP is not connected yet: '{operation}' has no implementation. Set Sap:Mode to Mock, or complete PrAgent.Sap.S4Hana (see docs/sap-hookup.md).");
