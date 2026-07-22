using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddAcceptanceRulesOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "acceptance_rules_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentTypeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acceptance_rules_overrides", x => x.Id);
                    table.CheckConstraint("ck_acceptance_rules_overrides_principal_xor", "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_rules_overrides_UserId_TenantId_DocumentTypeKey",
                table: "acceptance_rules_overrides",
                columns: new[] { "UserId", "TenantId", "DocumentTypeKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acceptance_rules_overrides");
        }
    }
}
