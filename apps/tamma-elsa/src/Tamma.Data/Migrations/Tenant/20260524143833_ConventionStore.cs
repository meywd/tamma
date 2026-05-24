using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <summary>
    /// Story 27-8 — <c>conventions</c> table: entity + schema for the
    /// per-(role, action) convention store.
    ///
    /// <para>Two-tier scoping:</para>
    /// <list type="bullet">
    ///   <item><c>tenant_id IS NULL</c> → system-default row (seeded in
    ///     Story 27-16; NOT seeded here).</item>
    ///   <item><c>tenant_id NOT NULL</c> → tenant override (tenant admin
    ///     owns it; member users cannot personalise).</item>
    /// </list>
    ///
    /// <para>Differs from <c>prompt_overrides</c>: no <c>user_id</c> column,
    /// no <c>principal_xor</c> CHECK — this is intentionally a simpler
    /// two-tier design. <c>tenant_id</c> is the sole discriminator.</para>
    ///
    /// <para>The unique index on <c>(tenant_id, role, action)</c> uses
    /// <c>NULLS NOT DISTINCT</c> (raw SQL — EF Core 8 cannot emit this
    /// natively) so exactly ONE system-default row per <c>(role, action)</c>
    /// cell is permitted. NULLS NOT DISTINCT requires Postgres ≥ 15
    /// (production runs PG17).</para>
    ///
    /// <para>No separate B-tree index is created: the unique index above
    /// already provides an optimal B-tree seek on
    /// <c>(tenant_id, role, action)</c> — adding a duplicate non-unique
    /// index would be redundant overhead.</para>
    ///
    /// <para>No RLS: convention resolution crosses the tenant boundary
    /// (service layer reads system-default rows whose tenant_id IS NULL),
    /// so RLS would block legitimate cross-tenant reads.</para>
    ///
    /// <para>This migration is online-safe: CREATE TABLE + CREATE INDEX only;
    /// no table-lock bulk INSERT; DEFAULT values are set so all columns are
    /// server-defaulted at write time.</para>
    /// </summary>
    public partial class ConventionStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conventions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conventions", x => x.Id);
                });

            // NULLS NOT DISTINCT unique index on (TenantId, Role, Action).
            // Raw SQL because EF Core 8.0 doesn't expose the NULLS NOT
            // DISTINCT option on CreateIndex (added in EF 9 + Npgsql provider
            // AreNullsDistinct). The semantics here are critical: a system-
            // default row has TenantId = NULL; without NULLS NOT DISTINCT
            // every null-tenant row would be considered distinct from every
            // other null-tenant row regardless of (Role, Action), allowing
            // multiple system defaults per cell. With NULLS NOT DISTINCT,
            // NULLs are treated as equal, so the constraint correctly
            // enforces exactly-one system-default per (role, action) cell.
            // Requires Postgres >= 15 (production runs PG17).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_conventions_TenantId_Role_Action\" "
              + "ON conventions (\"TenantId\", \"Role\", \"Action\") "
              + "NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DropTable cascades — it implicitly drops all indexes on the
            // table including the raw-SQL NULLS NOT DISTINCT unique index
            // (IX_conventions_TenantId_Role_Action). No separate DROP INDEX
            // is needed here. (Contrast with the PromptOverride migration's
            // explicit DROP INDEX: that index was on a table that is NOT
            // dropped in that migration's Down(), making the explicit drop
            // necessary. That pattern does not apply here.)
            migrationBuilder.DropTable(
                name: "conventions");
        }
    }
}
