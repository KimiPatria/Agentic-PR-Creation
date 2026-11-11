using PrAgent.Domain;
using PrAgent.Domain.Requests;

namespace PrAgent.UnitTests;

public class WorkflowTests
{
    private static readonly Person Requestor = new("u-rina", "rina@example.com", "Rina");
    private static readonly Person Reviewer = new("u-budi", "budi@example.com", "Budi");
    private static readonly DateTimeOffset Now = TestData.Now;

    private static PurchaseRequest Draft(int lines = 2)
    {
        var request = new PurchaseRequest(Guid.NewGuid(), "REQ-TEST", Requestor, Now);
        request.UpdateDraft("Laptops", "New joiners", "1100", "idr", Guid.NewGuid(),
            Enumerable.Range(1, lines).Select(n => new PrLine { Id = Guid.NewGuid(), LineNo = n, Quantity = 1, UnitPrice = 10 }), Now);
        return request;
    }

    /// <summary>One split per line, so partial failure can be exercised.</summary>
    private static List<PrSplit> SplitPerLine(PurchaseRequest request)
    {
        var splits = new List<PrSplit>();
        foreach (var line in request.Lines)
        {
            var split = new PrSplit(Guid.NewGuid(), line.LineNo, $"k{line.LineNo}", $"Split {line.LineNo}", "NB", "P01");
            line.SplitId = split.Id;
            splits.Add(split);
        }
        return splits;
    }

    private static PurchaseRequest InStatus(RequestStatus status)
    {
        var request = Draft();
        if (status == RequestStatus.Draft) return request;
        request.Submit(Now);
        if (status == RequestStatus.Submitted) return request;
        if (status == RequestStatus.SentBack) { request.SendBack(Reviewer, "Fix it", Now); return request; }
        request.Approve(Reviewer, null, SplitPerLine(request), Now);
        if (status == RequestStatus.Approved) return request;
        request.BeginCreation(Now);
        if (status == RequestStatus.Creating) return request;
        if (status == RequestStatus.Created)
        {
            foreach (var s in request.Splits) request.RecordSplitCreated(s.Id, $"10{s.Sequence}", true, Now);
        }
        else
        {
            request.RecordSplitFailed(request.Splits[0].Id, "boom", Now);
        }
        request.FinishCreation(Now);
        Assert.Equal(status, request.Status);
        return request;
    }

    [Fact]
    public void Happy_path_ends_created_with_a_pr_number_per_split()
    {
        var request = InStatus(RequestStatus.Created);

        Assert.Equal(RequestStatus.Created, request.Status);
        Assert.All(request.Splits, s =>
        {
            Assert.Equal(SplitStatus.Created, s.Status);
            Assert.NotNull(s.SapPrNumber);
            Assert.True(s.IsSimulated);
            Assert.Equal(1, s.Attempts);
        });
        Assert.Equal("idr".ToUpperInvariant(), request.Currency);
        Assert.Equal(20, request.Total);
    }

    public static TheoryData<RequestStatus, string, bool> Transitions()
    {
        // action → the statuses it is legal from
        var legal = new Dictionary<string, RequestStatus[]>
        {
            ["edit"] = [RequestStatus.Draft, RequestStatus.SentBack],
            ["submit"] = [RequestStatus.Draft, RequestStatus.SentBack],
            ["sendBack"] = [RequestStatus.Submitted],
            ["approve"] = [RequestStatus.Submitted],
            ["beginCreation"] = [RequestStatus.Approved, RequestStatus.CreationFailed],
            ["recordCreated"] = [RequestStatus.Creating],
            ["recordFailed"] = [RequestStatus.Creating],
            ["finish"] = [RequestStatus.Creating],
        };

        var data = new TheoryData<RequestStatus, string, bool>();
        foreach (var status in Enum.GetValues<RequestStatus>())
        foreach (var (action, from) in legal)
            data.Add(status, action, from.Contains(status));
        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Each_action_is_legal_only_from_its_own_statuses(RequestStatus status, string action, bool legal)
    {
        var request = InStatus(status);

        void Act()
        {
            switch (action)
            {
                case "edit": request.UpdateDraft("t", "j", "1100", "IDR", Guid.NewGuid(), [new PrLine { Id = Guid.NewGuid(), LineNo = 1 }], Now); break;
                case "submit": request.Submit(Now); break;
                case "sendBack": request.SendBack(Reviewer, "Please fix", Now); break;
                case "approve": request.Approve(Reviewer, null, SplitPerLine(request), Now); break;
                case "beginCreation": request.BeginCreation(Now); break;
                case "recordCreated": request.RecordSplitCreated(request.Splits[0].Id, "1000000001", true, Now); break;
                case "recordFailed": request.RecordSplitFailed(request.Splits[0].Id, "boom", Now); break;
                case "finish": request.FinishCreation(Now); break;
                default: throw new InvalidOperationException(action);
            }
        }

        if (legal) Act();
        else
        {
            var before = request.Status;
            Assert.ThrowsAny<Exception>(Act);
            Assert.Equal(before, request.Status);
        }
    }

    [Fact]
    public void Nothing_reaches_creation_without_an_approval()
    {
        Assert.Throws<DomainException>(() => InStatus(RequestStatus.Draft).BeginCreation(Now));
        Assert.Throws<DomainException>(() => InStatus(RequestStatus.Submitted).BeginCreation(Now));
        Assert.Throws<DomainException>(() => InStatus(RequestStatus.SentBack).BeginCreation(Now));
    }

    [Fact]
    public void A_requestor_cannot_review_their_own_request()
    {
        var request = InStatus(RequestStatus.Submitted);

        Assert.Throws<DomainException>(() => request.Approve(Requestor, null, SplitPerLine(request), Now));
        Assert.Throws<DomainException>(() => request.SendBack(Requestor with { Id = "other-id" }, "no", Now));
        Assert.Equal(RequestStatus.Submitted, request.Status);
    }

    [Fact]
    public void Send_back_needs_a_comment_and_makes_the_request_editable_again()
    {
        var request = InStatus(RequestStatus.Submitted);

        Assert.Throws<DomainException>(() => request.SendBack(Reviewer, "  ", Now));
        request.SendBack(Reviewer, "Wrong cost center", Now);

        Assert.True(request.IsEditable);
        Assert.Equal("Wrong cost center", request.ReviewComment);
        request.Submit(Now);
        Assert.Equal(RequestStatus.Submitted, request.Status);
    }

    [Fact]
    public void Submit_needs_lines_and_a_section()
    {
        var empty = new PurchaseRequest(Guid.NewGuid(), "REQ-1", Requestor, Now);
        Assert.Throws<DomainException>(() => empty.Submit(Now));

        var noSection = new PurchaseRequest(Guid.NewGuid(), "REQ-2", Requestor, Now);
        noSection.UpdateDraft("t", "j", "1100", "IDR", null, [new PrLine { Id = Guid.NewGuid(), LineNo = 1 }], Now);
        Assert.Throws<DomainException>(() => noSection.Submit(Now));
    }

    [Fact]
    public void Approval_refuses_lines_left_out_of_every_split()
    {
        var request = InStatus(RequestStatus.Submitted);
        var splits = SplitPerLine(request);
        request.Lines[1].SplitId = null;

        Assert.Throws<DomainException>(() => request.Approve(Reviewer, null, splits, Now));
    }

    [Fact]
    public void Partial_failure_then_retry_keeps_the_first_pr_number_and_creates_only_the_rest()
    {
        var request = InStatus(RequestStatus.Creating);
        var first = request.Splits[0];
        var second = request.Splits[1];
        var keyBefore = second.IdempotencyKey;

        request.RecordSplitCreated(first.Id, "1000000001", true, Now);
        request.RecordSplitFailed(second.Id, "SAP timeout", Now);
        request.FinishCreation(Now);

        Assert.Equal(RequestStatus.CreationFailed, request.Status);
        Assert.Equal("SAP timeout", second.LastError);

        request.BeginCreation(Now);
        request.RecordSplitCreated(second.Id, "1000000002", true, Now);
        request.FinishCreation(Now);

        Assert.Equal(RequestStatus.Created, request.Status);
        Assert.Equal("1000000001", first.SapPrNumber);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(2, second.Attempts);
        Assert.Null(second.LastError);
        Assert.Equal(keyBefore, second.IdempotencyKey);
    }

    [Fact]
    public void A_created_split_never_changes_its_pr_number()
    {
        var request = InStatus(RequestStatus.Creating);
        var split = request.Splits[0];
        request.RecordSplitCreated(split.Id, "1000000001", true, Now);

        request.RecordSplitCreated(split.Id, "1000000001", true, Now); // same answer again is fine
        Assert.Equal(1, split.Attempts);
        Assert.Throws<DomainException>(() => request.RecordSplitCreated(split.Id, "1000000999", true, Now));
        Assert.Throws<DomainException>(() => request.RecordSplitFailed(split.Id, "late error", Now));
        Assert.Equal("1000000001", split.SapPrNumber);
    }
}
