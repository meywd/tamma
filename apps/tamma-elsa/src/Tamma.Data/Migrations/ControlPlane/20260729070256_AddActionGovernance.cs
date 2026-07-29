using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 43-5 — <c>action_assignments</c> (per-principal autonomy policy,
    /// three scopes: platform ceiling / tenant / user) and
    /// <c>action_authorizations</c> (the one-human-decision-per-run ledger).
    ///
    /// <para><b>Why raw idempotent SQL instead of
    /// <c>migrationBuilder.CreateTable</c> (the <c>provider_settings</c> /
    /// <c>scheduled_triggers</c> precedent):</b> the Epic 19 startup wipe in
    /// <c>Tamma.Api/Program.cs</c> drops every Tamma-managed CP table AND the
    /// <c>__ControlPlaneMigrationsHistory</c> table on each deploy (unless
    /// <c>TAMMA_PRESERVE_DB=1</c>), then re-runs the whole migration graph.
    /// Both tables here are deliberately EXCLUDED from that DROP list — they
    /// are safety policy, the only thing between an agent and a production
    /// deploy, and a wipe that silently reverted every admin tightening would
    /// be a governance surface that lies (43-5 AC5/D3). That means this
    /// migration re-runs against a database where the tables already exist, so
    /// its DDL must be <c>IF NOT EXISTS</c>-idempotent or every second deploy
    /// dies with SqlState 42P07. The schema below is equivalent to what
    /// <c>TammaModelConfiguration.ConfigureActionGovernanceEntities</c>
    /// describes (the snapshot/model stays authoritative for EF).</para>
    ///
    /// <para><b>No FK to <c>tenants</c>/<c>users</c></b> — those ARE wiped,
    /// and a CASCADE would take the surviving policy rows with them.</para>
    ///
    /// <para><b>No CHECK on <c>MinAutonomy</c>'s VALUE</b> — deliberate
    /// (43-5 AC3/D5): a numeric CHECK frozen into this file would be a second
    /// permanent hardcoding of the <c>AutonomyDial</c> bound. Validation is
    /// domain-side. Pinned by
    /// <c>ActionGovernanceResidencyTests.Migration_HasNoNumericConstraintOnMinAutonomy</c>.</para>
    /// </summary>
    public partial class AddActionGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS action_assignments (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "TenantId" uuid NULL,
                    "UserId" uuid NULL,
                    "TargetKind" character varying(16) NOT NULL,
                    "TargetKey" character varying(200) NOT NULL,
                    "MinAutonomy" integer NULL,
                    "Enforce" boolean NULL,
                    "Enabled" boolean NULL,
                    "AllowedRoles" text[] NULL,
                    "Note" character varying(500) NULL,
                    "Version" integer NOT NULL DEFAULT 1,
                    "CreatedBy" uuid NULL,
                    "UpdatedBy" uuid NULL,
                    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT "PK_action_assignments" PRIMARY KEY ("Id"),
                    CONSTRAINT ck_action_assignments_principal_scope CHECK (
                        NOT ("TenantId" IS NOT NULL AND "UserId" IS NOT NULL)),
                    CONSTRAINT ck_action_assignments_target_kind CHECK (
                        "TargetKind" IN ('action','group','mode')),
                    CONSTRAINT ck_action_assignments_mode_row CHECK (
                        ("TargetKind" = 'mode') = ("MinAutonomy" IS NULL))
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_action_assignments_principal_target
                    ON action_assignments ("TenantId", "UserId", "TargetKind", "TargetKey")
                    NULLS NOT DISTINCT;

                CREATE TABLE IF NOT EXISTS action_authorizations (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "TenantId" uuid NULL,
                    "UserId" uuid NULL,
                    "CorrelationId" character varying(200) NOT NULL,
                    "TargetKind" character varying(16) NOT NULL,
                    "TargetKey" character varying(200) NOT NULL,
                    "State" character varying(16) NOT NULL DEFAULT 'pending',
                    "RequestedAtUtc" timestamp with time zone NOT NULL DEFAULT now(),
                    "DecidedAtUtc" timestamp with time zone NULL,
                    "DecidedByUserId" uuid NULL,
                    "ExpiresAtUtc" timestamp with time zone NULL,
                    "ConsumedAtUtc" timestamp with time zone NULL,
                    "Reason" character varying(1000) NULL,
                    "AutonomyLevelAtRequest" integer NULL,
                    CONSTRAINT "PK_action_authorizations" PRIMARY KEY ("Id"),
                    CONSTRAINT ck_action_authorizations_principal_scope CHECK (
                        NOT ("TenantId" IS NOT NULL AND "UserId" IS NOT NULL)),
                    CONSTRAINT ck_action_authorizations_state CHECK (
                        "State" IN ('pending','granted','denied','expired')),
                    CONSTRAINT ck_action_authorizations_target_kind CHECK (
                        "TargetKind" IN ('action','group'))
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_action_authorizations_open
                    ON action_authorizations ("TenantId", "UserId", "CorrelationId", "TargetKind", "TargetKey")
                    NULLS NOT DISTINCT
                    WHERE "State" IN ('pending','granted');

                CREATE INDEX IF NOT EXISTS "IX_action_authorizations_Correlation_State"
                    ON action_authorizations ("CorrelationId", "State");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS action_authorizations;
                DROP TABLE IF EXISTS action_assignments;
                """);
        }
    }
}
