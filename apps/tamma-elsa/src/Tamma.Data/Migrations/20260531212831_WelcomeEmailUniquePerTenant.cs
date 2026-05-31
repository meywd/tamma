using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Story 28-5 AC2 step-10 + AC5 — exactly-once-per-tenant welcome email.
    /// Adds the partial unique index <c>(TenantId, Template) WHERE Status
    /// &lt;&gt; 'failed' AND TenantId IS NOT NULL</c> on
    /// <c>platform_email_outbox</c>. Backs the concurrent-run race in
    /// <c>QueueWelcomeEmailActivity</c>: a pending/sending/sent welcome row
    /// blocks duplicates while a terminally-failed one can be re-queued.
    /// </summary>
    public partial class WelcomeEmailUniquePerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_platform_email_outbox_tenant_template_active",
                table: "platform_email_outbox",
                columns: new[] { "TenantId", "Template" },
                unique: true,
                filter: "\"Status\" <> 'failed' AND \"TenantId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_platform_email_outbox_tenant_template_active",
                table: "platform_email_outbox");
        }
    }
}
