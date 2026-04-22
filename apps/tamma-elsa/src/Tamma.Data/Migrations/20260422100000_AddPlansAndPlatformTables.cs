using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Wave A.5 post-merge reconciliation: adds the four tables Story 28-1
    /// / 28-6 introduced but that the legacy root-migration chain never
    /// created — <c>plans</c>, <c>platform_events</c>,
    /// <c>platform_queued_tasks</c>, <c>platform_email_outbox</c>. These
    /// tables live on <see cref="Tamma.Data.ControlPlaneDbContext"/> and
    /// hold cross-tenant / pre-tenant-resolution data.
    ///
    /// <para>Also layers the Epic 28 shadow columns onto <c>tenants</c>
    /// (Status, PlanId, EncryptedConnectionString, KekVersion,
    /// FailureReason, DeleteRequestedAt) and the Story 28-7
    /// rate-limit / prefix-index columns onto <c>api_keys</c>
    /// (RateLimitRpm, KeyPrefix / RevokedAt indexes). Wave A.5
    /// collapsed the two EF contexts and the shadow-column ALTER
    /// TABLEs needed to be reconciled in a single post-merge
    /// migration to keep the chain linear.</para>
    /// </summary>
    public partial class AddPlansAndPlatformTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MonthlyPriceUsd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Quotas = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plans_Slug",
                table: "plans",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateTable(
                name: "platform_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_CreatedAt",
                table: "platform_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_TenantId",
                table: "platform_events",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_UserId",
                table: "platform_events",
                column: "UserId",
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_Type_CreatedAt",
                table: "platform_events",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateTable(
                name: "platform_queued_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    InstallationId = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_queued_tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_InstallationId",
                table: "platform_queued_tasks",
                column: "InstallationId",
                filter: "\"InstallationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_TenantId",
                table: "platform_queued_tasks",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_Status_CreatedAt",
                table: "platform_queued_tasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateTable(
                name: "platform_email_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_email_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_email_outbox_Status_NextAttemptAt",
                table: "platform_email_outbox",
                columns: new[] { "Status", "NextAttemptAt" });

            // ── tenants Epic 28 shadow columns ──
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedConnectionString",
                table: "tenants",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KekVersion",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeleteRequestedAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Status",
                table: "tenants",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_PlanId",
                table: "tenants",
                column: "PlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_plans_PlanId",
                table: "tenants",
                column: "PlanId",
                principalTable: "plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ── api_keys Story 28-7 columns + indexes ──
            migrationBuilder.AddColumn<int>(
                name: "RateLimitRpm",
                table: "api_keys",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyPrefix",
                table: "api_keys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_RevokedAt",
                table: "api_keys",
                column: "RevokedAt",
                filter: "\"RevokedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_api_keys_RevokedAt", table: "api_keys");
            migrationBuilder.DropIndex(name: "IX_api_keys_KeyPrefix", table: "api_keys");
            migrationBuilder.DropColumn(name: "RateLimitRpm", table: "api_keys");

            migrationBuilder.DropForeignKey(name: "FK_tenants_plans_PlanId", table: "tenants");
            migrationBuilder.DropIndex(name: "IX_tenants_PlanId", table: "tenants");
            migrationBuilder.DropIndex(name: "IX_tenants_Status", table: "tenants");
            migrationBuilder.DropColumn(name: "DeleteRequestedAt", table: "tenants");
            migrationBuilder.DropColumn(name: "FailureReason", table: "tenants");
            migrationBuilder.DropColumn(name: "KekVersion", table: "tenants");
            migrationBuilder.DropColumn(name: "EncryptedConnectionString", table: "tenants");
            migrationBuilder.DropColumn(name: "PlanId", table: "tenants");
            migrationBuilder.DropColumn(name: "Status", table: "tenants");

            migrationBuilder.DropTable(name: "platform_email_outbox");
            migrationBuilder.DropTable(name: "platform_queued_tasks");
            migrationBuilder.DropTable(name: "platform_events");
            migrationBuilder.DropTable(name: "plans");
        }
    }
}
