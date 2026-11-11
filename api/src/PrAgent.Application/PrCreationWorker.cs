using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PrAgent.Application.Data;
using PrAgent.Application.Sap;
using PrAgent.Domain;
using PrAgent.Domain.Requests;

namespace PrAgent.Application;

/// <summary>Thrown when a creation run ends with at least one split not created, so the outbox retries it.</summary>
public sealed class PrCreationIncompleteException(string message) : Exception(message);

/// <summary>
/// Creates the SAP PRs of an approved request. This is the ONLY class allowed to depend on
/// <see cref="ISapPrWriter"/> (enforced by ArchitectureTests), and it refuses any request a reviewer
/// has not approved. It runs from the outbox, never from an HTTP request and never from the agent.
/// </summary>
public sealed class PrCreationWorker(
    AppDbContext db,
    ISapPrWriter sap,
    IAttachmentStore attachments,
    AuditLog audit,
    Notifier notifier,
    BusinessClock clock,
    ILogger<PrCreationWorker> logger)
{
    public async Task ProcessAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.Requests
            .Include(r => r.Lines).Include(r => r.Splits).Include(r => r.Attachments)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new NotFoundException($"Request {requestId} not found.");

        if (request.Status == RequestStatus.Created) return;

        // A run interrupted mid-way leaves the request in Creating; pick it up where it stopped.
        // Anything else must be Approved or CreationFailed, or BeginCreation throws.
        if (request.Status != RequestStatus.Creating)
        {
            request.BeginCreation(clock.Now);
            await db.SaveChangesAsync(ct);
        }

        foreach (var split in request.Splits.Where(s => s.Status != SplitStatus.Created).OrderBy(s => s.Sequence).ToList())
        {
            try
            {
                var result = await sap.CreatePurchaseRequisitionAsync(BuildSapRequest(request, split), ct);
                request.RecordSplitCreated(split.Id, result.PrNumber, result.IsSimulated, clock.Now);
                audit.Record(AuditLog.System, "sap.pr_created", request, new { split.Sequence, result.PrNumber, result.IsSimulated, split.IdempotencyKey });
                logger.LogInformation("Request {Reference} split {Sequence}: PR {PrNumber} created (simulated: {Simulated})",
                    request.Reference, split.Sequence, result.PrNumber, result.IsSimulated);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                request.RecordSplitFailed(split.Id, e.Message, clock.Now);
                audit.Record(AuditLog.System, "sap.pr_failed", request, new { split.Sequence, Error = e.Message, split.IdempotencyKey });
                logger.LogWarning(e, "Request {Reference} split {Sequence}: SAP PR creation failed", request.Reference, split.Sequence);
            }

            // Save after every split: a PR number is on record before the next SAP call is made.
            await db.SaveChangesAsync(ct);

            if (split.Status == SplitStatus.Created)
                await LinkAttachmentsAsync(request, split, ct);
        }

        request.FinishCreation(clock.Now);
        if (request.Status == RequestStatus.Created)
        {
            notifier.PrsCreated(request);
            audit.Record(AuditLog.System, "request.created_in_sap", request, new { PrNumbers = request.Splits.Select(s => s.SapPrNumber) });
        }
        await db.SaveChangesAsync(ct);

        if (request.Status == RequestStatus.CreationFailed)
        {
            var failed = request.Splits.Where(s => s.Status != SplitStatus.Created).Select(s => $"split {s.Sequence}: {s.LastError}");
            throw new PrCreationIncompleteException($"Request {request.Reference} is not fully created in SAP ({string.Join("; ", failed)}).");
        }
    }

    /// <summary>Attachment linking is best effort: a PR that exists is not failed because a file would not attach.</summary>
    private async Task LinkAttachmentsAsync(PurchaseRequest request, PrSplit split, CancellationToken ct)
    {
        foreach (var attachment in request.Attachments)
        {
            try
            {
                var file = new SapAttachment(attachment.FileName, attachment.ContentType, attachment.SizeBytes, token => attachments.OpenReadAsync(attachment.StorageKey, token));
                await sap.LinkAttachmentAsync(split.SapPrNumber!, file, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                audit.Record(AuditLog.System, "sap.attachment_link_failed", request, new { split.SapPrNumber, attachment.FileName, Error = e.Message });
                logger.LogWarning(e, "Could not link {FileName} to PR {PrNumber}", attachment.FileName, split.SapPrNumber);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static SapPrCreateRequest BuildSapRequest(PurchaseRequest request, PrSplit split)
    {
        var lines = request.Lines.Where(l => l.SplitId == split.Id).OrderBy(l => l.LineNo).ToList();
        if (lines.Count == 0) throw new DomainException($"Split {split.Sequence} has no lines.");

        // SAP numbers PR items in steps of 10.
        var items = lines.Select((line, index) => new SapPrItem(
            ItemNo: (index + 1) * 10,
            ShortText: Required(line.ShortText, line, "description"),
            MaterialNumber: line.MaterialNumber,
            MaterialGroup: Required(line.MaterialGroup, line, "material group"),
            Quantity: line.Quantity ?? throw Missing(line, "quantity"),
            Uom: Required(line.Uom, line, "unit of measure"),
            UnitPrice: line.UnitPrice ?? throw Missing(line, "unit price"),
            Currency: request.Currency,
            DeliveryDate: line.DeliveryDate ?? throw Missing(line, "delivery date"),
            Plant: request.Plant,
            PurchasingGroup: Required(line.PurchasingGroup, line, "purchasing group"),
            AcctAssignmentCategory: line.AcctAssignmentCategory ?? throw Missing(line, "account assignment category"),
            ItemCategory: line.ItemCategory ?? throw Missing(line, "item category"),
            GlAccount: line.GlAccount,
            CostCenter: line.CostCenter,
            Wbs: line.Wbs,
            AssetNumber: line.AssetNumber,
            Requisitioner: request.RequestorName)).ToList();

        return new SapPrCreateRequest(
            split.IdempotencyKey,
            split.PrDocType ?? throw new DomainException($"Split {split.Sequence} has no PR document type."),
            request.Reference,
            request.Title,
            items);
    }

    private static string Required(string? value, PrLine line, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw Missing(line, name) : value;

    private static DomainException Missing(PrLine line, string name) => new($"Line {line.LineNo} has no {name}.");
}
