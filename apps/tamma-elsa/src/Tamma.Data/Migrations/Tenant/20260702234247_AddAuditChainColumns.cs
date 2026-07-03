using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddAuditChainColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChainSequence",
                table: "audit_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_audit_records_ChainSequence",
                table: "audit_records",
                column: "ChainSequence",
                unique: true);

            // Story 37-2 (AC11) — append-only defence-in-depth trigger (per tenant schema).
            migrationBuilder.Sql(AuditRecordsAppendOnlyTrigger.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditRecordsAppendOnlyTrigger.DownSql);

            migrationBuilder.DropIndex(
                name: "UX_audit_records_ChainSequence",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "ChainSequence",
                table: "audit_records");
        }
    }
}
