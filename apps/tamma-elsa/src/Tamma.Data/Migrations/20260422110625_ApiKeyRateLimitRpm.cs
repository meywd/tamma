using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <summary>
    /// Story 28-7 deferred-item — adds the <c>RateLimitRpm</c> shadow column
    /// to <c>api_keys</c> so the <see cref="ControlPlaneDbContext"/> shadow
    /// property round-trips against the legacy <see cref="TammaDbContext"/>
    /// schema (dev / single-pod case where both contexts share a DB).
    ///
    /// <para>EF's model differ does not emit DDL for shadow properties that
    /// are only declared on a sibling context; the column is added here
    /// explicitly and guarded with <c>IF NOT EXISTS</c> so it's idempotent
    /// against any environment where the CP migration already ran.</para>
    /// </summary>
    public partial class ApiKeyRateLimitRpm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE api_keys ADD COLUMN IF NOT EXISTS \"RateLimitRpm\" integer;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE api_keys DROP COLUMN IF EXISTS \"RateLimitRpm\";");
        }
    }
}
