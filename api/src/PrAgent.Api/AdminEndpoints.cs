using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Domain.Rules;
using PrAgent.Sap.Mock;

namespace PrAgent.Api;

public sealed record RuleBody(
    string Code,
    string Description,
    Dictionary<string, string> Conditions,
    int Priority,
    bool Allowed,
    string? Message,
    string? PrDocType,
    string? AcctAssignmentCategory,
    string? ItemCategory,
    GlMode GlMode,
    string? GlFixedAccount,
    List<string>? RequiredFields,
    bool IsActive,
    DateOnly ValidFrom,
    DateOnly? ValidTo);

public sealed record ReviewerBody(string Email, string Name);

public sealed record SectionBody(string Code, string Name, List<ReviewerBody>? Reviewers);

public sealed record MaterialGroupMappingBody(ItemType? ItemType, string? DefaultGlAccount, string? DefaultPurchasingGroup);

/// <summary>What the PR admin maintains: rules, sections and reviewers, material group mappings, and the job monitor.</summary>
public static class AdminEndpoints
{
    private static readonly string[] RequirableFields = [Fields.MaterialNumber, Fields.CostCenter, Fields.Wbs, Fields.AssetNumber];

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(AuthRegistration.AdminPolicy);

        // ---- Rules ----------------------------------------------------------------------------
        admin.MapGet("rules", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(new
            {
                rules = await db.Rules.AsNoTracking().OrderBy(r => r.Code).ToListAsync(ct),
                factDomains = RuleFacts.Domains,
                requirableFields = RequirableFields,
            }));

        admin.MapPost("rules", async (RuleBody body, AppDbContext db, ICurrentUser user, AuditLog audit, BusinessClock clock, CancellationToken ct) =>
        {
            Validate(body);
            if (await db.Rules.AnyAsync(r => r.Code == body.Code.Trim(), ct))
                throw new DomainException($"A rule with code {body.Code.Trim()} already exists.");

            var rule = new PrRule { Id = Guid.NewGuid() };
            Apply(rule, body, user, clock);
            db.Rules.Add(rule);
            audit.Record(user.AsPerson(), "rule.created", "rule", rule.Id.ToString(), rule);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/rules/{rule.Id}", rule);
        });

        admin.MapPut("rules/{id:guid}", async (Guid id, RuleBody body, AppDbContext db, ICurrentUser user, AuditLog audit, BusinessClock clock, CancellationToken ct) =>
        {
            Validate(body);
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule not found.");
            if (await db.Rules.AnyAsync(r => r.Code == body.Code.Trim() && r.Id != id, ct))
                throw new DomainException($"A rule with code {body.Code.Trim()} already exists.");

            var before = Snapshot(rule);
            Apply(rule, body, user, clock);
            audit.Record(user.AsPerson(), "rule.updated", "rule", rule.Id.ToString(), new { before, after = Snapshot(rule) });
            await db.SaveChangesAsync(ct);
            return Results.Ok(rule);
        });

        admin.MapDelete("rules/{id:guid}", async (Guid id, AppDbContext db, ICurrentUser user, AuditLog audit, CancellationToken ct) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule not found.");
            db.Rules.Remove(rule);
            audit.Record(user.AsPerson(), "rule.deleted", "rule", rule.Id.ToString(), Snapshot(rule));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Sections and reviewers -----------------------------------------------------------
        admin.MapGet("sections", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sections.AsNoTracking().Include(s => s.Reviewers).OrderBy(s => s.Name).ToListAsync(ct)));

        admin.MapPost("sections", async (SectionBody body, AppDbContext db, ICurrentUser user, AuditLog audit, CancellationToken ct) =>
        {
            var section = new Section { Id = Guid.NewGuid() };
            await ApplyAsync(section, body, db, ct);
            db.Sections.Add(section);
            audit.Record(user.AsPerson(), "section.created", "section", section.Id.ToString(), body);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/sections/{section.Id}", section);
        });

        admin.MapPut("sections/{id:guid}", async (Guid id, SectionBody body, AppDbContext db, ICurrentUser user, AuditLog audit, CancellationToken ct) =>
        {
            var section = await db.Sections.Include(s => s.Reviewers).FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Section not found.");
            await ApplyAsync(section, body, db, ct);
            audit.Record(user.AsPerson(), "section.updated", "section", section.Id.ToString(), body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(section);
        });

        admin.MapDelete("sections/{id:guid}", async (Guid id, AppDbContext db, ICurrentUser user, AuditLog audit, CancellationToken ct) =>
        {
            var section = await db.Sections.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Section not found.");
            if (await db.Requests.AnyAsync(r => r.SectionId == id, ct))
                throw new DomainException("This section has requests and cannot be deleted.");
            db.Sections.Remove(section);
            audit.Record(user.AsPerson(), "section.deleted", "section", section.Id.ToString(), new { section.Code, section.Name });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Material group mapping -----------------------------------------------------------
        admin.MapGet("material-groups", async (MasterDataService masterData, CancellationToken ct) =>
            Results.Ok(await masterData.MaterialGroupsAsync(ct)));

        admin.MapPut("material-groups/{code}", async (string code, MaterialGroupMappingBody body, AppDbContext db, MasterDataService masterData,
            ICurrentUser user, AuditLog audit, BusinessClock clock, CancellationToken ct) =>
        {
            var group = (await masterData.MaterialGroupsAsync(ct)).FirstOrDefault(g => g.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFoundException($"Material group {code} does not exist in SAP.");

            var gl = Blank(body.DefaultGlAccount);
            if (gl is not null && (await masterData.GlAccountsAsync(ct)).All(a => a.Code != gl))
                throw new DomainException($"G/L account {gl} does not exist in SAP.");
            var purchasingGroup = Blank(body.DefaultPurchasingGroup);
            if (purchasingGroup is not null && (await masterData.PurchasingGroupsAsync(ct)).All(p => p.Code != purchasingGroup))
                throw new DomainException($"Purchasing group {purchasingGroup} does not exist in SAP.");

            var mapping = await db.MaterialGroupMappings.FirstOrDefaultAsync(m => m.Code == group.Code, ct);
            if (mapping is null)
            {
                mapping = new MaterialGroupMapping { Code = group.Code };
                db.MaterialGroupMappings.Add(mapping);
            }
            mapping.ItemType = body.ItemType;
            mapping.DefaultGlAccount = gl;
            mapping.DefaultPurchasingGroup = purchasingGroup;
            mapping.UpdatedAt = clock.Now;
            mapping.UpdatedBy = user.Email;
            audit.Record(user.AsPerson(), "material_group.mapped", "material_group", group.Code, body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MaterialGroupInfo(group.Code, group.Name, mapping.ItemType, mapping.DefaultGlAccount, mapping.DefaultPurchasingGroup));
        });

        // ---- Monitor --------------------------------------------------------------------------
        admin.MapGet("jobs", async (AppDbContext db, CancellationToken ct) =>
        {
            var jobs = await db.Outbox.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct);
            var ids = jobs.Where(j => j.RequestId != null).Select(j => j.RequestId!.Value).Distinct().ToList();
            var references = await db.Requests.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
            return Results.Ok(jobs.Select(j => new
            {
                j.Id, j.Type, j.Status, j.Attempts, j.CreatedAt, j.NextAttemptAt, j.ProcessedAt, j.LastError, j.RequestId,
                RequestReference = j.RequestId is { } id && references.TryGetValue(id, out var reference) ? reference : null,
                Summary = j.Type == OutboxMessage.Email ? EmailSubject(j.Payload) : "Create PR(s) in SAP",
            }));
        });

        admin.MapPost("jobs/{id:guid}/retry", async (Guid id, AppDbContext db, ICurrentUser user, AuditLog audit, BusinessClock clock, CancellationToken ct) =>
        {
            var job = await db.Outbox.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Job not found.");
            if (job.Status != OutboxStatus.Failed) throw new DomainException("Only a failed job can be retried.");
            job.Status = OutboxStatus.Pending;
            job.Attempts = 0;
            job.NextAttemptAt = clock.Now;
            job.ProcessedAt = null;
            audit.Record(user.AsPerson(), "job.retried", "job", job.Id.ToString(), new { job.Type, job.RequestId });
            await db.SaveChangesAsync(ct);
            return Results.Ok(job);
        });

        admin.MapGet("audit", async (string? entityType, string? entityId, int? take, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.AuditEvents.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrWhiteSpace(entityId)) query = query.Where(a => a.EntityId == entityId);
            return Results.Ok(await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync(ct));
        });

        admin.MapPost("master-data/refresh", (MasterDataCacheState state, ICurrentUser user, BusinessClock clock) =>
        {
            state.Invalidate(clock.Now);
            return Results.Ok(new { state.InvalidatedAt });
        });

        // What the mock adapter has "created": the exact payloads that would have gone to SAP.
        admin.MapGet("sap", (IOptions<SapOptions> sap, MasterDataCacheState state, IServiceProvider services) => Results.Ok(new
        {
            mode = sap.Value.Mode,
            simulated = CommonEndpoints.IsSimulated(sap.Value),
            cacheInvalidatedAt = state.InvalidatedAt,
            simulatedPrs = services.GetService<MockSapLedger>()?.Created,
        }));
    }

    private static void Validate(RuleBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Code)) throw new DomainException("A rule needs a code.");
        if (string.IsNullOrWhiteSpace(body.Description)) throw new DomainException("A rule needs a description.");

        foreach (var (fact, value) in body.Conditions ?? [])
        {
            if (!RuleFacts.Domains.TryGetValue(fact, out var domain))
                throw new DomainException($"'{fact}' is not a fact a rule can match on. Use: {string.Join(", ", RuleFacts.Domains.Keys)}.");
            if (!domain.Contains(value, StringComparer.OrdinalIgnoreCase))
                throw new DomainException($"'{value}' is not a value of {fact}. Use: {string.Join(", ", domain)}.");
        }

        var unknown = (body.RequiredFields ?? []).FirstOrDefault(f => !RequirableFields.Contains(f));
        if (unknown is not null)
            throw new DomainException($"'{unknown}' cannot be made a required field. Use: {string.Join(", ", RequirableFields)}.");

        if (body.ValidTo is { } to && to < body.ValidFrom) throw new DomainException("Valid-to is before valid-from.");

        if (body.Allowed)
        {
            if (string.IsNullOrWhiteSpace(body.PrDocType)) throw new DomainException("An allowed rule needs a PR document type.");
            if (body.AcctAssignmentCategory is null) throw new DomainException("An allowed rule needs an account assignment category (empty text for SAP blank).");
            if (body.ItemCategory is null) throw new DomainException("An allowed rule needs an item category (empty text for SAP blank).");
            if (body.GlMode == GlMode.Fixed && string.IsNullOrWhiteSpace(body.GlFixedAccount)) throw new DomainException("G/L mode Fixed needs a G/L account.");
        }
        else if (string.IsNullOrWhiteSpace(body.Message))
        {
            throw new DomainException("A rule that forbids a combination needs a message that tells the requestor why.");
        }
    }

    private static void Apply(PrRule rule, RuleBody body, ICurrentUser user, BusinessClock clock)
    {
        rule.Code = body.Code.Trim();
        rule.Description = body.Description.Trim();
        rule.Conditions = new Dictionary<string, string>(
            (body.Conditions ?? []).Select(c => KeyValuePair.Create(
                RuleFacts.Domains.Keys.First(k => k.Equals(c.Key, StringComparison.OrdinalIgnoreCase)),
                RuleFacts.Domains[c.Key].First(v => v.Equals(c.Value, StringComparison.OrdinalIgnoreCase)))),
            StringComparer.OrdinalIgnoreCase);
        rule.Priority = body.Priority;
        rule.Allowed = body.Allowed;
        rule.Message = Blank(body.Message);
        rule.PrDocType = body.Allowed ? body.PrDocType!.Trim() : null;
        rule.AcctAssignmentCategory = body.Allowed ? body.AcctAssignmentCategory!.Trim() : null;
        rule.ItemCategory = body.Allowed ? body.ItemCategory!.Trim() : null;
        rule.GlMode = body.Allowed ? body.GlMode : GlMode.None;
        rule.GlFixedAccount = body.Allowed && body.GlMode == GlMode.Fixed ? body.GlFixedAccount!.Trim() : null;
        rule.RequiredFields = body.Allowed ? [.. (body.RequiredFields ?? []).Distinct()] : [];
        rule.IsActive = body.IsActive;
        rule.ValidFrom = body.ValidFrom;
        rule.ValidTo = body.ValidTo;
        rule.IsPlaceholder = false; // an admin has taken ownership of this row
        rule.UpdatedAt = clock.Now;
        rule.UpdatedBy = user.Email;
    }

    private static object Snapshot(PrRule r) => new
    {
        r.Code, r.Description, r.Conditions, r.Priority, r.Allowed, r.Message, r.PrDocType, r.AcctAssignmentCategory,
        r.ItemCategory, r.GlMode, r.GlFixedAccount, r.RequiredFields, r.IsActive, r.ValidFrom, r.ValidTo, r.IsPlaceholder,
    };

    private static async Task ApplyAsync(Section section, SectionBody body, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
            throw new DomainException("A section needs a code and a name.");
        var code = body.Code.Trim().ToUpperInvariant();
        if (await db.Sections.AnyAsync(s => s.Code == code && s.Id != section.Id, ct))
            throw new DomainException($"A section with code {code} already exists.");

        section.Code = code;
        section.Name = body.Name.Trim();

        var wanted = (body.Reviewers ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Email))
            .Select(r => new ReviewerBody(r.Email.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(r.Name) ? r.Email.Trim() : r.Name.Trim()))
            .DistinctBy(r => r.Email)
            .ToList();
        if (wanted.Any(r => !r.Email.Contains('@'))) throw new DomainException("Reviewer email addresses must be valid.");

        section.Reviewers.RemoveAll(existing => wanted.All(w => w.Email != existing.Email));
        foreach (var reviewer in wanted)
        {
            var existing = section.Reviewers.FirstOrDefault(r => r.Email == reviewer.Email);
            if (existing is null)
                section.Reviewers.Add(new SectionReviewer { Id = Guid.NewGuid(), SectionId = section.Id, Email = reviewer.Email, Name = reviewer.Name });
            else
                existing.Name = reviewer.Name;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EmailSubject(string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("subject", out var subject) ? subject.GetString() ?? "Email" : "Email";
        }
        catch (System.Text.Json.JsonException)
        {
            return "Email";
        }
    }
}
