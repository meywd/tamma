using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddMarginPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "margin_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RefKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarkupMultiplier = table.Column<decimal>(type: "numeric(20,8)", nullable: true),
                    FixedUsdPer1M = table.Column<decimal>(type: "numeric(20,8)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_margin_policies", x => x.Id);
                    table.CheckConstraint("ck_margin_policies_has_knob", "\"MarkupMultiplier\" IS NOT NULL OR \"FixedUsdPer1M\" IS NOT NULL");
                    table.CheckConstraint("ck_margin_policies_scope", "\"Scope\" IN ('global','plan','provider')");
                    table.CheckConstraint("ck_margin_policies_status", "\"Status\" IN ('active','superseded')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_margin_policies_Window",
                table: "margin_policies",
                columns: new[] { "Scope", "RefKey", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_margin_policies_OneActivePerScopeRef",
                table: "margin_policies",
                columns: new[] { "Scope", "RefKey" },
                unique: true,
                filter: "\"Status\" = 'active'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "margin_policies");
        }
    }
}
