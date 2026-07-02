using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class BillingWebhookEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_webhook_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    StripeEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    StripeObjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "received"),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_webhook_events", x => x.Id);
                    table.CheckConstraint("ck_billing_webhook_events_status", "\"Status\" IN ('received','processing','projected','enqueued','failed','skipped')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_webhook_events_Status_ReceivedAt",
                table: "billing_webhook_events",
                columns: new[] { "Status", "ReceivedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_billing_webhook_events_TenantId",
                table: "billing_webhook_events",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_billing_webhook_events_StripeEventId",
                table: "billing_webhook_events",
                column: "StripeEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_webhook_events");
        }
    }
}
