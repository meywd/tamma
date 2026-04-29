using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <summary>
    /// Story 27-2 — make prompt_overrides dual-scoped.
    ///
    /// <para>Single-user-mode rows are keyed on <c>user_id</c>
    /// (<c>tenant_id IS NULL</c>); SaaS-mode rows are keyed on <c>tenant_id</c>
    /// (<c>user_id IS NULL</c>). The two row spaces co-exist on the same
    /// table — exactly which key is set is a per-row property of the calling
    /// principal at write time.</para>
    ///
    /// <para>Two schema changes:</para>
    ///
    /// <list type="number">
    ///   <item><c>ck_prompt_overrides_principal_xor</c> — CHECK that
    ///     exactly one of <c>user_id</c> / <c>tenant_id</c> is non-null.
    ///     Rejects rows that set both keys (would be ambiguous resolution)
    ///     OR neither key (would be a system default — those live in code,
    ///     never in the table).</item>
    ///   <item>Replace the legacy <c>(UserId, Scope, Role, Action)</c> unique
    ///     index with <c>(UserId, TenantId, Scope, Role, Action)</c> using
    ///     <c>NULLS NOT DISTINCT</c>. NULLS NOT DISTINCT is required so a
    ///     SaaS row <c>(null, T1, "role-action", "developer", "plan")</c> is
    ///     unique against another SaaS row with the same shape — Postgres'
    ///     default <c>NULLS DISTINCT</c> would let any number of those
    ///     coexist. Requires Postgres ≥ 15 (production runs PG17).</item>
    /// </list>
    ///
    /// <para>The <c>tenant_id UUID NULL</c> column itself was added in
    /// migration <c>20260428024426_AddMovedEntitiesToTenantSchema</c> when
    /// the table moved off the control plane — only the constraints are new.
    /// </para>
    /// </summary>
    public partial class Story27_2_PromptOverridesPrincipalXor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Drop the legacy single-key (UserId, Scope, Role, Action)
            //    unique index — replaced below by the dual-key variant.
            migrationBuilder.DropIndex(
                name: "IX_prompt_overrides_UserId_Scope_Role_Action",
                table: "prompt_overrides");

            // 2. principal_xor CHECK — exactly one of user_id / tenant_id
            //    is non-null. The default migrationBuilder.AddCheckConstraint
            //    emits ALTER TABLE ... ADD CONSTRAINT ... CHECK (...) which
            //    Postgres validates on insert + update.
            migrationBuilder.AddCheckConstraint(
                name: "ck_prompt_overrides_principal_xor",
                table: "prompt_overrides",
                sql: "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) "
                   + "OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");

            // 3. Dual-key unique index with NULLS NOT DISTINCT.
            //    Raw SQL because EF Core 8.0 doesn't expose the
            //    NULLS NOT DISTINCT option on CreateIndex (added in EF 9 +
            //    Npgsql provider AreNullsDistinct). The semantics here are
            //    critical — a SaaS row's UserId is always NULL; without
            //    NULLS NOT DISTINCT every SaaS row would be considered
            //    distinct from every other SaaS row regardless of TenantId.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_prompt_overrides_UserId_TenantId_Scope_Role_Action\" "
              + "ON prompt_overrides (\"UserId\", \"TenantId\", \"Scope\", \"Role\", \"Action\") "
              + "NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_prompt_overrides_UserId_TenantId_Scope_Role_Action\";");

            migrationBuilder.DropCheckConstraint(
                name: "ck_prompt_overrides_principal_xor",
                table: "prompt_overrides");

            migrationBuilder.CreateIndex(
                name: "IX_prompt_overrides_UserId_Scope_Role_Action",
                table: "prompt_overrides",
                columns: new[] { "UserId", "Scope", "Role", "Action" },
                unique: true);
        }
    }
}
