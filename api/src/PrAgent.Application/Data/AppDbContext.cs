using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PrAgent.Domain;
using PrAgent.Domain.Requests;
using PrAgent.Domain.Rules;

namespace PrAgent.Application.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PurchaseRequest> Requests => Set<PurchaseRequest>();
    public DbSet<PrRule> Rules => Set<PrRule>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<SectionReviewer> SectionReviewers => Set<SectionReviewer>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<MaterialGroupMapping> MaterialGroupMappings => Set<MaterialGroupMapping>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AgentSuggestion> AgentSuggestions => Set<AgentSuggestion>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Enums are stored by name so the tables read without a lookup.
        configuration.Properties<BudgetType>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<ItemType>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<CostObjectType>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<RequestStatus>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<SplitStatus>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<GlMode>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<OutboxStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PurchaseRequest>(e =>
        {
            e.ToTable("purchase_request");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Reference).HasMaxLength(30);
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => x.RequestorId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Plant).HasMaxLength(10);
            e.Property(x => x.Currency).HasMaxLength(5);

            // Postgres' xmin system column as an optimistic concurrency token: two reviewers
            // deciding the same request at once cannot both win.
            e.Property<uint>("RowVersion").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey("RequestId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Splits).WithOne().HasForeignKey("RequestId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey("RequestId").IsRequired().OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Splits).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        model.Entity<PrLine>(e =>
        {
            e.ToTable("pr_line");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.HasOne<PrSplit>().WithMany().HasForeignKey(x => x.SplitId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<PrSplit>(e =>
        {
            e.ToTable("pr_split");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.SapPrNumber);
        });

        model.Entity<Attachment>(e =>
        {
            e.ToTable("pr_attachment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        model.Entity<PrRule>(e =>
        {
            e.ToTable("pr_rule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Conditions)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                    v => new Dictionary<string, string>(
                        JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonSerializerOptions.Default) ?? new(),
                        StringComparer.OrdinalIgnoreCase),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key.ToLowerInvariant(), pair.Value)),
                        v => new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase)));
        });

        model.Entity<Section>(e =>
        {
            e.ToTable("section");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Reviewers).WithOne().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SectionReviewer>(e =>
        {
            e.ToTable("section_reviewer");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.SectionId, x.Email }).IsUnique();
            e.HasIndex(x => x.Email);
        });

        model.Entity<UserProfile>(e =>
        {
            e.ToTable("user_profile");
            e.HasKey(x => x.Email);
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<MaterialGroupMapping>(e =>
        {
            e.ToTable("material_group_mapping");
            e.HasKey(x => x.Code);
        });

        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.At);
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_message");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        model.Entity<AgentSuggestion>(e =>
        {
            e.ToTable("agent_suggestion");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Proposal).HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasColumnType("jsonb");
            e.HasIndex(x => x.SessionId);
        });
    }
}
