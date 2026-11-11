namespace PrAgent.Domain;

/// <summary>An organisational section. Requests are routed to the reviewers of their section.</summary>
public sealed class Section
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public List<SectionReviewer> Reviewers { get; set; } = [];
}

public sealed class SectionReviewer
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>What the app knows about a signed-in user beyond the identity token.</summary>
public sealed class UserProfile
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? SectionId { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// App-owned attributes of a SAP material group. SAP supplies the code and name; the PR admin
/// maintains which item type it is for and which G/L account and purchasing group it defaults to.
/// </summary>
public sealed class MaterialGroupMapping
{
    public string Code { get; set; } = "";
    public ItemType? ItemType { get; set; }
    public string? DefaultGlAccount { get; set; }
    public string? DefaultPurchasingGroup { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>Append-only record of who did what. Rows are never updated or deleted.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? Data { get; set; }
}

public enum OutboxStatus { Pending, Processed, Failed }

/// <summary>Work that must happen after a transaction commits: creating PRs in SAP, sending email.</summary>
public sealed class OutboxMessage
{
    public const string CreatePr = "CreatePr";
    public const string Email = "Email";

    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public Guid? RequestId { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>What the agent proposed for a form, and what the requestor did with it. Kept for audit and evals.</summary>
public sealed class AgentSuggestion
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = "";
    public Guid? RequestId { get; set; }
    public string UserEmail { get; set; } = "";
    public string Proposal { get; set; } = "{}";
    public string? Outcome { get; set; }
    public DateTimeOffset At { get; set; }
}
