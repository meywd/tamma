using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddAuditChainCheckpoints : Migration
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

            migrationBuilder.CreateTable(
                name: "audit_chain_checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    HeadSequence = table.Column<long>(type: "bigint", nullable: false),
                    HeadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    KeyVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_chain_checkpoints", x => x.Id);
                    table.CheckConstraint("ck_audit_chain_checkpoints_scope_tenant", "(\"Scope\" = 'platform' AND \"TenantId\" IS NULL) OR (\"Scope\" = 'tenant' AND \"TenantId\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "UX_audit_records_ChainSequence",
                table: "audit_records",
                column: "ChainSequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_chain_checkpoints_scope_seq",
                table: "audit_chain_checkpoints",
                columns: new[] { "Scope", "TenantId", "HeadSequence" });

            // Story 37-2 (AC11) — append-only defence-in-depth trigger.
            migrationBuilder.Sql(AuditRecordsAppendOnlyTrigger.UpSql);

            // Story 37-2 (code-review fix, AC7 hardening) — the signed checkpoints
            // are the anchor that reveals tail-truncation, so make them write-once
            // too. Without this, deleting recent records + the covering checkpoint
            // would verify as Ok.
            migrationBuilder.Sql(AuditChainCheckpointsAppendOnlyTrigger.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditChainCheckpointsAppendOnlyTrigger.DownSql);
            migrationBuilder.Sql(AuditRecordsAppendOnlyTrigger.DownSql);

            migrationBuilder.DropTable(
                name: "audit_chain_checkpoints");

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
