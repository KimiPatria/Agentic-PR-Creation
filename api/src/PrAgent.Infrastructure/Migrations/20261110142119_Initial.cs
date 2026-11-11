using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PrAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_suggestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserEmail = table.Column<string>(type: "text", nullable: false),
                    Proposal = table.Column<string>(type: "jsonb", nullable: false),
                    Outcome = table.Column<string>(type: "jsonb", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_suggestion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    ActorEmail = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_event", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "material_group_mapping",
                columns: table => new
                {
                    Code = table.Column<string>(type: "text", nullable: false),
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DefaultGlAccount = table.Column<string>(type: "text", nullable: true),
                    DefaultPurchasingGroup = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_group_mapping", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pr_rule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Conditions = table.Column<string>(type: "jsonb", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Allowed = table.Column<bool>(type: "boolean", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    PrDocType = table.Column<string>(type: "text", nullable: true),
                    AcctAssignmentCategory = table.Column<string>(type: "text", nullable: true),
                    ItemCategory = table.Column<string>(type: "text", nullable: true),
                    GlMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GlFixedAccount = table.Column<string>(type: "text", nullable: true),
                    RequiredFields = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsPlaceholder = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pr_rule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "section",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_section", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_request",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestorId = table.Column<string>(type: "text", nullable: false),
                    RequestorEmail = table.Column<string>(type: "text", nullable: false),
                    RequestorName = table.Column<string>(type: "text", nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Justification = table.Column<string>(type: "text", nullable: false),
                    Plant = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Currency = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewerId = table.Column<string>(type: "text", nullable: true),
                    ReviewerEmail = table.Column<string>(type: "text", nullable: true),
                    ReviewerName = table.Column<string>(type: "text", nullable: true),
                    ReviewComment = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_request", x => x.Id);
                    table.ForeignKey(
                        name: "FK_purchase_request_section_SectionId",
                        column: x => x.SectionId,
                        principalTable: "section",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "section_reviewer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_section_reviewer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_section_reviewer_section_SectionId",
                        column: x => x.SectionId,
                        principalTable: "section",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_profile",
                columns: table => new
                {
                    Email = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_profile", x => x.Email);
                    table.ForeignKey(
                        name: "FK_user_profile_section_SectionId",
                        column: x => x.SectionId,
                        principalTable: "section",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pr_attachment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pr_attachment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pr_attachment_purchase_request_RequestId",
                        column: x => x.RequestId,
                        principalTable: "purchase_request",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pr_split",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    PrDocType = table.Column<string>(type: "text", nullable: true),
                    PurchasingGroup = table.Column<string>(type: "text", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SapPrNumber = table.Column<string>(type: "text", nullable: true),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedInSapAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pr_split", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pr_split_purchase_request_RequestId",
                        column: x => x.RequestId,
                        principalTable: "purchase_request",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pr_line",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    BudgetType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CostObjectType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsStock = table.Column<bool>(type: "boolean", nullable: true),
                    MaterialNumber = table.Column<string>(type: "text", nullable: true),
                    ShortText = table.Column<string>(type: "text", nullable: true),
                    MaterialGroup = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    Uom = table.Column<string>(type: "text", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PurchasingGroup = table.Column<string>(type: "text", nullable: true),
                    PrDocType = table.Column<string>(type: "text", nullable: true),
                    AcctAssignmentCategory = table.Column<string>(type: "text", nullable: true),
                    ItemCategory = table.Column<string>(type: "text", nullable: true),
                    GlAccount = table.Column<string>(type: "text", nullable: true),
                    CostCenter = table.Column<string>(type: "text", nullable: true),
                    Wbs = table.Column<string>(type: "text", nullable: true),
                    AssetNumber = table.Column<string>(type: "text", nullable: true),
                    SplitId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pr_line", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pr_line_pr_split_SplitId",
                        column: x => x.SplitId,
                        principalTable: "pr_split",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_pr_line_purchase_request_RequestId",
                        column: x => x.RequestId,
                        principalTable: "purchase_request",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_suggestion_SessionId",
                table: "agent_suggestion",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_At",
                table: "audit_event",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_EntityType_EntityId",
                table: "audit_event",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_Status_NextAttemptAt",
                table: "outbox_message",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_pr_attachment_RequestId",
                table: "pr_attachment",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_pr_line_RequestId",
                table: "pr_line",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_pr_line_SplitId",
                table: "pr_line",
                column: "SplitId");

            migrationBuilder.CreateIndex(
                name: "IX_pr_rule_Code",
                table: "pr_rule",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pr_split_IdempotencyKey",
                table: "pr_split",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pr_split_RequestId",
                table: "pr_split",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_pr_split_SapPrNumber",
                table: "pr_split",
                column: "SapPrNumber");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_request_Reference",
                table: "purchase_request",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_request_RequestorId",
                table: "purchase_request",
                column: "RequestorId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_request_SectionId",
                table: "purchase_request",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_request_Status",
                table: "purchase_request",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_section_Code",
                table: "section",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_section_reviewer_Email",
                table: "section_reviewer",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_section_reviewer_SectionId_Email",
                table: "section_reviewer",
                columns: new[] { "SectionId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_profile_SectionId",
                table: "user_profile",
                column: "SectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_suggestion");

            migrationBuilder.DropTable(
                name: "audit_event");

            migrationBuilder.DropTable(
                name: "material_group_mapping");

            migrationBuilder.DropTable(
                name: "outbox_message");

            migrationBuilder.DropTable(
                name: "pr_attachment");

            migrationBuilder.DropTable(
                name: "pr_line");

            migrationBuilder.DropTable(
                name: "pr_rule");

            migrationBuilder.DropTable(
                name: "section_reviewer");

            migrationBuilder.DropTable(
                name: "user_profile");

            migrationBuilder.DropTable(
                name: "pr_split");

            migrationBuilder.DropTable(
                name: "purchase_request");

            migrationBuilder.DropTable(
                name: "section");
        }
    }
}
