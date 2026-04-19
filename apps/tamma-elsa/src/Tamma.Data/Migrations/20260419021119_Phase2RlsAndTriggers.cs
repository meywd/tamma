using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <summary>
    /// Phase-2 hardening: defense-in-depth tenant isolation. Restores three
    /// load-bearing TS migration artefacts (010_rls_tenant_isolation.sql,
    /// 011_tenant_scoped_stores.sql) that the C# port shed:
    ///
    ///   1. <c>prevent_tenant_id_change()</c> trigger function + 6 BEFORE-UPDATE
    ///      triggers — raises if any tenant_id is mutated on UPDATE. Independent
    ///      of RLS; takes effect immediately for any role.
    ///
    ///   2. <c>tamma_app</c> role created (idempotent), granted minimal CRUD.
    ///      Not used by the runtime today — the application still connects as
    ///      the privileged role. Pre-positioned for the connection-string split
    ///      that completes finding 021.
    ///
    ///   3. RLS policies (<c>ENABLE</c> + <c>FORCE</c>) on the eight TS-tagged
    ///      tables. Because the runtime connects as a superuser-equivalent role,
    ///      these policies are dormant — superusers bypass RLS. They take
    ///      effect once <c>tamma_app</c> becomes the runtime role.
    ///
    /// This staged approach lets the schema artifacts ride into production
    /// without risking a hard-cutover outage. Phase-3 will swap the runtime
    /// connection string to <c>tamma_app</c> and turn the dormant policies
    /// into a live safety net.
    ///
    /// References:
    ///   - finding 020 (RLS policies missing)
    ///   - finding 021 (tamma_app role missing)
    ///   - finding 022 (prevent_tenant_id_change trigger missing)
    /// </summary>
    public partial class Phase2RlsAndTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. prevent_tenant_id_change function + triggers (finding 022) ──
            // Raises if any UPDATE attempts to modify TenantId. Applies to
            // every connecting role (no superuser bypass for triggers).
            // The trigger blocks cross-tenant moves on UPDATE. Subtle policy
            // choice for the C# port: rows are commonly created with
            // TenantId = NULL (e.g. during user registration before
            // EnsurePersonalTenantMiddleware materialises the tenant). The
            // first NULL → uuid assignment is permitted. Any non-NULL → other
            // value or non-NULL → NULL transition is blocked. This preserves
            // the audit guarantee (no covert tenant migration of established
            // records) while accommodating the personal-tenant-on-demand
            // bootstrap flow that TS did not have.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION prevent_tenant_id_change()
                RETURNS TRIGGER AS $$
                BEGIN
                  IF OLD.""TenantId"" IS NOT NULL
                     AND OLD.""TenantId"" IS DISTINCT FROM NEW.""TenantId"" THEN
                    RAISE EXCEPTION 'Cannot change TenantId on existing row';
                  END IF;
                  RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;");

            // Install BEFORE UPDATE row triggers on every tenant-scoped table
            // that has a TenantId column. These cover the eight tables the TS
            // migration covered, mapped to their C# names.
            string[] tenantScopedTables =
            {
                "users",
                "github_installations",
                "api_keys",
                "user_invites",
                "domain_events",
                "workflow_instances",
                "agent_configs",
                "provider_diagnostics",
                "provider_health",
                "sanitization_rules",
                "prompt_overrides",
            };

            foreach (var table in tenantScopedTables)
            {
                migrationBuilder.Sql($@"
                    DROP TRIGGER IF EXISTS trg_prevent_tenant_change_{table} ON {table};
                    CREATE TRIGGER trg_prevent_tenant_change_{table}
                      BEFORE UPDATE ON {table}
                      FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();");
            }

            // ── 2. tamma_app role (finding 021) ────────────────────────────────
            // Idempotent CREATE — uses pg_roles probe so re-runs against an
            // existing DB are safe. Password is a placeholder; production
            // deploys must override via ALTER ROLE before the connection
            // string is switched.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
                    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
                  END IF;
                END $$;");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                  EXECUTE format('GRANT CONNECT ON DATABASE %I TO tamma_app', current_database());
                END $$;");

            migrationBuilder.Sql(@"
                GRANT USAGE ON SCHEMA public TO tamma_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT USAGE, SELECT ON SEQUENCES TO tamma_app;");

            // ── 3. RLS policies (finding 020) ─────────────────────────────────
            // The application uses `current_setting('app.current_tenant_id',
            // true)::uuid` which is set per-request by the TenantContext
            // middleware. true = "missing key returns NULL" rather than
            // raising, so policies short-circuit cleanly when no tenant is
            // bound. Policies are FORCE-d so the table owner cannot bypass.
            //
            // For tenants table, identity is `id` (own row). For all other
            // scoped tables, identity is `tenant_id`. Special-case for
            // api_keys: service-scope rows have tenant_id NULL by design
            // (cross-tenant platform credentials) and the policy permits
            // access to those when the session has no tenant set OR when
            // scope = 'service'.

            // tenants: see-your-own-row by id
            EnableRls(migrationBuilder, "tenants");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation_policy ON tenants
                  USING (""Id"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                  WITH CHECK (""Id"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);");

            // tenant_memberships: see your tenant's memberships
            EnableRls(migrationBuilder, "tenant_memberships");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation_policy ON tenant_memberships
                  USING (""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                  WITH CHECK (""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);");

            // Standard tenant_id-scoped tables (uniform policy shape)
            string[] standardScopedTables =
            {
                "users",
                "github_installations",
                "github_installation_repos", // joins via installation; permissive policy
                "user_invites",
                "domain_events",
                "workflow_instances",
                "workflow_definitions",
                "agent_configs",
                "provider_diagnostics",
                "provider_health",
                "sanitization_rules",
                "prompt_overrides",
            };

            foreach (var table in standardScopedTables)
            {
                EnableRls(migrationBuilder, table);

                // Policy uses NULLIF + ::uuid cast so empty/missing setting
                // cleanly returns NULL (no rows match — fail-closed). Rows
                // with TenantId IS NULL (system defaults, service keys) are
                // permitted globally so platform-level data still works.
                if (table == "github_installation_repos")
                {
                    // Special: this table has no TenantId column. Policy
                    // permits access if the parent installation matches.
                    migrationBuilder.Sql(@"
                        CREATE POLICY tenant_isolation_policy ON github_installation_repos
                          USING (
                            EXISTS (
                              SELECT 1 FROM github_installations gi
                              WHERE gi.""Id"" = github_installation_repos.""InstallationEntityId""
                                AND (
                                  gi.""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                                  OR gi.""TenantId"" IS NULL
                                )
                            )
                          );");
                }
                else
                {
                    migrationBuilder.Sql($@"
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
            }

            // api_keys: scope = 'service' rows are platform credentials and
            // must be visible cross-tenant; user / installation rows respect
            // tenant_id. Combined into one policy.
            EnableRls(migrationBuilder, "api_keys");
            migrationBuilder.Sql(@"
                CREATE POLICY tenant_isolation_policy ON api_keys
                  USING (
                    ""Scope"" = 'service'
                    OR ""TenantId"" IS NULL
                    OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  )
                  WITH CHECK (
                    ""Scope"" = 'service'
                    OR ""TenantId"" IS NULL
                    OR ""TenantId"" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop policies first (they depend on tables).
            string[] policyTables =
            {
                "tenants", "tenant_memberships",
                "users", "github_installations", "github_installation_repos",
                "user_invites", "api_keys",
                "domain_events", "workflow_instances", "workflow_definitions",
                "agent_configs", "provider_diagnostics", "provider_health",
                "sanitization_rules", "prompt_overrides",
            };

            foreach (var table in policyTables)
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation_policy ON {table};");
                migrationBuilder.Sql($@"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            // Triggers
            string[] tenantScopedTables =
            {
                "users", "github_installations", "api_keys", "user_invites",
                "domain_events", "workflow_instances", "agent_configs",
                "provider_diagnostics", "provider_health", "sanitization_rules",
                "prompt_overrides",
            };
            foreach (var table in tenantScopedTables)
            {
                migrationBuilder.Sql($@"DROP TRIGGER IF EXISTS trg_prevent_tenant_change_{table} ON {table};");
            }
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS prevent_tenant_id_change();");

            // Role left in place — dropping it would orphan grants. Operators
            // can drop manually with `DROP OWNED BY tamma_app; DROP ROLE tamma_app;`
            // if a full teardown is needed.
        }

        private static void EnableRls(MigrationBuilder mb, string table)
        {
            mb.Sql($@"
                ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation_policy ON {table};");
        }
    }
}
