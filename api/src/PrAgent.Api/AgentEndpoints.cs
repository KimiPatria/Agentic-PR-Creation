using System.ComponentModel;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Domain.Rules;

namespace PrAgent.Api;

public sealed class AgentOptions
{
    public const string Section = "Agent";
    public const string ServiceTokenHeader = "X-Service-Token";

    /// <summary>Where the Strands agent listens. Locally that is the AgentCore-compatible server on port 8080.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>Shared secret the agent presents to call /mcp. Empty disables the MCP endpoint.</summary>
    public string? ServiceToken { get; set; }
}

public sealed record AgentChatBody(string SessionId, string Message, DraftInput? Form, Guid? RequestId);

public sealed record AgentSuggestionBody(string SessionId, Guid? RequestId, JsonElement Proposal, JsonElement? Outcome);

public static class AgentEndpoints
{
    public const string HttpClientName = "agent";

    public static void MapAgentEndpoints(this WebApplication app)
    {
        // The browser never talks to the agent directly. The API is the single point of sign-in,
        // adds who the user is, and relays the agent's event stream.
        app.MapPost("/api/agent/chat", async (AgentChatBody body, HttpContext http, ICurrentUser user, IHttpClientFactory clients,
            IOptions<AgentOptions> options, BusinessClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Message)) return Problem(400, "Empty message", "Type a message for the assistant.");
            if (string.IsNullOrWhiteSpace(body.SessionId) || body.SessionId.Length > 100) return Problem(400, "Bad session", "A session id is required.");

            // Sessions are namespaced by user, so nobody can read or continue someone else's conversation.
            var owner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id)))[..16];
            var sessionId = $"{owner}-{body.SessionId}";

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl.TrimEnd('/')}/invocations")
            {
                Content = JsonContent.Create(new
                {
                    prompt = body.Message,
                    session_id = sessionId,
                    user = new { email = user.Email, name = user.Name },
                    form = body.Form,
                    today = clock.Today,
                }, options: Json.Options),
            };
            request.Headers.Add("X-Amzn-Bedrock-AgentCore-Runtime-Session-Id", sessionId);
            request.Headers.Accept.ParseAdd("text/event-stream");

            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException)
            {
                return Problem(503, "Assistant unavailable", "The assistant is not running. You can still fill in the form by hand.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return Problem(502, "Assistant error", $"The assistant returned {(int)response.StatusCode}. You can still fill in the form by hand.");

                http.Response.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }

            return Results.Empty;
        });

        // The web app reports what the agent proposed and what the requestor did with it.
        app.MapPost("/api/agent/suggestions", async (AgentSuggestionBody body, AppDbContext db, ICurrentUser user, BusinessClock clock, CancellationToken ct) =>
        {
            db.AgentSuggestions.Add(new AgentSuggestion
            {
                Id = Guid.NewGuid(),
                SessionId = body.SessionId,
                RequestId = body.RequestId,
                UserEmail = user.Email,
                Proposal = body.Proposal.GetRawText(),
                Outcome = body.Outcome?.GetRawText(),
                At = clock.Now,
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}

/// <summary>
/// The agent's window onto SAP, served over MCP at /mcp. READ ONLY by design: there is no tool here,
/// and must never be one, that creates, changes or submits anything. Locally the agent connects
/// straight to this endpoint; in AWS the same tools sit behind AgentCore Gateway.
/// </summary>
[McpServerToolType]
public sealed class SapReadTools(MasterDataService masterData, EvaluationService evaluator)
{
    public sealed record PreviewLine(
        [property: Description("1-based line number")] int LineNo,
        [property: Description("Opex or Capex")] BudgetType? BudgetType,
        [property: Description("Material or Service")] ItemType? ItemType,
        [property: Description("CostCenter or Wbs: what the line is charged to")] CostObjectType? CostObjectType,
        [property: Description("SAP material number, only if the item exists in the material master")] string? MaterialNumber,
        [property: Description("What is being bought, max 40 characters")] string? ShortText,
        [property: Description("Material group code from search_master_data")] string? MaterialGroup,
        decimal? Quantity,
        [property: Description("Unit of measure code from search_master_data")] string? Uom,
        decimal? UnitPrice,
        [property: Description("ISO date yyyy-MM-dd")] DateOnly? DeliveryDate,
        string? CostCenter,
        string? Wbs,
        string? AssetNumber);

    [McpServerTool(Name = "search_master_data", ReadOnly = true, Idempotent = true)]
    [Description("Search SAP master data by code or name. Use it to turn what the requestor said into real SAP codes; never invent a code. " +
                 "Returns up to `max` matches as JSON: code, name and type-specific details.")]
    public async Task<string> SearchMasterData(
        [Description("One of: plants, uoms, purchasing-groups, gl-accounts, material-groups, materials, cost-centers, wbs, assets")] string type,
        [Description("Part of a code or name, for example 'laptop' or 'CC11'. Leave empty to list the first entries.")] string? query,
        [Description("Maximum number of results, 1 to 25. Default 10.")] int max = 10,
        CancellationToken ct = default)
    {
        if (!MasterDataService.Kinds.Contains(type, StringComparer.OrdinalIgnoreCase))
            return $"Unknown type '{type}'. Use one of: {string.Join(", ", MasterDataService.Kinds)}.";

        var results = await masterData.SearchAsync(type, query, Math.Clamp(max, 1, 25), ct);
        if (results.Count == 0)
            return $"No {type} match '{query}'. Try a shorter or different search term, or leave the query empty to list what exists.";

        return JsonSerializer.Serialize(results.Select(r => Flatten(r)), Json.Options);
    }

    [McpServerTool(Name = "get_budget", ReadOnly = true, Idempotent = true)]
    [Description("Remaining budget of a cost center or WBS element, from SAP. Use it before suggesting a cost object, and tell the requestor when a request would exceed what is left.")]
    public async Task<string> GetBudget(
        [Description("CostCenter or Wbs")] string objectType,
        [Description("The cost center or WBS element code, exactly as returned by search_master_data")] string code,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<CostObjectType>(objectType, ignoreCase: true, out var type))
            return "objectType must be CostCenter or Wbs.";

        var budget = await masterData.BudgetAsync(type, code, ct);
        if (budget is null)
            return $"SAP holds no budget for {type} {code}. Check the code with search_master_data.";

        return JsonSerializer.Serialize(new
        {
            budget.ObjectType,
            budget.ObjectCode,
            budget.Currency,
            budget.Budget,
            budget.Committed,
            budget.Actual,
            Remaining = budget.Budget - budget.Committed - budget.Actual,
        }, Json.Options);
    }

    [McpServerTool(Name = "preview_rules", ReadOnly = true, Idempotent = true)]
    [Description("Run the PR rules engine over draft lines WITHOUT saving anything. Returns, per line, the SAP fields the rules lock " +
                 "(document type, account assignment, item category, G/L, purchasing group), what is still missing or wrong, and which choices remain allowed. " +
                 "Also returns how many SAP PRs the lines would be split into and any budget warning. Call it before proposing a form fill.")]
    public async Task<string> PreviewRules(
        [Description("The draft lines. Leave unknown fields out.")] PreviewLine[] lines,
        [Description("Currency of the unit prices. Default IDR.")] string currency = "IDR",
        CancellationToken ct = default)
    {
        if (lines.Length == 0) return "Pass at least one line.";

        var draft = new DraftInput
        {
            Currency = currency,
            Lines = lines.Select(l => new LineInput
            {
                LineNo = l.LineNo, BudgetType = l.BudgetType, ItemType = l.ItemType, CostObjectType = l.CostObjectType,
                MaterialNumber = l.MaterialNumber, ShortText = l.ShortText, MaterialGroup = l.MaterialGroup, Quantity = l.Quantity,
                Uom = l.Uom, UnitPrice = l.UnitPrice, DeliveryDate = l.DeliveryDate, CostCenter = l.CostCenter, Wbs = l.Wbs, AssetNumber = l.AssetNumber,
            }).ToList(),
        };
        var evaluation = await evaluator.EvaluateAsync(draft, ct);

        return JsonSerializer.Serialize(new
        {
            lines = evaluation.Lines.Select(e => new
            {
                e.LineNo,
                valid = e.IsValid,
                rule = e.MatchedRuleCode,
                lockedSapFields = new
                {
                    e.Resolved.PrDocType, e.Resolved.AcctAssignmentCategory, e.Resolved.ItemCategory, e.Resolved.GlAccount,
                    e.Resolved.PurchasingGroup, e.Resolved.MaterialGroup, e.Resolved.Uom, e.Resolved.IsStock,
                },
                problems = e.Messages.Where(m => m.Severity != Severity.Info).Select(m => m.Text),
                allowedChoices = e.AllowedOptions,
            }),
            sapPrCount = evaluation.Splits.Count,
            splits = evaluation.Splits.Select(s => new { s.Label, s.LineNos, s.Total }),
            budget = evaluation.Budget.Select(b => new { b.ObjectType, b.ObjectCode, b.Requested, b.Remaining, b.IsOver, b.Text }),
        }, Json.Options);
    }

    private static Dictionary<string, object?> Flatten(MasterDataOption option)
    {
        var flat = new Dictionary<string, object?> { ["code"] = option.Code, ["name"] = option.Name };
        foreach (var (key, value) in option.Meta ?? new Dictionary<string, object?>())
            if (value is not null) flat[key] = value;
        return flat;
    }
}
