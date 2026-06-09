using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class TenancyP0_ScopeAndRoleChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_users_platform_role",
                table: "users",
                sql: "\"platform_role\" IN ('user','platform_admin')");

            // Guard: SchemaHardeningPhase1 (old chain, 20260419015726) already
            // added ck_api_keys_scope with a narrower set ('user','installation',
            // 'service'). Drop-and-recreate so the constraint is always widened
            // to the Phase-0 transitional set — safe on both fresh and old-chain
            // databases.
            migrationBuilder.Sql(
                "ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS ck_api_keys_scope;");
            migrationBuilder.Sql(
                "ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scope "
                + "CHECK (\"Scope\" IN ('platform','user','installation','service','tenant'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_platform_role",
                table: "users");

            // Restore the original narrow-set constraint that SchemaHardeningPhase1
            // would have left. Fresh databases (no old chain) simply drop it.
            migrationBuilder.Sql(
                "ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS ck_api_keys_scope;");
            migrationBuilder.Sql(
                "ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scope "
                + "CHECK (\"Scope\" IN ('user','installation','service'));");
        }
    }
}
