using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 41-30 — the tenant-aware scheduled-trigger seam's two
    /// control-plane tables: <c>scheduled_triggers</c> (schedule registry,
    /// D1) and <c>scheduled_trigger_fires</c> (the durable at-most-once fire
    /// ledger, D2).
    ///
    /// <para><b>Why raw idempotent SQL instead of
    /// <c>migrationBuilder.CreateTable</c></b> (the <c>AddProviderSettings</c>
    /// precedent): the Epic 19 startup wipe in <c>Tamma.Api/Program.cs</c>
    /// drops every Tamma-managed CP table AND the migrations-history table on
    /// each deploy (unless <c>TAMMA_PRESERVE_DB=1</c>), then re-runs the whole
    /// migration graph. BOTH schedule tables are deliberately EXCLUDED from
    /// that DROP list (AC7 — a deploy must not silently disable every
    /// tenant's audits, nor erase the ledger that makes fires at-most-once
    /// across restarts). So this migration re-runs against a database where
    /// the tables already exist and its DDL must be
    /// <c>IF NOT EXISTS</c>-idempotent or every second deploy dies with
    /// SqlState 42P07. The schema below is equivalent to what
    /// <c>TammaModelConfiguration.ConfigureScheduledTriggerEntities</c>
    /// describes (the snapshot/model stays authoritative for EF).</para>
    ///
    /// <para><b>No FK to <c>tenants</c></b> — tenants ARE wiped, and a
    /// cascade would take the surviving schedule rows with them
    /// (the <c>provider_settings</c> rationale). The intra-seam FK
    /// (fires → triggers, CASCADE) is safe: both tables share the
    /// exclusion and survive together.</para>
    /// </summary>
    public partial class AddScheduledTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS scheduled_triggers (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "TenantId" uuid NULL,
                    "DefinitionId" character varying(200) NOT NULL,
                    "Name" character varying(200) NOT NULL,
                    "CronExpression" character varying(100) NOT NULL,
                    "Enabled" boolean NOT NULL DEFAULT TRUE,
                    "InputJson" jsonb NOT NULL DEFAULT '{}'::jsonb,
                    "NextDueAt" timestamp with time zone NULL,
                    "LastWindowKey" character varying(64) NULL,
                    "LastFiredAt" timestamp with time zone NULL,
                    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    "CreatedBy" uuid NULL,
                    CONSTRAINT "PK_scheduled_triggers" PRIMARY KEY ("Id"),
                    CONSTRAINT ck_scheduled_triggers_definition_id CHECK (length("DefinitionId") > 0),
                    CONSTRAINT ck_scheduled_triggers_name CHECK (length("Name") > 0),
                    CONSTRAINT ck_scheduled_triggers_cron CHECK (length("CronExpression") > 0)
                );

                -- The natural key: one row per (tenant, definition, name).
                -- NULLS NOT DISTINCT so at most ONE platform template
                -- (TenantId NULL) exists per (definition, name) — D1.
                CREATE UNIQUE INDEX IF NOT EXISTS "ux_scheduled_triggers_tenant_definition_name"
                    ON scheduled_triggers ("TenantId", "DefinitionId", "Name")
                    NULLS NOT DISTINCT;

                CREATE INDEX IF NOT EXISTS "IX_scheduled_triggers_Enabled_NextDueAt"
                    ON scheduled_triggers ("Enabled", "NextDueAt");

                CREATE TABLE IF NOT EXISTS scheduled_trigger_fires (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "TriggerId" uuid NOT NULL,
                    "TenantId" uuid NOT NULL,
                    "DefinitionId" character varying(200) NOT NULL,
                    "WindowKey" character varying(64) NOT NULL,
                    "ClaimedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    "DispatchedAt" timestamp with time zone NULL,
                    "WorkflowInstanceId" text NULL,
                    "Outcome" character varying(16) NOT NULL DEFAULT 'claimed',
                    "Detail" text NULL,
                    CONSTRAINT "PK_scheduled_trigger_fires" PRIMARY KEY ("Id"),
                    CONSTRAINT ck_scheduled_trigger_fires_outcome CHECK (
                        "Outcome" IN ('claimed','dispatched','failed')),
                    CONSTRAINT "FK_scheduled_trigger_fires_scheduled_triggers_TriggerId"
                        FOREIGN KEY ("TriggerId") REFERENCES scheduled_triggers ("Id")
                        ON DELETE CASCADE
                );

                -- THE at-most-once invariant (D2): the INSERT … ON CONFLICT
                -- DO NOTHING claim is arbitrated by this unique index.
                CREATE UNIQUE INDEX IF NOT EXISTS "ux_scheduled_trigger_fires_trigger_window"
                    ON scheduled_trigger_fires ("TriggerId", "WindowKey");

                CREATE INDEX IF NOT EXISTS "IX_scheduled_trigger_fires_ClaimedAt"
                    ON scheduled_trigger_fires ("ClaimedAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS scheduled_trigger_fires;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS scheduled_triggers;");
        }
    }
}
