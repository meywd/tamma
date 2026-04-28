using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class PlatformAnalyticsHourly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_analytics_hourly",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Hour = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowsStarted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsCompleted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsFailed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AgentDispatches = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensIn = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensOut = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CostUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    ActiveTenantsAtHourEnd = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_analytics_hourly", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_analytics_hourly_TenantId_Hour",
                table: "platform_analytics_hourly",
                columns: new[] { "TenantId", "Hour" },
                descending: new[] { false, true },
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_platform_analytics_hourly_Hour_PlatformWide",
                table: "platform_analytics_hourly",
                column: "Hour",
                unique: true,
                descending: new bool[0],
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_platform_analytics_hourly_Hour_TenantId",
                table: "platform_analytics_hourly",
                columns: new[] { "Hour", "TenantId" },
                unique: true,
                descending: new[] { true, false },
                filter: "\"TenantId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_analytics_hourly");
        }
    }
}
