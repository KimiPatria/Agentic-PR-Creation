namespace PrAgent.Domain.Requests;

public sealed record Person(string Id, string Email, string Name);

/// <summary>
/// One request from a requestor. It becomes one or more SAP PRs (see <see cref="PrSplit"/>) after a
/// reviewer approves it. All status changes go through the methods here, which reject illegal moves.
/// </summary>
public sealed class PurchaseRequest
{
    private readonly List<PrLine> _lines = [];
    private readonly List<PrSplit> _splits = [];
    private readonly List<Attachment> _attachments = [];

    private PurchaseRequest() { }

    public PurchaseRequest(Guid id, string reference, Person requestor, DateTimeOffset now)
    {
        Id = id;
        Reference = reference;
        RequestorId = requestor.Id;
        RequestorEmail = requestor.Email;
        RequestorName = requestor.Name;
        Status = RequestStatus.Draft;
        CreatedAt = UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Reference { get; private set; } = "";
    public string RequestorId { get; private set; } = "";
    public string RequestorEmail { get; private set; } = "";
    public string RequestorName { get; private set; } = "";
    public Guid? SectionId { get; private set; }
    public string Title { get; private set; } = "";
    public string Justification { get; private set; } = "";
    public string Plant { get; private set; } = "";
    public string Currency { get; private set; } = "IDR";
    public RequestStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? ReviewerId { get; private set; }
    public string? ReviewerEmail { get; private set; }
    public string? ReviewerName { get; private set; }
    public string? ReviewComment { get; private set; }

    public IReadOnlyList<PrLine> Lines => _lines;
    public IReadOnlyList<PrSplit> Splits => _splits;
    public IReadOnlyList<Attachment> Attachments => _attachments;

    public bool IsEditable => Status is RequestStatus.Draft or RequestStatus.SentBack;
    public decimal Total => _lines.Sum(l => l.Total);

    public void UpdateDraft(string title, string justification, string plant, string currency, Guid? sectionId, IEnumerable<PrLine> lines, DateTimeOffset now)
    {
        EnsureEditable();
        Title = title.Trim();
        Justification = justification.Trim();
        Plant = plant.Trim();
        Currency = string.IsNullOrWhiteSpace(currency) ? "IDR" : currency.Trim().ToUpperInvariant();
        SectionId = sectionId;
        _lines.Clear();
        _lines.AddRange(lines.OrderBy(l => l.LineNo));
        UpdatedAt = now;
    }

    public void AddAttachment(Attachment attachment, DateTimeOffset now)
    {
        EnsureEditable();
        _attachments.Add(attachment);
        UpdatedAt = now;
    }

    public Attachment RemoveAttachment(Guid attachmentId, DateTimeOffset now)
    {
        EnsureEditable();
        var attachment = _attachments.FirstOrDefault(a => a.Id == attachmentId)
            ?? throw new DomainException("Attachment not found on this request.");
        _attachments.Remove(attachment);
        UpdatedAt = now;
        return attachment;
    }

    public void Submit(DateTimeOffset now)
    {
        EnsureEditable();
        if (_lines.Count == 0) throw new DomainException("A request needs at least one line before it can be submitted.");
        if (SectionId is null) throw new DomainException("Choose the section this request belongs to, so it can be routed to a reviewer.");
        Status = RequestStatus.Submitted;
        SubmittedAt = now;
        UpdatedAt = now;
    }

    public void SendBack(Person reviewer, string comment, DateTimeOffset now)
    {
        Require(RequestStatus.Submitted, "sent back");
        EnsureNotOwnRequest(reviewer);
        if (string.IsNullOrWhiteSpace(comment)) throw new DomainException("Say what needs to change before sending a request back.");
        SetDecision(reviewer, comment.Trim(), now);
        Status = RequestStatus.SentBack;
    }

    /// <summary>Approves the request and fixes the split into SAP PRs. Nothing is sent to SAP here.</summary>
    public void Approve(Person reviewer, string? comment, IEnumerable<PrSplit> splits, DateTimeOffset now)
    {
        Require(RequestStatus.Submitted, "approved");
        EnsureNotOwnRequest(reviewer);

        _splits.Clear();
        _splits.AddRange(splits);
        if (_splits.Count == 0) throw new DomainException("An approved request must produce at least one SAP PR.");

        var unassigned = _lines.Where(l => _splits.All(s => s.Id != l.SplitId)).Select(l => l.LineNo).ToList();
        if (unassigned.Count > 0)
            throw new DomainException($"Lines {string.Join(", ", unassigned)} are not assigned to a SAP PR.");

        SetDecision(reviewer, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), now);
        Status = RequestStatus.Approved;
    }

    /// <summary>Moves an approved request (or one whose creation failed) into creation.</summary>
    public void BeginCreation(DateTimeOffset now)
    {
        if (Status is not (RequestStatus.Approved or RequestStatus.CreationFailed))
            throw new DomainException($"A request in status {Status} cannot be created in SAP. It must be approved first.");
        Status = RequestStatus.Creating;
        UpdatedAt = now;
    }

    public void RecordSplitCreated(Guid splitId, string sapPrNumber, bool isSimulated, DateTimeOffset now)
    {
        Require(RequestStatus.Creating, "updated with a SAP PR number");
        Split(splitId).MarkCreated(sapPrNumber, isSimulated, now);
        UpdatedAt = now;
    }

    public void RecordSplitFailed(Guid splitId, string error, DateTimeOffset now)
    {
        Require(RequestStatus.Creating, "updated with a SAP error");
        Split(splitId).MarkFailed(error, now);
        UpdatedAt = now;
    }

    /// <summary>Closes a creation attempt: Created when every split has a PR number, otherwise CreationFailed.</summary>
    public void FinishCreation(DateTimeOffset now)
    {
        Require(RequestStatus.Creating, "finished");
        Status = _splits.All(s => s.Status == SplitStatus.Created) ? RequestStatus.Created : RequestStatus.CreationFailed;
        UpdatedAt = now;
    }

    private PrSplit Split(Guid splitId) =>
        _splits.FirstOrDefault(s => s.Id == splitId) ?? throw new DomainException("Split not found on this request.");

    private void SetDecision(Person reviewer, string? comment, DateTimeOffset now)
    {
        ReviewerId = reviewer.Id;
        ReviewerEmail = reviewer.Email;
        ReviewerName = reviewer.Name;
        ReviewComment = comment;
        DecidedAt = now;
        UpdatedAt = now;
    }

    private void EnsureEditable()
    {
        if (!IsEditable)
            throw new DomainException($"A request in status {Status} cannot be edited.");
    }

    private void EnsureNotOwnRequest(Person reviewer)
    {
        if (string.Equals(reviewer.Id, RequestorId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reviewer.Email, RequestorEmail, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("You cannot review your own request.");
    }

    private void Require(RequestStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"A request in status {Status} cannot be {action}. It must be {expected}.");
    }
}

public sealed class PrLine
{
    public Guid Id { get; set; }
    public int LineNo { get; set; }
    public BudgetType? BudgetType { get; set; }
    public ItemType? ItemType { get; set; }
    public CostObjectType? CostObjectType { get; set; }
    public bool? IsStock { get; set; }
    public string? MaterialNumber { get; set; }
    public string? ShortText { get; set; }
    public string? MaterialGroup { get; set; }
    public decimal? Quantity { get; set; }
    public string? Uom { get; set; }
    public decimal? UnitPrice { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public string? PurchasingGroup { get; set; }
    public string? PrDocType { get; set; }
    public string? AcctAssignmentCategory { get; set; }
    public string? ItemCategory { get; set; }
    public string? GlAccount { get; set; }
    public string? CostCenter { get; set; }
    public string? Wbs { get; set; }
    public string? AssetNumber { get; set; }

    /// <summary>The SAP PR this line goes into. Set at approval.</summary>
    public Guid? SplitId { get; set; }

    public decimal Total => (Quantity ?? 0m) * (UnitPrice ?? 0m);
}

/// <summary>One SAP PR to create for a request, and the cross-reference to its PR number once created.</summary>
public sealed class PrSplit
{
    private PrSplit() { }

    public PrSplit(Guid id, int sequence, string key, string label, string? prDocType, string? purchasingGroup)
    {
        Id = id;
        Sequence = sequence;
        Key = key;
        Label = label;
        PrDocType = prDocType;
        PurchasingGroup = purchasingGroup;
        IdempotencyKey = Guid.NewGuid();
        Status = SplitStatus.Pending;
    }

    public Guid Id { get; private set; }
    public int Sequence { get; private set; }
    public string Key { get; private set; } = "";
    public string Label { get; private set; } = "";
    public string? PrDocType { get; private set; }
    public string? PurchasingGroup { get; private set; }

    /// <summary>Sent to SAP with every attempt, so a retry cannot create a second PR for the same split.</summary>
    public Guid IdempotencyKey { get; private set; }

    public SplitStatus Status { get; private set; }
    public string? SapPrNumber { get; private set; }

    /// <summary>True when the number came from the mock adapter and no PR exists in SAP.</summary>
    public bool IsSimulated { get; private set; }

    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? CreatedInSapAt { get; private set; }

    internal void MarkCreated(string sapPrNumber, bool isSimulated, DateTimeOffset now)
    {
        if (Status == SplitStatus.Created)
        {
            if (SapPrNumber != sapPrNumber)
                throw new DomainException($"Split {Sequence} already has SAP PR {SapPrNumber}; refusing to replace it with {sapPrNumber}.");
            return;
        }

        Attempts++;
        Status = SplitStatus.Created;
        SapPrNumber = sapPrNumber;
        IsSimulated = isSimulated;
        LastError = null;
        CreatedInSapAt = now;
    }

    internal void MarkFailed(string error, DateTimeOffset now)
    {
        if (Status == SplitStatus.Created)
            throw new DomainException($"Split {Sequence} already has SAP PR {SapPrNumber}; it cannot be marked failed.");
        Attempts++;
        Status = SplitStatus.Failed;
        LastError = error;
    }
}

public sealed class Attachment
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}
