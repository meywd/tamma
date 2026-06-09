using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Program.cs wipes a known set of Tamma tables + __ControlPlaneMigrationsHistory
            // (formerly __TammaMigrationsHistory) on every deploy to work around a 42P07
            // race with EF's history table. `queued_tasks` is not in that list, so on the
            // second migrate replay the CreateTable below would collide. Drop it
            // defensively here so the migration is idempotent across the
            // wipe-and-recreate dance. Harmless for first-ever deploys.
            migrationBuilder.Sql("DROP TABLE IF EXISTS queued_tasks CASCADE;");

            migrationBuilder.CreateTable(
                name: "queued_tasks",
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
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queued_tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_queued_tasks_Status_CreatedAt",
                table: "queued_tasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_queued_tasks_TenantId_Status",
                table: "queued_tasks",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "queued_tasks");
        }
    }
}
