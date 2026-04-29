using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 30-3 — adds the v2 ITenantInfrastructureProvider columns to the
    /// tenants table:
    ///
    /// <list type="bullet">
    ///   <item><description><c>ProviderKey</c> (TEXT/varchar(40), nullable)
    ///     — selects the v2 provider for the tenant (<c>cranl</c>,
    ///     <c>hetzner</c>, <c>cloudflare</c>, <c>byo</c>). NULL when the
    ///     tenant rides on shared infra.</description></item>
    ///   <item><description><c>ProviderResourceIds</c> (JSONB, nullable) —
    ///     opaque-to-the-platform map of provider-minted resource ids
    ///     (e.g. <c>{"cranl_project_id": "...", "cranl_database_id": "..."}</c>).
    ///     NULL until the provider populates it on first successful provision.</description></item>
    /// </list>
    ///
    /// <para><b>Backfill</b>: existing tenants that already have legacy
    /// <c>cranl_project_id</c> populated are stamped with
    /// <c>provider_key = 'cranl'</c> + the equivalent
    /// <c>provider_resource_ids</c> JSONB so the v2 dispatch workflow can
    /// resolve them through the registry without a code path that special-
    /// cases the legacy columns. Tenants without legacy Cranl ids stay on
    /// NULL — they're shared-infra rows.</para>
    ///
    /// <para>The structured-failure short code (the third v2 field per the
    /// 30-1 ADR §2) is NOT added here because the existing
    /// <c>tenants.failure_reason</c> shadow column from Epic 28's KEK
    /// rotations work covers the same use case; the v2 status snapshot
    /// reads it through <c>EF.Property&lt;string?&gt;("FailureReason")</c>.</para>
    /// </summary>
    public partial class AddV2ProviderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "tenants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderResourceIds",
                table: "tenants",
                type: "jsonb",
                nullable: true);

            // Backfill: any tenant that already has legacy Cranl identifiers
            // populated is, by definition, a Cranl-backed tenant — stamp the
            // v2 columns so the dispatch workflow's registry lookup works for
            // them on day one. Idempotent: we only update rows where
            // ProviderKey IS NULL, so re-running this migration (or a future
            // re-seed) won't overwrite a backend explicitly set elsewhere.
            migrationBuilder.Sql(@"
                UPDATE tenants
                SET ""ProviderKey"" = 'cranl',
                    ""ProviderResourceIds"" = jsonb_strip_nulls(jsonb_build_object(
                        'cranl_project_id',  ""CranlProjectId"",
                        'cranl_database_id', ""CranlDatabaseId"",
                        'cranl_app_id',      ""CranlAppId"",
                        'cranl_region',      ""CranlRegion""))
                WHERE ""CranlProjectId"" IS NOT NULL
                  AND ""ProviderKey"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No data to restore on rollback — the legacy cranl_* columns
            // remain populated, so the original information is intact.
            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProviderResourceIds",
                table: "tenants");
        }
    }
}
