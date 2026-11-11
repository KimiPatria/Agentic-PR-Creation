using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Application.Sap;
using PrAgent.Domain;
using PrAgent.Infrastructure;

namespace PrAgent.Api;

public sealed record DecisionBody(string? Comment);

public static class CommonEndpoints
{
    public static void MapCommonEndpoints(this WebApplication app)
    {
        // What the sign-in screen needs before anyone is signed in.
        app.MapGet("/api/public/config", (IOptions<AuthOptions> auth, IOptions<SapOptions> sap) => Results.Ok(new
        {
            authMode = auth.Value.Mode,
            sapMode = sap.Value.Mode,
            devUsers = auth.Value.Mode.Equals(AuthOptions.Dev, StringComparison.OrdinalIgnoreCase)
                ? DemoUsers.All.Select(u => new { u.Email, u.Name, u.Roles })
                : null,
        })).AllowAnonymous();

        app.MapGet("/api/me", async (ICurrentUser user, AppDbContext db, BusinessClock clock, CancellationToken ct) =>
        {
            var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Email == user.Email, ct);
            if (profile is null)
            {
                profile = new UserProfile { Email = user.Email };
                db.UserProfiles.Add(profile);
            }
            profile.Name = user.Name;
            profile.LastSeenAt = clock.Now;
            await db.SaveChangesAsync(ct);

            var reviews = user.IsInRole(Roles.Reviewer)
                ? await db.SectionReviewers.AsNoTracking().Where(r => r.Email == user.Email).Select(r => r.SectionId).ToListAsync(ct)
                : [];

            return Results.Ok(new { user.Id, user.Email, user.Name, user.Roles, profile.SectionId, reviewsSectionIds = reviews });
        });

        app.MapGet("/api/meta", (IOptions<SapOptions> sap, IOptions<AppOptions> appOptions) =>
        {
            var splitKeys = appOptions.Value.SplitKeys.Count > 0 ? appOptions.Value.SplitKeys : [.. Domain.Rules.SplitPlanner.DefaultKeys];
            return Results.Ok(new { sapMode = sap.Value.Mode, sapSimulated = IsSimulated(sap.Value), splitKeys });
        });

        app.MapGet("/api/sections", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sections.AsNoTracking().OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Code, s.Name, HasReviewer = s.Reviewers.Any() }).ToListAsync(ct)));

        app.MapGet("/api/master-data/{kind}", async (string kind, string? q, int? max, MasterDataService masterData, CancellationToken ct) =>
            Results.Ok(await masterData.SearchAsync(kind, q, max ?? 50, ct)));

        app.MapGet("/api/budget", async (CostObjectType type, string code, MasterDataService masterData, CancellationToken ct) =>
            await masterData.BudgetAsync(type, code, ct) is { } budget
                ? Results.Ok(new { budget.ObjectType, budget.ObjectCode, budget.Currency, budget.Budget, budget.Committed, budget.Actual, Remaining = budget.Budget - budget.Committed - budget.Actual, budget.AsOf })
                : Results.NotFound(new ProblemDetails { Status = 404, Title = "No budget", Detail = $"SAP holds no budget for {type} {code}." }));

        app.MapPost("/api/rules/evaluate", async (DraftInput draft, EvaluationService evaluator, CancellationToken ct) =>
            Results.Ok(await evaluator.EvaluateAsync(draft, ct)));
    }

    /// <summary>Whether PR numbers are simulated, decided from configuration so no write-side service is touched.</summary>
    public static bool IsSimulated(SapOptions sap) => sap.Mode.Equals(SapOptions.Mock, StringComparison.OrdinalIgnoreCase);
}

public static class RequestEndpoints
{
    public static void MapRequestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/requests");

        group.MapGet("", async (string? scope, RequestStatus? status, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.ListAsync(scope ?? "mine", status, ct)));

        group.MapPost("", async (DraftInput draft, RequestService requests, CancellationToken ct) =>
        {
            var created = await requests.CreateDraftAsync(draft, ct);
            return Results.Created($"/api/requests/{created.Request.Id}", created);
        });

        group.MapGet("{id:guid}", async (Guid id, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.GetAsync(id, ct)));

        group.MapPut("{id:guid}", async (Guid id, DraftInput draft, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.UpdateDraftAsync(id, draft, ct)));

        group.MapDelete("{id:guid}", async (Guid id, RequestService requests, CancellationToken ct) =>
        {
            await requests.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });

        group.MapPost("{id:guid}/submit", async (Guid id, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.SubmitAsync(id, ct)));

        group.MapPost("{id:guid}/approve", async (Guid id, DecisionBody? body, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.ApproveAsync(id, body?.Comment, ct)));

        group.MapPost("{id:guid}/send-back", async (Guid id, DecisionBody body, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.SendBackAsync(id, body.Comment ?? "", ct)));

        group.MapPost("{id:guid}/retry", async (Guid id, RequestService requests, CancellationToken ct) =>
            Results.Ok(await requests.RetryCreationAsync(id, ct)));

        group.MapGet("{id:guid}/history", async (Guid id, RequestService requests, AppDbContext db, CancellationToken ct) =>
        {
            await requests.GetAsync(id, ct); // enforces who may see this request
            var entityId = id.ToString();
            return Results.Ok(await db.AuditEvents.AsNoTracking()
                .Where(a => a.EntityType == "request" && a.EntityId == entityId)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.At, a.ActorEmail, a.Action, a.Data })
                .ToListAsync(ct));
        });

        group.MapPost("{id:guid}/attachments", async (Guid id, IFormFile file, RequestService requests, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return Results.Ok(await requests.AddAttachmentAsync(id, file.FileName, file.ContentType, stream, ct));
        }).DisableAntiforgery();

        group.MapGet("{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, RequestService requests, CancellationToken ct) =>
        {
            var (info, content) = await requests.OpenAttachmentAsync(id, attachmentId, ct);
            return Results.File(content, info.ContentType, info.FileName);
        });

        group.MapDelete("{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, RequestService requests, CancellationToken ct) =>
        {
            await requests.RemoveAttachmentAsync(id, attachmentId, ct);
            return Results.NoContent();
        });
    }
}
