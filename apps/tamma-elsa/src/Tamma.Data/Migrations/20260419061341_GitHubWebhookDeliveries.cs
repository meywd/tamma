using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_webhook_deliveries",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstallationId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_deliveries", x => x.DeliveryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_InstallationId_ReceivedAt",
                table: "github_webhook_deliveries",
                columns: new[] { "InstallationId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_ReceivedAt",
                table: "github_webhook_deliveries",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_webhook_deliveries");
        }
    }
}
