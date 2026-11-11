using PrAgent.Domain.Requests;

namespace PrAgent.Application;

public static class Roles
{
    public const string Requestor = "Requestor";
    public const string Reviewer = "Reviewer";
    public const string Admin = "Admin";
}

/// <summary>The signed-in user of the current HTTP request.</summary>
public interface ICurrentUser
{
    string Id { get; }
    string Email { get; }
    string Name { get; }
    IReadOnlyCollection<string> Roles { get; }
    bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    Person AsPerson() => new(Id, Email, Name);
}

public sealed record EmailMessage(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string TextBody);

/// <summary>Sends email. Implementations: SES (real AWS) and Log (tests, and until SES is set up).</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Stores attachment bytes. Implementations: S3 (real AWS) and Local (tests, and until the bucket exists).</summary>
public interface IAttachmentStore
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Base URL of the web app, used for links in email.</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>IANA time zone used to decide what "today" is for delivery dates and rule validity.</summary>
    public string TimeZone { get; set; } = "Asia/Jakarta";

    /// <summary>Fields that force lines into separate SAP PRs. The PR document type always splits as well.</summary>
    public List<string> SplitKeys { get; set; } = [];

    public long MaxAttachmentBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed class SapOptions
{
    public const string Section = "Sap";
    public const string Mock = "Mock";
    public const string S4Hana = "S4Hana";

    public string Mode { get; set; } = Mock;
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>The request does not pass the rules engine. Carries the full evaluation for the caller to show.</summary>
public sealed class ValidationFailedException(string message, EvaluationResult evaluation) : Exception(message)
{
    public EvaluationResult Evaluation { get; } = evaluation;
}

public sealed class BusinessClock(TimeProvider time, Microsoft.Extensions.Options.IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _zone = Resolve(options.Value.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);

    private static TimeZoneInfo Resolve(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
