using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 46-1 — <c>provider_settings</c> (persisted provider model
    /// selection + platform enable flag).
    ///
    /// <para><b>Why raw idempotent SQL instead of
    /// <c>migrationBuilder.CreateTable</c>:</b> the Epic 19 startup wipe in
    /// <c>Tamma.Api/Program.cs</c> drops every Tamma-managed CP table AND the
    /// <c>__ControlPlaneMigrationsHistory</c> table on each deploy (unless
    /// <c>TAMMA_PRESERVE_DB=1</c>), then re-runs the whole migration graph.
    /// <c>provider_settings</c> is deliberately EXCLUDED from that DROP list —
    /// the whole point of the table is that a model picked in the UI survives
    /// redeploys (epic 46). That means this migration re-runs against a
    /// database where the table already exists, so its DDL must be
    /// <c>IF NOT EXISTS</c>-idempotent or every second deploy dies with
    /// SqlState 42P07. The schema below is equivalent to what
    /// <c>TammaModelConfiguration.ConfigureProviderSettings</c> describes
    /// (the snapshot/model stays authoritative for EF).</para>
    ///
    /// <para>No FK to <c>tenants</c>/<c>users</c> — those ARE wiped, and a
    /// CASCADE would take the surviving settings rows with them.</para>
    /// </summary>
    public partial class AddProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS provider_settings (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "TenantId" uuid NULL,
                    "UserId" uuid NULL,
                    "Scope" character varying(16) NOT NULL,
                    "ProviderKey" character varying(100) NOT NULL,
                    "DefaultModel" character varying(256) NULL,
                    "Enabled" boolean NOT NULL DEFAULT TRUE,
                    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    "UpdatedBy" uuid NULL,
                    CONSTRAINT "PK_provider_settings" PRIMARY KEY ("Id"),
                    CONSTRAINT ck_provider_settings_model CHECK (
                        "DefaultModel" IS NULL OR length("DefaultModel") > 0),
                    CONSTRAINT ck_provider_settings_principal_xor CHECK (
                        NOT ("TenantId" IS NOT NULL AND "UserId" IS NOT NULL)),
                    CONSTRAINT ck_provider_settings_scope CHECK (
                        ("Scope" = 'platform' AND "TenantId" IS NULL AND "UserId" IS NULL)
                        OR ("Scope" = 'principal' AND (
                            ("TenantId" IS NOT NULL AND "UserId" IS NULL)
                            OR ("TenantId" IS NULL AND "UserId" IS NOT NULL)))
                    )
                );

                CREATE INDEX IF NOT EXISTS "IX_provider_settings_ProviderKey"
                    ON provider_settings ("ProviderKey");

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_provider_settings_TenantId_UserId_ProviderKey"
                    ON provider_settings ("TenantId", "UserId", "ProviderKey")
                    NULLS NOT DISTINCT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS provider_settings;");
        }
    }
}
