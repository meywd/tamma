using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddTenantPlanAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_plan_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_plan_assignments", x => x.Id);
                    table.CheckConstraint("ck_tpa_effective_window", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("ck_tpa_status", "\"Status\" IN ('active','scheduled','cancelled')");
                    table.CheckConstraint("ck_tpa_version_positive", "\"PlanVersion\" >= 1");
                    table.ForeignKey(
                        name: "FK_tenant_plan_assignments_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_plan_assignments_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_plan_assignments_PlanId",
                table: "tenant_plan_assignments",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_plan_assignments_TenantId_Status",
                table: "tenant_plan_assignments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_tpa_one_active_per_tenant",
                table: "tenant_plan_assignments",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" = 'active'");

            // Story 34-4 AC3 — back-fill exactly one `active` assignment for EVERY
            // existing (non-deleted) tenant, pinning the plan's CURRENT Version at
            // migration time. Resolution chain (PlanId + Version stay paired):
            //   1. the version-pinned Epic-28 shadow `PlanId` FK (p), else
            //   2. the plan whose Slug matches the legacy `Plan` string (s), else
            //   3. the active `free` plan as a GUARANTEED terminal fallback.
            // The prior version silently OMITTED any tenant that resolved to NULL
            // (via `AND COALESCE(...) IS NOT NULL`), which — if the active `free`
            // plan were ever absent — left a non-deleted tenant with NO assignment
            // and made Story 34-6 throw NO_ASSIGNMENT/404 for it post-deploy. This
            // version drops that filter and instead FAILS LOUD (RAISE) when a
            // tenant would be left assignment-less because `free` is missing, so the
            // fault is caught at deploy time, never silently after.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    free_id uuid;
                    free_version int;
                    orphan_count int;
                BEGIN
                    SELECT f.""Id"", f.""Version"" INTO free_id, free_version
                    FROM plans f
                    WHERE f.""Slug"" = 'free' AND f.""Status"" = 'active'
                    LIMIT 1;

                    -- Any non-deleted tenant that resolves to NO plan through the
                    -- chain (pinned PlanId → active slug match → active free) means
                    -- the terminal `free` fallback is unavailable. Fail loud rather
                    -- than leaving that tenant assignment-less.
                    SELECT count(*) INTO orphan_count
                    FROM tenants t
                    LEFT JOIN plans s ON s.""Slug"" = t.""Plan"" AND s.""Status"" = 'active'
                    WHERE t.""DeletedAt"" IS NULL
                      AND COALESCE(t.""PlanId"", s.""Id"", free_id) IS NULL;

                    IF orphan_count > 0 THEN
                        RAISE EXCEPTION
                            'AddTenantPlanAssignment back-fill: % non-deleted tenant(s) resolve to no plan and the active ''free'' plan is absent — seed the ''free'' plan before migrating so every tenant gets a terminal fallback assignment.',
                            orphan_count;
                    END IF;

                    INSERT INTO tenant_plan_assignments
                        (""Id"", ""TenantId"", ""PlanId"", ""PlanVersion"", ""Status"",
                         ""EffectiveFrom"", ""AssignedByUserId"", ""Reason"", ""CreatedAt"", ""UpdatedAt"")
                    SELECT gen_random_uuid(),
                           t.""Id"",
                           COALESCE(t.""PlanId"", s.""Id"", free_id),
                           COALESCE(p.""Version"", s.""Version"", free_version, 1),
                           'active',
                           t.""CreatedAt"",
                           NULL,
                           'backfill: 34-4 migration',
                           now(),
                           now()
                    FROM tenants t
                    LEFT JOIN plans p ON p.""Id"" = t.""PlanId""
                    LEFT JOIN plans s ON s.""Slug"" = t.""Plan"" AND s.""Status"" = 'active'
                    WHERE t.""DeletedAt"" IS NULL;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_plan_assignments");
        }
    }
}
