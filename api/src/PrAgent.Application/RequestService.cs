using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Domain.Requests;
using PrAgent.Domain.Rules;

namespace PrAgent.Application;

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string UploadedBy, DateTimeOffset UploadedAt);

public sealed record RequestPermissions(bool CanEdit, bool CanSubmit, bool CanReview, bool CanRetry);

public sealed record RequestDto(
    Guid Id,
    string Reference,
    RequestStatus Status,
    string Title,
    string Justification,
    string Plant,
    string Currency,
    Guid? SectionId,
    string? SectionName,
    string RequestorName,
    string RequestorEmail,
    string? ReviewerName,
    string? ReviewComment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt,
    decimal Total,
    IReadOnlyList<PrLine> Lines,
    IReadOnlyList<PrSplit> Splits,
    IReadOnlyList<AttachmentDto> Attachments,
    RequestPermissions Permissions);

public sealed record RequestSummaryDto(
    Guid Id,
    string Reference,
    RequestStatus Status,
    string Title,
    string RequestorName,
    string? SectionName,
    decimal Total,
    string Currency,
    int LineCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    IReadOnlyList<string> PrNumbers,
    bool IsSimulated);

public sealed record RequestWithEvaluation(RequestDto Request, EvaluationResult Evaluation);

/// <summary>Drafts, submission and review of purchase requests. Nothing here talks to SAP's write side.</summary>
public sealed class RequestService(
    AppDbContext db,
    ICurrentUser user,
    EvaluationService evaluator,
    IAttachmentStore attachmentStore,
    AuditLog audit,
    Notifier notifier,
    BusinessClock clock,
    IOptions<AppOptions> options)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".xlsx", ".xls", ".docx", ".doc", ".csv", ".txt", ".msg", ".eml",
    };

    // ---- Queries ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RequestSummaryDto>> ListAsync(string scope, RequestStatus? status, CancellationToken ct)
    {
        var query = db.Requests.AsNoTracking().Include(r => r.Lines).Include(r => r.Splits).AsQueryable();

        switch (scope.ToLowerInvariant())
        {
            case "mine":
                query = query.Where(r => r.RequestorId == user.Id);
                break;
            case "review":
                var sections = await ReviewedSectionIdsAsync(ct);
                query = query.Where(r => r.SectionId != null && sections.Contains(r.SectionId.Value)
                                         && r.Status != RequestStatus.Draft && r.RequestorId != user.Id);
                break;
            case "all":
                if (!user.IsInRole(Roles.Admin)) throw new ForbiddenException("Only a PR admin can list every request.");
                break;
            default:
                throw new NotFoundException($"Unknown scope '{scope}'. Use mine, review or all.");
        }

        if (status is not null) query = query.Where(r => r.Status == status);

        var requests = await query.OrderByDescending(r => r.UpdatedAt).Take(500).AsSplitQuery().ToListAsync(ct);
        var sectionNames = await db.Sections.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return requests.Select(r => new RequestSummaryDto(
            r.Id, r.Reference, r.Status, r.Title, r.RequestorName,
            r.SectionId is { } id && sectionNames.TryGetValue(id, out var name) ? name : null,
            r.Total, r.Currency, r.Lines.Count, r.UpdatedAt, r.SubmittedAt,
            r.Splits.Where(s => s.SapPrNumber != null).OrderBy(s => s.Sequence).Select(s => s.SapPrNumber!).ToList(),
            r.Splits.Any(s => s.IsSimulated))).ToList();
    }

    public async Task<RequestWithEvaluation> GetAsync(Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        await EnsureCanViewAsync(request, ct);
        return new(await ToDtoAsync(request, ct), await evaluator.EvaluateAsync(ToDraft(request), ct));
    }

    // ---- Draft --------------------------------------------------------------------------------

    public async Task<RequestWithEvaluation> CreateDraftAsync(DraftInput draft, CancellationToken ct)
    {
        var request = new PurchaseRequest(Guid.NewGuid(), NewReference(), user.AsPerson(), clock.Now);
        db.Requests.Add(request);
        var evaluation = await ApplyDraftAsync(request, draft, ct);
        audit.Record(user.AsPerson(), "request.created", request, new { request.Reference });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), evaluation);
    }

    public async Task<RequestWithEvaluation> UpdateDraftAsync(Guid id, DraftInput draft, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        EnsureOwner(request);
        var evaluation = await ApplyDraftAsync(request, draft, ct);
        audit.Record(user.AsPerson(), "request.updated", request, new { Lines = request.Lines.Count, request.Total });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), evaluation);
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        EnsureOwner(request);
        if (request.Status != RequestStatus.Draft)
            throw new DomainException("Only a draft that was never submitted can be deleted.");

        foreach (var attachment in request.Attachments)
            await attachmentStore.DeleteAsync(attachment.StorageKey, ct);

        db.Requests.Remove(request);
        audit.Record(user.AsPerson(), "request.deleted", request, new { request.Reference });
        await db.SaveChangesAsync(ct);
    }

    // ---- Workflow -----------------------------------------------------------------------------

    public async Task<RequestWithEvaluation> SubmitAsync(Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        EnsureOwner(request);

        // Re-evaluate what is stored: what the browser showed a minute ago proves nothing.
        var evaluation = await ApplyDraftAsync(request, ToDraft(request), ct);
        if (!evaluation.CanSubmit)
            throw new ValidationFailedException("This request has errors and cannot be submitted yet.", evaluation);

        request.Submit(clock.Now);

        var reviewers = await db.SectionReviewers.AsNoTracking()
            .Where(r => r.SectionId == request.SectionId).Select(r => r.Email).ToListAsync(ct);
        notifier.RequestSubmitted(request, reviewers.Where(e => !e.Equals(user.Email, StringComparison.OrdinalIgnoreCase)));
        audit.Record(user.AsPerson(), "request.submitted", request, new { request.Total, Lines = request.Lines.Count });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), evaluation);
    }

    public async Task<RequestWithEvaluation> SendBackAsync(Guid id, string comment, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        await EnsureReviewerAsync(request, ct);

        request.SendBack(user.AsPerson(), comment, clock.Now);
        notifier.RequestSentBack(request);
        audit.Record(user.AsPerson(), "request.sent_back", request, new { Comment = request.ReviewComment });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), await evaluator.EvaluateAsync(ToDraft(request), ct));
    }

    /// <summary>
    /// Approves a request. This fixes the split into SAP PRs and queues their creation; the SAP call
    /// itself is made later by <see cref="PrCreationWorker"/> from the outbox.
    /// </summary>
    public async Task<RequestWithEvaluation> ApproveAsync(Guid id, string? comment, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        await EnsureReviewerAsync(request, ct);
        if (request.Status != RequestStatus.Submitted)
            throw new DomainException($"A request in status {request.Status} cannot be approved. It must be Submitted.");

        // Rules or master data may have changed since submission. Validate against today's truth,
        // without touching the stored lines: the reviewer approves what they were shown.
        var evaluation = await evaluator.EvaluateAsync(ToDraft(request), ct);
        if (!evaluation.CanSubmit)
            throw new ValidationFailedException("This request no longer passes validation. Send it back to the requestor to fix.", evaluation);
        if (HasDrifted(request, evaluation))
            throw new ValidationFailedException("The rules or master data changed after this request was submitted, so its SAP fields are out of date. Send it back so the requestor can resubmit.", evaluation);

        var splits = new List<PrSplit>();
        foreach (var (group, index) in evaluation.Splits.Select((g, i) => (g, i)))
        {
            var split = new PrSplit(Guid.NewGuid(), index + 1, group.Key, group.Label, group.PrDocType, group.PurchasingGroup);
            splits.Add(split);
            foreach (var line in request.Lines.Where(l => group.LineNos.Contains(l.LineNo)))
                line.SplitId = split.Id;
        }

        request.Approve(user.AsPerson(), comment, splits, clock.Now);
        QueueCreation(request);
        audit.Record(user.AsPerson(), "request.approved", request, new { Comment = request.ReviewComment, Splits = splits.Count, request.Total });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), evaluation);
    }

    /// <summary>Queues another creation attempt for a request whose SAP creation failed.</summary>
    public async Task<RequestWithEvaluation> RetryCreationAsync(Guid id, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        if (!user.IsInRole(Roles.Admin)) await EnsureReviewerAsync(request, ct);
        if (request.Status != RequestStatus.CreationFailed)
            throw new DomainException($"A request in status {request.Status} has nothing to retry.");

        var alreadyQueued = await db.Outbox.AnyAsync(m => m.RequestId == request.Id && m.Type == OutboxMessage.CreatePr && m.Status == OutboxStatus.Pending, ct);
        if (!alreadyQueued) QueueCreation(request);
        audit.Record(user.AsPerson(), "request.retry_requested", request);
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(request, ct), await evaluator.EvaluateAsync(ToDraft(request), ct));
    }

    // ---- Attachments --------------------------------------------------------------------------

    public async Task<AttachmentDto> AddAttachmentAsync(Guid id, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        EnsureOwner(request);

        var safeName = Path.GetFileName(fileName).Trim();
        if (safeName.Length is 0 or > 200) throw new DomainException("The file name is empty or too long.");
        if (!AllowedExtensions.Contains(Path.GetExtension(safeName)))
            throw new DomainException($"Files of type '{Path.GetExtension(safeName)}' are not accepted. Allowed: {string.Join(", ", AllowedExtensions.Order())}.");
        if (request.Attachments.Count >= 10) throw new DomainException("A request can carry at most 10 attachments.");

        var max = options.Value.MaxAttachmentBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw new DomainException($"The file is larger than the {max / (1024 * 1024)} MB limit.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw new DomainException("The file is empty.");

        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = buffer.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))),
            UploadedBy = user.Email,
            UploadedAt = clock.Now,
        };
        attachment.StorageKey = $"requests/{request.Id}/{attachment.Id}/{safeName}";

        buffer.Position = 0;
        await attachmentStore.SaveAsync(attachment.StorageKey, buffer, attachment.ContentType, ct);

        request.AddAttachment(attachment, clock.Now);
        audit.Record(user.AsPerson(), "attachment.added", request, new { attachment.FileName, attachment.SizeBytes, attachment.Sha256 });
        await db.SaveChangesAsync(ct);
        return ToDto(attachment);
    }

    public async Task RemoveAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        EnsureOwner(request);
        var attachment = request.RemoveAttachment(attachmentId, clock.Now);
        audit.Record(user.AsPerson(), "attachment.removed", request, new { attachment.FileName });
        await db.SaveChangesAsync(ct);
        await attachmentStore.DeleteAsync(attachment.StorageKey, ct);
    }

    public async Task<(AttachmentDto Info, Stream Content)> OpenAttachmentAsync(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var request = await LoadAsync(id, ct);
        await EnsureCanViewAsync(request, ct);
        var attachment = request.Attachments.FirstOrDefault(a => a.Id == attachmentId) ?? throw new NotFoundException("Attachment not found.");
        return (ToDto(attachment), await attachmentStore.OpenReadAsync(attachment.StorageKey, ct));
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>Evaluates the draft and stores it with the SAP fields the rules engine derived.</summary>
    private async Task<EvaluationResult> ApplyDraftAsync(PurchaseRequest request, DraftInput draft, CancellationToken ct)
    {
        var evaluation = await evaluator.EvaluateAsync(draft, ct);
        request.UpdateDraft(
            draft.Title ?? "", draft.Justification ?? "", draft.Plant ?? "", draft.Currency ?? "IDR", draft.SectionId,
            evaluation.Lines.Select(e => ToEntity(e.Resolved)), clock.Now);
        return evaluation;
    }

    private void QueueCreation(PurchaseRequest request) => db.Outbox.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(),
        Type = OutboxMessage.CreatePr,
        RequestId = request.Id,
        Payload = JsonSerializer.Serialize(new { requestId = request.Id }),
        Status = OutboxStatus.Pending,
        CreatedAt = clock.Now,
        NextAttemptAt = clock.Now,
    });

    /// <summary>True when re-evaluating the stored lines would change any SAP field the reviewer was shown.</summary>
    private static bool HasDrifted(PurchaseRequest request, EvaluationResult evaluation) =>
        request.Lines.Count != evaluation.Lines.Count
        || request.Lines.Any(line =>
        {
            var now = evaluation.Lines.FirstOrDefault(e => e.LineNo == line.LineNo)?.Resolved;
            return now is null
                   || now.PrDocType != line.PrDocType
                   || now.AcctAssignmentCategory != line.AcctAssignmentCategory
                   || now.ItemCategory != line.ItemCategory
                   || now.GlAccount != line.GlAccount
                   || now.PurchasingGroup != line.PurchasingGroup
                   || now.MaterialGroup != line.MaterialGroup
                   || now.Uom != line.Uom
                   || now.ItemType != line.ItemType
                   || now.IsStock != line.IsStock
                   || now.CostCenter != line.CostCenter
                   || now.Wbs != line.Wbs
                   || now.AssetNumber != line.AssetNumber;
        });

    private async Task<PurchaseRequest> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Requests
            .Include(r => r.Lines.OrderBy(l => l.LineNo))
            .Include(r => r.Splits.OrderBy(s => s.Sequence))
            .Include(r => r.Attachments.OrderBy(a => a.UploadedAt))
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new NotFoundException("Request not found.");

    private void EnsureOwner(PurchaseRequest request)
    {
        if (request.RequestorId != user.Id) throw new ForbiddenException("Only the requestor can change this request.");
    }

    private async Task<List<Guid>> ReviewedSectionIdsAsync(CancellationToken ct)
    {
        if (!user.IsInRole(Roles.Reviewer)) return [];
        var email = user.Email.ToLowerInvariant();
        return await db.SectionReviewers.AsNoTracking().Where(r => r.Email == email).Select(r => r.SectionId).ToListAsync(ct);
    }

    private async Task<bool> ReviewsSectionOfAsync(PurchaseRequest request, CancellationToken ct) =>
        request.SectionId is { } sectionId && (await ReviewedSectionIdsAsync(ct)).Contains(sectionId);

    private async Task EnsureReviewerAsync(PurchaseRequest request, CancellationToken ct)
    {
        if (!await ReviewsSectionOfAsync(request, ct))
            throw new ForbiddenException("You are not a reviewer for the section this request belongs to.");
    }

    private async Task EnsureCanViewAsync(PurchaseRequest request, CancellationToken ct)
    {
        if (request.RequestorId == user.Id || user.IsInRole(Roles.Admin)) return;
        if (request.Status != RequestStatus.Draft && await ReviewsSectionOfAsync(request, ct)) return;
        throw new ForbiddenException("You do not have access to this request.");
    }

    private async Task<RequestDto> ToDtoAsync(PurchaseRequest request, CancellationToken ct)
    {
        var isOwner = request.RequestorId == user.Id;
        var reviews = !isOwner && await ReviewsSectionOfAsync(request, ct);
        var sectionName = request.SectionId is null
            ? null
            : await db.Sections.AsNoTracking().Where(s => s.Id == request.SectionId).Select(s => s.Name).FirstOrDefaultAsync(ct);

        return new RequestDto(
            request.Id, request.Reference, request.Status, request.Title, request.Justification, request.Plant, request.Currency,
            request.SectionId, sectionName, request.RequestorName, request.RequestorEmail, request.ReviewerName, request.ReviewComment,
            request.CreatedAt, request.UpdatedAt, request.SubmittedAt, request.DecidedAt, request.Total,
            request.Lines.OrderBy(l => l.LineNo).ToList(),
            request.Splits.OrderBy(s => s.Sequence).ToList(),
            request.Attachments.OrderBy(a => a.UploadedAt).Select(ToDto).ToList(),
            new RequestPermissions(
                CanEdit: isOwner && request.IsEditable,
                CanSubmit: isOwner && request.IsEditable,
                CanReview: reviews && request.Status == RequestStatus.Submitted,
                CanRetry: request.Status == RequestStatus.CreationFailed && (reviews || user.IsInRole(Roles.Admin))));
    }

    private static AttachmentDto ToDto(Attachment a) => new(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, a.UploadedAt);

    public static DraftInput ToDraft(PurchaseRequest request) => new()
    {
        Title = request.Title,
        Justification = request.Justification,
        Plant = request.Plant,
        Currency = request.Currency,
        SectionId = request.SectionId,
        Lines = request.Lines.OrderBy(l => l.LineNo).Select(l => new LineInput
        {
            LineNo = l.LineNo,
            BudgetType = l.BudgetType,
            ItemType = l.ItemType,
            CostObjectType = l.CostObjectType,
            IsStock = l.IsStock,
            MaterialNumber = l.MaterialNumber,
            ShortText = l.ShortText,
            MaterialGroup = l.MaterialGroup,
            Quantity = l.Quantity,
            Uom = l.Uom,
            UnitPrice = l.UnitPrice,
            DeliveryDate = l.DeliveryDate,
            CostCenter = l.CostCenter,
            Wbs = l.Wbs,
            AssetNumber = l.AssetNumber,
        }).ToList(),
    };

    private static PrLine ToEntity(ResolvedLine r) => new()
    {
        Id = Guid.NewGuid(),
        LineNo = r.LineNo,
        BudgetType = r.BudgetType,
        ItemType = r.ItemType,
        CostObjectType = r.CostObjectType,
        IsStock = r.IsStock,
        MaterialNumber = r.MaterialNumber,
        ShortText = r.ShortText,
        MaterialGroup = r.MaterialGroup,
        Quantity = r.Quantity,
        Uom = r.Uom,
        UnitPrice = r.UnitPrice,
        DeliveryDate = r.DeliveryDate,
        PurchasingGroup = r.PurchasingGroup,
        PrDocType = r.PrDocType,
        AcctAssignmentCategory = r.AcctAssignmentCategory,
        ItemCategory = r.ItemCategory,
        GlAccount = r.GlAccount,
        CostCenter = r.CostCenter,
        Wbs = r.Wbs,
        AssetNumber = r.AssetNumber,
    };

    private static string NewReference()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var suffix = string.Create(5, alphabet, static (span, chars) =>
        {
            foreach (ref var c in span) c = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
        return $"REQ-{DateTime.UtcNow:yyMMdd}-{suffix}";
    }
}
