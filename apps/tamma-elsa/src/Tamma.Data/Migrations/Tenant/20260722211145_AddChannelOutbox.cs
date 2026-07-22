using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddChannelOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    DecisionSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_outbox_tenant_audience_status",
                table: "channel_outbox",
                columns: new[] { "TenantId", "Audience", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_outbox_tenant_recipient_status",
                table: "channel_outbox",
                columns: new[] { "TenantId", "RecipientUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_outbox");
        }
    }
}
