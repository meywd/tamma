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
            // Story 37-2 (code-review fix) — re-type PayloadJson jsonb -> text so the
            // exact insert-time bytes the hash-chain is computed over round-trip
            // identically on read-back (jsonb reorders keys / strips whitespace /
            // normalizes numbers, which would make every chain verify as TAMPERED).
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" DROP DEFAULT;");
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" TYPE text USING \"PayloadJson\"::text;");
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" SET DEFAULT '{}';");

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

            // Revert PayloadJson text -> jsonb.
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" DROP DEFAULT;");
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" TYPE jsonb USING \"PayloadJson\"::jsonb;");
            migrationBuilder.Sql(
                "ALTER TABLE audit_records ALTER COLUMN \"PayloadJson\" SET DEFAULT '{}'::jsonb;");
        }
    }
}
