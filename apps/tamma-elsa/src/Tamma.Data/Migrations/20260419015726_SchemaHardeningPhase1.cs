using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchemaHardeningPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_health_ProviderKey_TenantId",
                table: "provider_health");

            migrationBuilder.DropIndex(
                name: "IX_agent_configs_TenantId",
                table: "agent_configs");

            migrationBuilder.AlterColumn<long>(
                name: "GitHubId",
                table: "users",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Settings",
                table: "users",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "AgentType",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "provider_diagnostics",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngineId",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "provider_diagnostics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "provider_diagnostics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProjectId",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskId",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskType",
                table: "provider_diagnostics",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "prompt_overrides",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "prompt_overrides",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "prompt_overrides",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AlterColumn<long>(
                name: "AppId",
                table: "github_installations",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "github_installation_repos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "github_installation_repos",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "github_installation_repos",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "github_installation_repos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_TenantId_DefinitionId",
                table: "workflow_instances",
                columns: new[] { "TenantId", "DefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_TenantId_Status",
                table: "workflow_instances",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sanitization_rules_TenantId",
                table: "sanitization_rules",
                column: "TenantId",
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_ProviderKey_TenantId",
                table: "provider_health",
                columns: new[] { "ProviderKey", "TenantId" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_CorrelationId",
                table: "provider_diagnostics",
                column: "CorrelationId",
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_EngineId_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "EngineId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_Model_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "Model", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_RequestType_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "RequestType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_TenantId_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_AccountLogin",
                table: "github_installations",
                column: "AccountLogin");

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_TenantId",
                table: "github_installations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_github_installation_repos_RepoFullName",
                table: "github_installation_repos",
                column: "RepoFullName");

            migrationBuilder.CreateIndex(
                name: "IX_domain_events_TenantId_IssueNumber",
                table: "domain_events",
                columns: new[] { "TenantId", "IssueNumber" },
                filter: "\"IssueNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_TenantId",
                table: "agent_configs",
                column: "TenantId",
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_agent_configs_tenants_TenantId",
                table: "agent_configs",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_sanitization_rules_tenants_TenantId",
                table: "sanitization_rules",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // ─── Raw-SQL hardening (CHECK constraints + partial indexes) ────────
            // EF Core cannot model these directly, so they are issued as raw
            // SQL. They restore the safety net the TS migrations had at
            // database/archived-sql-migrations/008_…, 010_…, 012_…, 013_…,
            // 014_…, 015_…, 016_…, 017_…, 018_user_auth_fields.sql.
            //
            // FK additions are restricted to agent_configs and sanitization_rules
            // (small write surface, easy to seed in tests). FKs on
            // api_keys / github_installations / user_invites.invited_by /
            // domain_events / workflow_instances are NOT added at this stage —
            // the C# port's webhook firehose + personal-tenant middleware
            // produces orphan rows in normal operation. Tenant isolation is
            // currently enforced by EF query filters and will be hardened by
            // RLS in a follow-up phase (findings 020-022). The audit's
            // remediation notes for findings 016, 017, 019, 026, 029 record
            // this divergence.

            // ── CHECK constraints ──────────────────────────────────────────────
            // tenants.plan ∈ {free, pro, enterprise} (TS migration 008).
            migrationBuilder.Sql(@"
                ALTER TABLE tenants
                  ADD CONSTRAINT ck_tenants_plan
                  CHECK (""Plan"" IN ('free', 'pro', 'enterprise'));");

            // tenant_memberships.role ∈ {owner, admin, member} (TS migration 017).
            migrationBuilder.Sql(@"
                ALTER TABLE tenant_memberships
                  ADD CONSTRAINT ck_tenant_memberships_role
                  CHECK (""Role"" IN ('owner', 'admin', 'member'));");

            // user_invites.role ∈ {owner, admin, member} (TS migration 006).
            migrationBuilder.Sql(@"
                ALTER TABLE user_invites
                  ADD CONSTRAINT ck_user_invites_role
                  CHECK (""Role"" IN ('owner', 'admin', 'member'));");

            // users.role + users.auth_method (TS migrations 002 + 018).
            migrationBuilder.Sql(@"
                ALTER TABLE users
                  ADD CONSTRAINT ck_users_role
                  CHECK (""Role"" IN ('owner', 'admin', 'member'));");
            migrationBuilder.Sql(@"
                ALTER TABLE users
                  ADD CONSTRAINT ck_users_auth_method
                  CHECK (""AuthMethod"" IN ('email', 'github', 'both'));");

            // api_keys.scope ∈ {user, installation, service} (TS migration 009).
            migrationBuilder.Sql(@"
                ALTER TABLE api_keys
                  ADD CONSTRAINT ck_api_keys_scope
                  CHECK (""Scope"" IN ('user', 'installation', 'service'));");

            // github_installations.account_type ∈ {User, Organization} (TS 001).
            migrationBuilder.Sql(@"
                ALTER TABLE github_installations
                  ADD CONSTRAINT ck_github_installations_account_type
                  CHECK (""AccountType"" IN ('User', 'Organization'));");

            // prompt_overrides.max_tokens > 0 + version > 0 (TS migration 012).
            migrationBuilder.Sql(@"
                ALTER TABLE prompt_overrides
                  ADD CONSTRAINT ck_prompt_overrides_max_tokens_positive
                  CHECK (""MaxTokens"" > 0);");
            migrationBuilder.Sql(@"
                ALTER TABLE prompt_overrides
                  ADD CONSTRAINT ck_prompt_overrides_version_positive
                  CHECK (""Version"" > 0);");

            // provider_health.status — C# uses {healthy, degraded, down, unknown}
            // (see ProviderHealth.Status xmldoc + CircuitBreakerService writes).
            // TS used a boolean circuit_open + half_open_in_progress instead.
            // CHECK pins the C# vocabulary to prevent typos.
            migrationBuilder.Sql(@"
                ALTER TABLE provider_health
                  ADD CONSTRAINT ck_provider_health_status
                  CHECK (""Status"" IN ('healthy', 'degraded', 'down', 'unknown'));");

            // ── Partial indexes ───────────────────────────────────────────────
            // Active refresh-token reaper hot path (TS migration 018).
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_refresh_tokens_active_expires
                  ON refresh_tokens (""ExpiresAt"")
                  WHERE ""RevokedAt"" IS NULL;");

            // Active password-reset reaper / validation (TS migration 018).
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_password_reset_tokens_active_expires
                  ON password_reset_tokens (""ExpiresAt"")
                  WHERE ""ConsumedAt"" IS NULL;");

            // Case-insensitive unique email (TS migration 018). Unique partial
            // on LOWER(email) so ""Alice@x"" and ""alice@x"" collide and the
            // login query LOWER(email) hits an index.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email_lower
                  ON users (LOWER(""Email""))
                  WHERE ""DeletedAt"" IS NULL;");

            // Soft-delete partial on tenants (TS migration 008).
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_tenants_deleted_at
                  ON tenants (""DeletedAt"")
                  WHERE ""DeletedAt"" IS NULL;");

            // Active-only api-keys lookup (TS migration 009). Filter on the
            // hot-path WHERE revoked_at IS NULL clause.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_api_keys_active
                  ON api_keys (""Scope"")
                  WHERE ""RevokedAt"" IS NULL;");

            // Provider budget partial — ""successful spend this month"" hot path
            // (TS migration 014 idx_diagnostics_budget).
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_provider_diagnostics_budget
                  ON provider_diagnostics (""TenantId"", ""CreatedAt"")
                  WHERE ""Success"" = true;");

            // Open-circuit lookup (TS migration 015 idx_provider_health_open).
            // ""down"" is the C# port's equivalent of TS's circuit_open=true.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_provider_health_open
                  ON provider_health (""ProviderKey"")
                  WHERE ""Status"" = 'down';");

            // Agent-configs system-default partial unique. The unique on
            // ""TenantId"" WHERE NOT NULL was added by EF above; this companion
            // partial enforces ""at most one row with TenantId IS NULL""
            // (TS migration 013).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ix_agent_configs_system_default
                  ON agent_configs ((1))
                  WHERE ""TenantId"" IS NULL;");

            // Provider-health system-default partial unique (one row per
            // ProviderKey when no tenant is bound).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ix_provider_health_system_default
                  ON provider_health (""ProviderKey"")
                  WHERE ""TenantId"" IS NULL;");

            // Sanitization-rules system-default partial unique.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ix_sanitization_rules_system_default
                  ON sanitization_rules ((1))
                  WHERE ""TenantId"" IS NULL;");

            // ── Self-FK on api_keys.RotatedFromId (TS migration 009) ──────────
            migrationBuilder.Sql(@"
                ALTER TABLE api_keys
                  ADD CONSTRAINT fk_api_keys_rotated_from
                  FOREIGN KEY (""RotatedFromId"") REFERENCES api_keys(""Id"")
                  ON DELETE SET NULL;");

            // ── github_installation_repos: backfill Owner/Name from RepoFullName ──
            // EF added the columns with default '' to satisfy NOT NULL. Backfill
            // any existing rows by splitting on '/'. Empty Owner/Name remain
            // for rows whose RepoFullName lacks a slash (defensive — should be 0).
            migrationBuilder.Sql(@"
                UPDATE github_installation_repos
                SET ""Owner"" = SPLIT_PART(""RepoFullName"", '/', 1),
                    ""Name""  = SPLIT_PART(""RepoFullName"", '/', 2)
                WHERE (""Owner"" = '' OR ""Name"" = '')
                  AND POSITION('/' IN ""RepoFullName"") > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the raw-SQL hardening before EF-managed objects.
            migrationBuilder.Sql(@"ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS fk_api_keys_rotated_from;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_sanitization_rules_system_default;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_provider_health_system_default;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_agent_configs_system_default;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_provider_health_open;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_provider_diagnostics_budget;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_api_keys_active;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_tenants_deleted_at;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_users_email_lower;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_password_reset_tokens_active_expires;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_refresh_tokens_active_expires;");
            migrationBuilder.Sql(@"ALTER TABLE provider_health DROP CONSTRAINT IF EXISTS ck_provider_health_status;");
            migrationBuilder.Sql(@"ALTER TABLE prompt_overrides DROP CONSTRAINT IF EXISTS ck_prompt_overrides_version_positive;");
            migrationBuilder.Sql(@"ALTER TABLE prompt_overrides DROP CONSTRAINT IF EXISTS ck_prompt_overrides_max_tokens_positive;");
            migrationBuilder.Sql(@"ALTER TABLE github_installations DROP CONSTRAINT IF EXISTS ck_github_installations_account_type;");
            migrationBuilder.Sql(@"ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS ck_api_keys_scope;");
            migrationBuilder.Sql(@"ALTER TABLE users DROP CONSTRAINT IF EXISTS ck_users_auth_method;");
            migrationBuilder.Sql(@"ALTER TABLE users DROP CONSTRAINT IF EXISTS ck_users_role;");
            migrationBuilder.Sql(@"ALTER TABLE user_invites DROP CONSTRAINT IF EXISTS ck_user_invites_role;");
            migrationBuilder.Sql(@"ALTER TABLE tenant_memberships DROP CONSTRAINT IF EXISTS ck_tenant_memberships_role;");
            migrationBuilder.Sql(@"ALTER TABLE tenants DROP CONSTRAINT IF EXISTS ck_tenants_plan;");

            migrationBuilder.DropForeignKey(
                name: "FK_agent_configs_tenants_TenantId",
                table: "agent_configs");

            migrationBuilder.DropForeignKey(
                name: "FK_sanitization_rules_tenants_TenantId",
                table: "sanitization_rules");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_TenantId_DefinitionId",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_TenantId_Status",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_sanitization_rules_TenantId",
                table: "sanitization_rules");

            migrationBuilder.DropIndex(
                name: "IX_provider_health_ProviderKey_TenantId",
                table: "provider_health");

            migrationBuilder.DropIndex(
                name: "IX_provider_diagnostics_CorrelationId",
                table: "provider_diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_provider_diagnostics_EngineId_CreatedAt",
                table: "provider_diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_provider_diagnostics_Model_CreatedAt",
                table: "provider_diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_provider_diagnostics_RequestType_CreatedAt",
                table: "provider_diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_provider_diagnostics_TenantId_CreatedAt",
                table: "provider_diagnostics");

            migrationBuilder.DropIndex(
                name: "IX_github_installations_AccountLogin",
                table: "github_installations");

            migrationBuilder.DropIndex(
                name: "IX_github_installations_TenantId",
                table: "github_installations");

            migrationBuilder.DropIndex(
                name: "IX_github_installation_repos_RepoFullName",
                table: "github_installation_repos");

            migrationBuilder.DropIndex(
                name: "IX_domain_events_TenantId_IssueNumber",
                table: "domain_events");

            migrationBuilder.DropIndex(
                name: "IX_agent_configs_TenantId",
                table: "agent_configs");

            migrationBuilder.DropColumn(
                name: "Settings",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AgentType",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "EngineId",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "TaskId",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "TaskType",
                table: "provider_diagnostics");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "prompt_overrides");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "prompt_overrides");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "prompt_overrides");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "github_installation_repos");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "github_installation_repos");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "github_installation_repos");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "github_installation_repos");

            migrationBuilder.AlterColumn<int>(
                name: "GitHubId",
                table: "users",
                type: "integer",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "AppId",
                table: "github_installations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_ProviderKey_TenantId",
                table: "provider_health",
                columns: new[] { "ProviderKey", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_TenantId",
                table: "agent_configs",
                column: "TenantId",
                unique: true);
        }
    }
}
