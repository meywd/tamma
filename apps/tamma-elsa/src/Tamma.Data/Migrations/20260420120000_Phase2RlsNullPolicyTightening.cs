using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <summary>
    /// Phase-2 follow-up (review-session 2026-04-20 finding 2): tightens the
    /// RLS policies installed by <c>20260419021119_Phase2RlsAndTriggers</c>
    /// to close a NULL-tenant leak.
    ///
    /// <para>
    /// The original <c>tenant_isolation_policy</c> on standard tenant-
    /// scoped tables used the shape
    /// <code>"TenantId" IS NULL OR "TenantId" = NULLIF(current_setting(...), '')::uuid</code>
    /// so rows that legitimately have <c>TenantId = NULL</c> (system defaults,
    /// service keys, bootstrap rows) remained visible to every session.
    /// Once Phase-3 wires <c>TammaAppDbContext</c> onto the runtime hot path
    /// (follow-up story 19-6), the <c>IS NULL</c> branch would expose those
    /// rows across every tenant session — a cross-tenant data leak.
    /// </para>
    ///
    /// <para>
    /// This migration drops the <c>IS NULL</c> branch from <b>strictly
    /// tenant-scoped</b> tables and keeps it on the handful of tables that
    /// have legitimate platform-global NULL-tenant rows.
    /// </para>
    ///
    /// <para>
    /// <b>Strict tenant-scoped (drop NULL branch)</b>: a NULL <c>TenantId</c>
    /// on these tables is always a bug or a transient pre-resolution state
    /// that must not be readable by any app-role session:
    /// <list type="bullet">
    ///   <item><c>users</c> — NULL means registration-in-flight before
    ///   <c>EnsurePersonalTenantMiddleware</c>. Gated behind the admin role
    ///   permanently: the app-role policy now requires a non-NULL match.</item>
    ///   <item><c>github_installations</c> — every installation is owned by
    ///   exactly one tenant once provisioning completes.</item>
    ///   <item><c>user_invites</c> — an invite is scoped to the inviting
    ///   tenant.</item>
    ///   <item><c>domain_events</c> — audit events must not leak across
    ///   tenants. Background services that need cross-tenant aggregation
    ///   use the admin role explicitly.</item>
    ///   <item><c>workflow_instances</c> — every instance has an owning
    ///   tenant.</item>
    ///   <item><c>provider_diagnostics</c>, <c>provider_health</c> — per-
    ///   tenant telemetry.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Platform-global NULL allowed (keep NULL branch)</b>: these tables
    /// ship with system-default rows that are intentionally shared across
    /// tenants, so the <c>IS NULL</c> branch remains:
    /// <list type="bullet">
    ///   <item><c>prompt_overrides</c> — NULL = system-default prompt; a
    ///   user override is a per-tenant row.</item>
    ///   <item><c>agent_configs</c> — NULL = system-default agent config.</item>
    ///   <item><c>sanitization_rules</c> — NULL = platform-wide rule.</item>
    ///   <item><c>workflow_definitions</c> — NULL = Tamma-shipped workflow
    ///   (tenant-custom workflows are a follow-up).</item>
    /// </list>
    /// <c>api_keys</c> keeps its existing policy with the
    /// <c>Scope = 'service'</c> disjunction — that's the canonical way to
    /// flag a platform-wide service credential, and it already handles the
    /// cross-tenant cases the review called out.
    /// </para>
    ///
    /// <para>
    /// The admin (superuser) role still bypasses every policy — it uses
    /// <c>.IgnoreQueryFilters()</c> + the superuser bypass for the handful
    /// of legitimately-cross-tenant paths (migrations, task queue processor,
    /// outbox sender, workflow sync, <c>EnsurePersonalTenantMiddleware</c>).
    /// </para>
    ///
    /// <para>
    /// References: review session `docs/review/session-2026-04-20.md` §2.1
    /// Finding 2; Phase-2 origin migration `20260419021119_Phase2RlsAndTriggers`.
    /// </para>
    /// </summary>
    public partial class Phase2RlsNullPolicyTightening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Strict tenant-scoped: drop the IS NULL branch ─────────────
            // The replacement policy treats NULL TenantId as "no row matches"
            // (NULL = anything is NULL → falsey in WHERE) so registration-
            // in-flight rows + any future buggy INSERTs are invisible to the
            // app-role plane.
            string[] strictTables =
            {
                "users",
                "github_installations",
                "user_invites",
                "domain_events",
                "workflow_instances",
                "provider_diagnostics",
                "provider_health",
            };

            foreach (var table in strictTables)
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS tenant_isolation_policy ON {table};
                    CREATE POLICY tenant_isolation_policy ON {table}
                      USING (
                        ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      )
                      WITH CHECK (
                        ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      );");
            }

            // ── Platform-global NULL allowed: policy unchanged ────────────
            // These tables keep the IS NULL disjunction because a NULL
            // TenantId row is a deliberate "ships with Tamma / shared
            // rule" marker. Re-assert the policy verbatim so the migration
            // is idempotent + the intent is visible in the migration
            // history.
            string[] platformGlobalTables =
            {
                "prompt_overrides",
                "agent_configs",
                "sanitization_rules",
                "workflow_definitions",
            };

            foreach (var table in platformGlobalTables)
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS tenant_isolation_policy ON {table};
                    CREATE POLICY tenant_isolation_policy ON {table}
                      USING (
                        ""TenantId"" IS NULL
                        OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      )
                      WITH CHECK (
                        ""TenantId"" IS NULL
                        OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      );");
            }

            // api_keys is intentionally left alone — its existing policy
            // (Scope = 'service' OR TenantId IS NULL OR tenant match) is
            // the canonical platform-credential pattern called out at lines
            // 206-220 of the Phase-2 migration. The review specifically
            // said "service-scoped API keys (already handled ... leave alone)".
            // tenants + tenant_memberships + github_installation_repos are
            // also unchanged — their policies don't use the IS NULL
            // disjunction at all (tenants uses Id match, memberships + repos
            // use joins to parent rows).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the permissive IS NULL-allowing policy on strict
            // tables. This is the Phase-2 shape prior to this migration.
            string[] strictTables =
            {
                "users",
                "github_installations",
                "user_invites",
                "domain_events",
                "workflow_instances",
                "provider_diagnostics",
                "provider_health",
            };

            foreach (var table in strictTables)
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS tenant_isolation_policy ON {table};
                    CREATE POLICY tenant_isolation_policy ON {table}
                      USING (
                        ""TenantId"" IS NULL
                        OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      )
                      WITH CHECK (
                        ""TenantId"" IS NULL
                        OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                      );");
            }

            // Platform-global tables are a no-op on Down — we asserted the
            // same policy shape in Up, so a Down does not need to change them.
        }
    }
}
