using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 28-R2 / Finding C1 — privilege escalation fix.
    ///
    /// <para>Adds a dedicated <c>platform_role</c> column to <c>users</c> so the
    /// platform-admin axis is decoupled from the per-tenant <c>role</c>. Before
    /// this migration, <see cref="Tamma.Api.Auth.JwtService.GenerateAccessToken"/>
    /// derived the JWT <c>platformRole</c> claim from the per-tenant role
    /// (<c>role == "owner" ? "platform_admin" : "user"</c>). Since every signed-up
    /// user is auto-<c>owner</c> of their personal tenant, that mapping let
    /// every user pass the <c>OwnerAccess</c> policy on every <c>/api/admin/*</c>
    /// route — a clear privilege escalation.</para>
    ///
    /// <para>The new column:
    /// <list type="bullet">
    ///   <item><description>Is <c>character varying(20) NOT NULL DEFAULT
    ///     'user'</c>. Existing rows on UPGRADE all start as <c>"user"</c>.
    ///     The bootstrap superadmin (the first user ever created) is promoted
    ///     to <c>"platform_admin"</c> by the registration flow.</description></item>
    ///   <item><description>Is constrained at the DB level to
    ///     <c>'user' | 'platform_admin'</c> via a CHECK constraint
    ///     (<c>users_platform_role_check</c>). Matches the pattern used for
    ///     <c>role</c> (owner|admin|member) and <c>auth_method</c>
    ///     (email|github|both).</description></item>
    /// </list></para>
    ///
    /// <para>Once this migration ships, the new <c>PlatformOwnerAccess</c>
    /// policy gates every platform-scoped admin route by reading the JWT
    /// <c>platformRole</c> claim (now sourced from
    /// <see cref="Tamma.Data.Entities.User.PlatformRole"/>) instead of the
    /// tenant-role.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddUsersPlatformRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "platform_role",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "user");

            // CHECK constraint mirrors the pattern used for role/auth_method.
            // Allows future expansion (e.g. adding a "platform_support" tier)
            // without dropping existing data — just relax the constraint.
            migrationBuilder.Sql(
                "ALTER TABLE users ADD CONSTRAINT users_platform_role_check "
                + "CHECK (platform_role IN ('user', 'platform_admin'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE users DROP CONSTRAINT IF EXISTS users_platform_role_check;");

            migrationBuilder.DropColumn(
                name: "platform_role",
                table: "users");
        }
    }
}
