namespace PrAgent.Domain;

public enum BudgetType { Opex, Capex }

public enum ItemType { Material, Service }

public enum CostObjectType { CostCenter, Wbs }

public enum RequestStatus
{
    Draft,
    Submitted,
    SentBack,
    Approved,
    Creating,
    Created,
    CreationFailed,
}

public enum SplitStatus { Pending, Created, Failed }

/// <summary>How the G/L account of a line is determined by a rule.</summary>
public enum GlMode
{
    /// <summary>No G/L on the PR line (stock material, asset: SAP determines it).</summary>
    None,
    /// <summary>Default G/L account mapped to the line's material group.</summary>
    FromMaterialGroup,
    /// <summary>The account written on the rule itself.</summary>
    Fixed,
}

public enum Severity { Info, Warning, Error }

/// <summary>Field names shared by the rules engine, the API and the web form.</summary>
public static class Fields
{
    public const string BudgetType = "budgetType";
    public const string ItemType = "itemType";
    public const string CostObjectType = "costObjectType";
    public const string IsStock = "isStock";
    public const string MaterialNumber = "materialNumber";
    public const string ShortText = "shortText";
    public const string MaterialGroup = "materialGroup";
    public const string Quantity = "quantity";
    public const string Uom = "uom";
    public const string UnitPrice = "unitPrice";
    public const string DeliveryDate = "deliveryDate";
    public const string PurchasingGroup = "purchasingGroup";
    public const string PrDocType = "prDocType";
    public const string AcctAssignmentCategory = "acctAssignmentCategory";
    public const string ItemCategory = "itemCategory";
    public const string GlAccount = "glAccount";
    public const string CostCenter = "costCenter";
    public const string Wbs = "wbs";
    public const string AssetNumber = "assetNumber";

    /// <summary>The cost-object fields a rule can require. Any of these a rule does not require is cleared.</summary>
    public static readonly string[] CostObjectFields = [CostCenter, Wbs, AssetNumber];
}

public sealed class DomainException(string message) : Exception(message);
