using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Api.Services.Secrets.Postgres.Migrations
{
    /// <summary>
    /// Story 29-1 (review fix) — widen the <c>secrets</c> unique index from
    /// <c>(Scope, Name)</c> to <c>(Scope, TenantId, Name)</c> with
    /// <c>NULLS NOT DISTINCT</c>, so two tenants can each hold a same-named
    /// tenant-scoped secret while platform-scope name uniqueness (all
    /// platform rows share <c>TenantId = NULL</c>) is still enforced.
    ///
    /// <para>Scoped to <see cref="SecretsDbContext"/> only
    /// (<c>__SecretStoreMigrationsHistory</c>); does NOT touch
    /// ControlPlaneDbContext / TenantDbContext.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSecretTenantScopedUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_secrets_Scope_Name",
                table: "secrets");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Scope_TenantId_Name",
                table: "secrets",
                columns: new[] { "Scope", "TenantId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_secrets_Scope_TenantId_Name",
                table: "secrets");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Scope_Name",
                table: "secrets",
                columns: new[] { "Scope", "Name" },
                unique: true);
        }
    }
}
