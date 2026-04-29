using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlatformWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlatformKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstallationExternalId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_webhook_deliveries", x => x.Id);
                    table.CheckConstraint("CK_platform_webhook_deliveries_PlatformKind", "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_webhook_deliveries_ReceivedAt",
                table: "platform_webhook_deliveries",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "UX_platform_webhook_deliveries_Kind_DeliveryId",
                table: "platform_webhook_deliveries",
                columns: new[] { "PlatformKind", "DeliveryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_webhook_deliveries");
        }
    }
}
