using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Infrastructure;

namespace PrAgent.IntegrationTests;

[Collection(ApiCollection.Name)]
public class WorkflowApiTests(PostgresFixture postgres)
{
    private static string Status(JsonNode response) => response["request"]!["status"]!.GetValue<string>();
    private static Guid Id(JsonNode response) => response["request"]!["id"]!.GetValue<Guid>();
    private static JsonArray Splits(JsonNode response) => response["request"]!["splits"]!.AsArray();

    private static async Task<JsonNode> SubmitAsync(HttpClient requestor, string sectionCode, params object[] lines)
    {
        var section = await requestor.SectionIdAsync(sectionCode);
        var draft = await requestor.PostAsync("/api/requests", Drafts.Request(section, lines)).OkAsync();
        return await requestor.PostAsync($"/api/requests/{Id(draft)}/submit").OkAsync();
    }

    [Fact]
    public async Task Request_goes_from_draft_to_created_with_one_simulated_pr_per_split()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);

        // Requestor: a material and a service line. The rules engine locks the SAP fields.
        var section = await rina.SectionIdAsync("IT-INFRA");
        var draft = await rina.PostAsync("/api/requests", Drafts.Request(section, Drafts.Material(1), Drafts.Service(2))).OkAsync();
        Assert.Equal("Draft", Status(draft));
        Assert.True(draft["evaluation"]!["canSubmit"]!.GetValue<bool>(), draft["evaluation"]!.ToJsonString());
        Assert.Equal(2, draft["evaluation"]!["splits"]!.AsArray().Count);

        var line1 = draft["request"]!["lines"]![0]!;
        Assert.Equal("NB", line1["prDocType"]!.GetValue<string>());
        Assert.Equal("K", line1["acctAssignmentCategory"]!.GetValue<string>());
        Assert.Equal("61100000", line1["glAccount"]!.GetValue<string>());
        Assert.Equal("P01", line1["purchasingGroup"]!.GetValue<string>());
        Assert.Equal("D", draft["request"]!["lines"]![1]!["itemCategory"]!.GetValue<string>());

        var id = Id(draft);
        var submitted = await rina.PostAsync($"/api/requests/{id}/submit").OkAsync();
        Assert.Equal("Submitted", Status(submitted));
        Assert.Empty(app.Ledger.Created); // submitting writes nothing to SAP

        await app.DrainAsync();
        var toReviewer = Assert.Single(app.Email.Sent);
        Assert.Equal([DemoUsers.Budi.Email], toReviewer.To);

        // Reviewer sees it in the queue and approves. Approval itself still writes nothing to SAP.
        var queue = (await budi.GetAsync("/api/requests?scope=review").OkAsync()).AsArray();
        Assert.Contains(queue, r => r!["id"]!.GetValue<Guid>() == id);

        var approved = await budi.PostAsync($"/api/requests/{id}/approve", new { comment = "Go ahead" }).OkAsync();
        Assert.Equal("Approved", Status(approved));
        Assert.Equal(2, Splits(approved).Count);
        Assert.Empty(app.Ledger.Created);

        // The outbox worker creates the PRs.
        await app.DrainAsync();
        var created = await rina.GetAsync($"/api/requests/{id}").OkAsync();
        Assert.Equal("Created", Status(created));
        var numbers = Splits(created).Select(s => s!["sapPrNumber"]!.GetValue<string>()).ToList();
        Assert.Equal(2, numbers.Distinct().Count());
        Assert.All(Splits(created), s => Assert.True(s!["isSimulated"]!.GetValue<bool>()));
        Assert.Equal(numbers.Order(), app.Ledger.Created.Select(p => p.PrNumber).Order());

        // What went to (simulated) SAP is what the rules engine locked.
        var servicePr = app.Ledger.Created.Single(p => p.Request.Items.Any(i => i.ItemCategory == "D"));
        var item = Assert.Single(servicePr.Request.Items);
        Assert.Equal(10, item.ItemNo);
        Assert.Equal("K", item.AcctAssignmentCategory);
        Assert.Equal("61300000", item.GlAccount);
        Assert.Equal("P02", item.PurchasingGroup);
        Assert.Equal("CC1101", item.CostCenter);

        // Email to requestor, copied to the reviewer, labelled simulated.
        var createdMail = app.Email.Sent.Last();
        Assert.Equal([DemoUsers.Rina.Email], createdMail.To);
        Assert.Equal([DemoUsers.Budi.Email], createdMail.Cc);
        Assert.Contains("SIMULATED", createdMail.Subject);
        Assert.All(numbers, n => Assert.Contains(n, createdMail.TextBody));

        // The audit trail lists every step.
        var history = (await rina.GetAsync($"/api/requests/{id}/history").OkAsync()).AsArray().Select(h => h!["action"]!.GetValue<string>()).ToList();
        Assert.Equal(
            ["request.created", "request.submitted", "request.approved", "sap.pr_created", "sap.pr_created", "request.created_in_sap"],
            history);
    }

    [Fact]
    public async Task Failure_on_split_two_of_three_then_retry_yields_exactly_three_prs()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);

        // SAP fails on the second PR of the run.
        app.Sap.Script = (_, call) => call == 2 ? SapFailure.BeforeSap : SapFailure.None;

        // Three splits: IT material (P01), IT service (P02), office supplies (P03).
        var submitted = await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1), Drafts.Service(2), Drafts.Material(3, "GA01", "Whiteboard markers", 50_000));
        var id = Id(submitted);
        var approved = await budi.PostAsync($"/api/requests/{id}/approve").OkAsync();
        Assert.Equal(3, Splits(approved).Count);

        await app.DispatchOnceAsync(); // first attempt: splits 1 and 3 succeed, split 2 fails
        var failed = await rina.GetAsync($"/api/requests/{id}").OkAsync();
        Assert.Equal("CreationFailed", Status(failed));
        Assert.Equal(["Created", "Failed", "Created"], Splits(failed).Select(s => s!["status"]!.GetValue<string>()));
        Assert.Contains("Injected", Splits(failed)[1]!["lastError"]!.GetValue<string>());
        Assert.Equal(2, app.Ledger.Created.Count);
        var firstNumbers = Splits(failed).Select(s => s!["sapPrNumber"]?.GetValue<string>()).ToList();

        await app.DrainAsync(); // the outbox retries on its own
        var done = await rina.GetAsync($"/api/requests/{id}").OkAsync();
        Assert.Equal("Created", Status(done));
        Assert.Equal(3, app.Ledger.Created.Count); // exactly three, never a fourth
        Assert.Equal(4, app.Sap.CreateCalls); // the retry called SAP once, for the failed split only

        var finalNumbers = Splits(done).Select(s => s!["sapPrNumber"]!.GetValue<string>()).ToList();
        Assert.Equal(firstNumbers[0], finalNumbers[0]);
        Assert.Equal(firstNumbers[2], finalNumbers[2]);
        Assert.Equal(3, finalNumbers.Distinct().Count());
    }

    [Fact]
    public async Task Lost_reply_from_sap_does_not_create_a_duplicate_pr_on_retry()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);

        // SAP creates the PR, then the connection drops before the app hears the PR number.
        app.Sap.Script = (_, call) => call == 1 ? SapFailure.AfterSap : SapFailure.None;

        var id = Id(await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1)));
        await budi.PostAsync($"/api/requests/{id}/approve").OkAsync();

        await app.DispatchOnceAsync();
        Assert.Equal("CreationFailed", Status(await rina.GetAsync($"/api/requests/{id}").OkAsync()));
        var orphan = Assert.Single(app.Ledger.Created); // the PR exists in SAP, the app does not know its number yet

        await app.DrainAsync();
        var done = await rina.GetAsync($"/api/requests/{id}").OkAsync();
        Assert.Equal("Created", Status(done));
        Assert.Single(app.Ledger.Created);
        Assert.Equal(orphan.PrNumber, Splits(done)[0]!["sapPrNumber"]!.GetValue<string>());
        Assert.Equal(2, Splits(done)[0]!["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task Exhausted_retries_park_the_job_and_a_manual_retry_finishes_it()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);
        var sari = app.As(DemoUsers.Sari);

        var sapIsDown = true;
        app.Sap.Script = (_, _) => sapIsDown ? SapFailure.BeforeSap : SapFailure.None;

        var id = Id(await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1)));
        await budi.PostAsync($"/api/requests/{id}/approve").OkAsync();
        await app.DrainAsync();

        Assert.Equal("CreationFailed", Status(await rina.GetAsync($"/api/requests/{id}").OkAsync()));
        Assert.Equal(3, app.Sap.CreateCalls); // Outbox:MaxAttempts
        Assert.Empty(app.Ledger.Created);
        var jobs = (await sari.GetAsync("/api/admin/jobs").OkAsync()).AsArray();
        Assert.Contains(jobs, j => j!["type"]!.GetValue<string>() == "CreatePr" && j["status"]!.GetValue<string>() == "Failed");

        sapIsDown = false;
        var retry = await budi.PostAsync($"/api/requests/{id}/retry").OkAsync();
        Assert.True(retry["request"]!["permissions"]!["canRetry"]!.GetValue<bool>());
        await app.DrainAsync();

        Assert.Equal("Created", Status(await rina.GetAsync($"/api/requests/{id}").OkAsync()));
        Assert.Single(app.Ledger.Created);
    }

    [Fact]
    public async Task Nothing_is_written_to_sap_for_a_request_no_reviewer_approved()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var id = Id(await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1)));
        await app.DrainAsync();

        // Simulate a bug or an attacker queuing creation for a request that is only Submitted.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Outbox.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), Type = OutboxMessage.CreatePr, RequestId = id, Payload = "{}",
                Status = OutboxStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }
        await app.DrainAsync();

        Assert.Equal(0, app.Sap.CreateCalls);
        Assert.Empty(app.Ledger.Created);
        Assert.Equal("Submitted", Status(await rina.GetAsync($"/api/requests/{id}").OkAsync()));

        using var check = app.Services.CreateScope();
        var job = await check.ServiceProvider.GetRequiredService<AppDbContext>().Outbox.SingleAsync(m => m.Type == OutboxMessage.CreatePr);
        Assert.Equal(OutboxStatus.Failed, job.Status);
        Assert.Contains("must be approved first", job.LastError);
    }

    [Fact]
    public async Task Only_the_right_people_can_see_and_decide_a_request()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);
        var dewi = app.As(DemoUsers.Dewi);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rina.GetAsync("/api/admin/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await budi.GetAsync("/api/requests?scope=all")).StatusCode);

        var section = await rina.SectionIdAsync("IT-INFRA");
        var draft = await rina.PostAsync("/api/requests", Drafts.Request(section, Drafts.Material(1))).OkAsync();
        var id = Id(draft);

        // A draft is private to its requestor, even from the section's reviewer.
        Assert.Equal(HttpStatusCode.Forbidden, (await budi.GetAsync($"/api/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await budi.PutAsJsonAsync($"/api/requests/{id}", Drafts.Request(section, Drafts.Material(1)))).StatusCode);

        await rina.PostAsync($"/api/requests/{id}/submit").OkAsync();

        // Requestor cannot approve; a reviewer of another section cannot approve or even see it.
        Assert.Equal(HttpStatusCode.Forbidden, (await rina.PostAsync($"/api/requests/{id}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dewi.PostAsync($"/api/requests/{id}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dewi.GetAsync($"/api/requests/{id}")).StatusCode);
        // A submitted request is frozen for its requestor.
        Assert.Equal(HttpStatusCode.Conflict, (await rina.PutAsJsonAsync($"/api/requests/{id}", Drafts.Request(section, Drafts.Material(1)))).StatusCode);

        // A reviewer cannot approve their own request, and it never shows up in their own queue.
        var own = Id(await SubmitAsync(budi, "IT-INFRA", Drafts.Material(1)));
        Assert.Equal(HttpStatusCode.Conflict, (await budi.PostAsync($"/api/requests/{own}/approve")).StatusCode);
        var queue = (await budi.GetAsync("/api/requests?scope=review").OkAsync()).AsArray();
        Assert.DoesNotContain(queue, r => r!["id"]!.GetValue<Guid>() == own);

        await app.DrainAsync();
        Assert.Empty(app.Ledger.Created);
    }

    [Fact]
    public async Task Invalid_request_cannot_be_submitted_and_says_why()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var section = await rina.SectionIdAsync("IT-INFRA");

        // CAPEX on a cost center without an asset number, and an unknown cost center on line 2.
        var capex = new
        {
            lineNo = 1, budgetType = "Capex", itemType = "Material", costObjectType = "CostCenter", shortText = "Rack server", materialGroup = "IT02",
            quantity = 1, uom = "EA", unitPrice = 250_000_000m, deliveryDate = Drafts.DeliveryDate, costCenter = "CC1101",
        };
        var draft = await rina.PostAsync("/api/requests", Drafts.Request(section, capex, Drafts.Material(2, costCenter: "NOPE"))).OkAsync();
        Assert.False(draft["evaluation"]!["canSubmit"]!.GetValue<bool>());

        var response = await rina.PostAsync($"/api/requests/{Id(draft)}/submit");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadAsync();
        var messages = problem["evaluation"]!["lines"]!.AsArray().SelectMany(l => l!["messages"]!.AsArray()).Select(m => m!["text"]!.GetValue<string>()).ToList();
        Assert.Contains(messages, m => m.Contains("Asset number is required"));
        Assert.Contains(messages, m => m.Contains("Cost center NOPE was not found"));

        Assert.Equal("Draft", Status(await rina.GetAsync($"/api/requests/{Id(draft)}").OkAsync()));
    }

    [Fact]
    public async Task Evaluate_warns_about_budget_without_blocking()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var section = await rina.SectionIdAsync("IT-INFRA");

        // CC1101 has 500,000,000 IDR left in the mock data; this asks for 600,000,000.
        var evaluation = await rina.PostAsync("/api/rules/evaluate", Drafts.Request(section, Drafts.Material(1, unitPrice: 300_000_000))).OkAsync();

        Assert.True(evaluation["canSubmit"]!.GetValue<bool>());
        var check = Assert.Single(evaluation["budget"]!.AsArray());
        Assert.True(check!["isOver"]!.GetValue<bool>());
        Assert.Equal(500_000_000m, check["remaining"]!.GetValue<decimal>());
        Assert.Contains(evaluation["messages"]!.AsArray(), m => m!["severity"]!.GetValue<string>() == "Warning" && m["text"]!.GetValue<string>().Contains("Over by"));
        Assert.True(evaluation["usesPlaceholderRules"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sent_back_request_can_be_fixed_and_resubmitted()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);
        var section = await rina.SectionIdAsync("IT-INFRA");
        var id = Id(await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1)));

        Assert.Equal(HttpStatusCode.Conflict, (await budi.PostAsync($"/api/requests/{id}/send-back", new { comment = " " })).StatusCode);
        var sentBack = await budi.PostAsync($"/api/requests/{id}/send-back", new { comment = "Use the service desk cost center" }).OkAsync();
        Assert.Equal("SentBack", Status(sentBack));

        await app.DrainAsync();
        Assert.Contains(app.Email.Sent, m => m.To.Contains(DemoUsers.Rina.Email) && m.TextBody.Contains("Use the service desk cost center"));

        var fixedDraft = await rina.PutAsJsonAsync($"/api/requests/{id}", Drafts.Request(section, Drafts.Material(1, costCenter: "CC1103"))).OkAsync();
        Assert.Equal("CC1103", fixedDraft["request"]!["lines"]![0]!["costCenter"]!.GetValue<string>());
        Assert.Equal("Submitted", Status(await rina.PostAsync($"/api/requests/{id}/submit").OkAsync()));
        Assert.Equal("Approved", Status(await budi.PostAsync($"/api/requests/{id}/approve").OkAsync()));
    }

    [Fact]
    public async Task Approval_is_refused_when_the_rules_changed_after_submission()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);
        var sari = app.As(DemoUsers.Sari);
        var id = Id(await SubmitAsync(rina, "IT-INFRA", Drafts.Material(1)));

        // The admin replaces placeholder rule R01 with a different document type.
        var rules = (await sari.GetAsync("/api/admin/rules").OkAsync())["rules"]!.AsArray();
        var r01 = rules.Single(r => r!["code"]!.GetValue<string>() == "R01")!;
        Assert.True(r01["isPlaceholder"]!.GetValue<bool>());
        r01["prDocType"] = "ZOPX";
        var updated = await sari.PutAsJsonAsync($"/api/admin/rules/{r01["id"]!.GetValue<Guid>()}", r01).OkAsync();
        Assert.False(updated["isPlaceholder"]!.GetValue<bool>()); // an edited row is no longer a placeholder

        var response = await budi.PostAsync($"/api/requests/{id}/approve");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("changed after this request was submitted", (await response.ReadAsync())["detail"]!.GetValue<string>());
        Assert.Equal("Submitted", Status(await rina.GetAsync($"/api/requests/{id}").OkAsync()));
    }

    [Fact]
    public async Task Admin_rule_changes_are_validated_and_audited()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var sari = app.As(DemoUsers.Sari);

        object Rule(string code, object conditions, bool allowed = true, string? message = null) => new
        {
            code, description = "Test rule", conditions, priority = 1, allowed, message, prDocType = "NB", acctAssignmentCategory = "K", itemCategory = "",
            glMode = "FromMaterialGroup", requiredFields = new[] { "costCenter" }, isActive = true, validFrom = "2026-01-01",
        };

        Assert.Equal(HttpStatusCode.Conflict, (await sari.PostAsync("/api/admin/rules", Rule("X1", new { colour = "red" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sari.PostAsync("/api/admin/rules", Rule("X1", new { budgetType = "Sometimes" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sari.PostAsync("/api/admin/rules", Rule("X1", new { budgetType = "Opex" }, allowed: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sari.PostAsync("/api/admin/rules", Rule("R01", new { budgetType = "Opex" }))).StatusCode);

        var created = await sari.PostAsync("/api/admin/rules", Rule("X1", new { budgetType = "opex" })).OkAsync();
        Assert.Equal("Opex", created["conditions"]!["budgetType"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await sari.DeleteAsync($"/api/admin/rules/{created["id"]!.GetValue<Guid>()}")).StatusCode);

        var audit = (await sari.GetAsync("/api/admin/audit?entityType=rule").OkAsync()).AsArray().Select(a => a!["action"]!.GetValue<string>()).ToList();
        Assert.Equal(["rule.deleted", "rule.created"], audit);
    }

    [Fact]
    public async Task Attachment_is_stored_downloadable_and_linked_to_the_created_pr()
    {
        await using var app = await TestApp.StartAsync(postgres);
        var rina = app.As(DemoUsers.Rina);
        var budi = app.As(DemoUsers.Budi);
        var section = await rina.SectionIdAsync("IT-INFRA");
        var id = Id(await rina.PostAsync("/api/requests", Drafts.Request(section, Drafts.Material(1))).OkAsync());

        MultipartFormDataContent File(string name, byte[] bytes)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return new MultipartFormDataContent { { part, "file", name } };
        }

        Assert.Equal(HttpStatusCode.Conflict, (await rina.PostAsync($"/api/requests/{id}/attachments", File("virus.exe", [1, 2, 3]))).StatusCode);

        var bytes = "%PDF-1.4 quotation"u8.ToArray();
        var uploaded = await rina.PostAsync($"/api/requests/{id}/attachments", File("quotation.pdf", bytes)).OkAsync();
        var attachmentId = uploaded["id"]!.GetValue<Guid>();
        Assert.Equal(bytes.Length, uploaded["sizeBytes"]!.GetValue<long>());

        Assert.Equal(HttpStatusCode.Forbidden, (await budi.GetAsync($"/api/requests/{id}/attachments/{attachmentId}")).StatusCode);

        await rina.PostAsync($"/api/requests/{id}/submit").OkAsync();
        Assert.Equal(bytes, await budi.GetByteArrayAsync($"/api/requests/{id}/attachments/{attachmentId}"));

        await budi.PostAsync($"/api/requests/{id}/approve").OkAsync();
        await app.DrainAsync();
        Assert.Equal(["quotation.pdf"], Assert.Single(app.Ledger.Created).Attachments);
    }

    [Fact]
    public async Task Mcp_endpoint_needs_the_service_token_and_offers_only_read_tools()
    {
        await using var app = await TestApp.StartAsync(postgres);

        HttpRequestMessage Rpc(object body, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(body) };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (token is not null) request.Headers.Add("X-Service-Token", token);
            return request;
        }

        var client = app.CreateClient();
        var list = new { jsonrpc = "2.0", id = 1, method = "tools/list" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Rpc(list, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Rpc(list, "wrong"))).StatusCode);

        var response = await client.SendAsync(Rpc(list, TestApp.ServiceToken));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var names = System.Text.RegularExpressions.Regex.Matches(body, "\"name\"\\s*:\\s*\"([a-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(["get_budget", "preview_rules", "search_master_data"], names);

        var call = new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "search_master_data", arguments = new { type = "materials", query = "laptop" } } };
        var result = await (await client.SendAsync(Rpc(call, TestApp.ServiceToken))).Content.ReadAsStringAsync();
        Assert.Contains("100000101", result);

        await app.DrainAsync();
        Assert.Equal(0, app.Sap.CreateCalls);
    }
}
