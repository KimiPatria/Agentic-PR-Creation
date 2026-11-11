using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Domain;
using PrAgent.Domain.Rules;

namespace PrAgent.Infrastructure;

/// <summary>Applies migrations and seeds what an empty database needs to be usable.</summary>
public static class DbInitialiser
{
    public static async Task InitialiseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<BusinessClock>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitialiser");

        await db.Database.MigrateAsync(ct);

        if (!await db.Rules.AnyAsync(ct))
        {
            db.Rules.AddRange(PlaceholderRules.Create(clock.Now));
            logger.LogWarning("Seeded 16 PLACEHOLDER rules. Replace them in the admin view once the real rule table is known.");
        }

        if (configuration.GetValue("Seed:DemoData", false))
            await SeedDemoDataAsync(db, clock.Now, logger, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Demo sections, reviewers and material group mappings that match the mock SAP data set and the
    /// development personas. Turn off with Seed:DemoData = false.
    /// </summary>
    private static async Task SeedDemoDataAsync(AppDbContext db, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        if (!await db.Sections.AnyAsync(ct))
        {
            Section Make(string code, string name, params (string Email, string Name)[] reviewers)
            {
                var section = new Section { Id = Guid.NewGuid(), Code = code, Name = name };
                section.Reviewers = reviewers.Select(r => new SectionReviewer { Id = Guid.NewGuid(), SectionId = section.Id, Email = r.Email, Name = r.Name }).ToList();
                return section;
            }

            db.Sections.AddRange(
                Make("IT-INFRA", "IT Infrastructure", (DemoUsers.Budi.Email, DemoUsers.Budi.Name)),
                Make("IT-APPS", "IT Applications", (DemoUsers.Budi.Email, DemoUsers.Budi.Name)),
                Make("GA", "General Affairs", (DemoUsers.Dewi.Email, DemoUsers.Dewi.Name)),
                Make("ENG", "Engineering and Maintenance", (DemoUsers.Dewi.Email, DemoUsers.Dewi.Name)));
            logger.LogInformation("Seeded demo sections and reviewers.");
        }

        if (!await db.MaterialGroupMappings.AnyAsync(ct))
        {
            MaterialGroupMapping Map(string code, ItemType type, string gl, string purchasingGroup) =>
                new() { Code = code, ItemType = type, DefaultGlAccount = gl, DefaultPurchasingGroup = purchasingGroup, UpdatedAt = now, UpdatedBy = "seed" };

            db.MaterialGroupMappings.AddRange(
                Map("IT01", ItemType.Material, "61100000", "P01"),
                Map("IT02", ItemType.Material, "61100000", "P01"),
                Map("IT03", ItemType.Material, "61200000", "P01"),
                Map("IT10", ItemType.Service, "61300000", "P02"),
                Map("GA01", ItemType.Material, "62100000", "P03"),
                Map("GA02", ItemType.Material, "62200000", "P03"),
                Map("EN01", ItemType.Material, "63100000", "P04"),
                Map("EN10", ItemType.Service, "63200000", "P04"),
                Map("PS01", ItemType.Service, "64100000", "P05"),
                Map("PS02", ItemType.Service, "64200000", "P05"));
            logger.LogInformation("Seeded demo material group mappings.");
        }
    }
}

/// <summary>The development personas offered by the dev login (Auth:Mode = Dev).</summary>
public static class DemoUsers
{
    public sealed record DemoUser(string Email, string Name, string[] Roles);

    public static readonly DemoUser Rina = new("rina.requestor@example.com", "Rina (Requestor)", [Roles.Requestor]);
    public static readonly DemoUser Budi = new("budi.reviewer@example.com", "Budi (Reviewer, IT)", [Roles.Requestor, Roles.Reviewer]);
    public static readonly DemoUser Dewi = new("dewi.reviewer@example.com", "Dewi (Reviewer, GA and Engineering)", [Roles.Requestor, Roles.Reviewer]);
    public static readonly DemoUser Sari = new("sari.admin@example.com", "Sari (PR Admin)", [Roles.Requestor, Roles.Admin]);

    public static readonly DemoUser[] All = [Rina, Budi, Dewi, Sari];
}
