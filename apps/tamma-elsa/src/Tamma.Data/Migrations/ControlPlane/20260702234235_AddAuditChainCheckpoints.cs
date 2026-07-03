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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditRecordsAppendOnlyTrigger.DownSql);

            migrationBuilder.DropTable(
                name: "audit_chain_checkpoints");

            migrationBuilder.DropIndex(
                name: "UX_audit_records_ChainSequence",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "ChainSequence",
                table: "audit_records");
        }
    }
}
