using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddAnalyticsUsageFactTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_usage_daily",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepoId = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CostBasis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokensIn = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensOut = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CostUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    PlatformBilledUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    WorkflowsStarted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsCompleted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsFailed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AgentDispatches = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_usage_daily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "analytics_usage_hourly",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Hour = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepoId = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CostBasis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokensIn = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensOut = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CostUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    PlatformBilledUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    WorkflowsStarted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsCompleted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsFailed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AgentDispatches = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_usage_hourly", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_usage_daily_breakdown",
                table: "analytics_usage_daily",
                columns: new[] { "Day", "Provider", "AgentId", "WorkflowDefinitionId", "CostBasis" });

            migrationBuilder.CreateIndex(
                name: "UX_analytics_usage_daily_dims",
                table: "analytics_usage_daily",
                columns: new[] { "Day", "Provider", "AgentId", "WorkflowDefinitionId", "RepoId", "CostBasis" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_analytics_usage_hourly_breakdown",
                table: "analytics_usage_hourly",
                columns: new[] { "Hour", "Provider", "AgentId", "WorkflowDefinitionId", "CostBasis" });

            migrationBuilder.CreateIndex(
                name: "UX_analytics_usage_hourly_dims",
                table: "analytics_usage_hourly",
                columns: new[] { "Hour", "Provider", "AgentId", "WorkflowDefinitionId", "RepoId", "CostBasis" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_usage_daily");

            migrationBuilder.DropTable(
                name: "analytics_usage_hourly");
        }
    }
}
