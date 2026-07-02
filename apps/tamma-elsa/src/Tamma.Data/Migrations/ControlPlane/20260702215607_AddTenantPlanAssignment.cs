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

            // Story 34-4 AC3 — back-fill exactly one `active` assignment per
            // existing (non-deleted) tenant, pinning the plan's CURRENT Version
            // at migration time. Resolution: the Epic-28 shadow `PlanId` column
            // (p) → else the plan whose Slug matches the legacy `Plan` string (s)
            // → else the active `free` plan (f). PlanVersion is read from the
            // SAME chosen plan row so it stays consistent with PlanId.
            migrationBuilder.Sql(@"
                INSERT INTO tenant_plan_assignments
                    (""Id"", ""TenantId"", ""PlanId"", ""PlanVersion"", ""Status"",
                     ""EffectiveFrom"", ""AssignedByUserId"", ""Reason"", ""CreatedAt"", ""UpdatedAt"")
                SELECT gen_random_uuid(),
                       t.""Id"",
                       COALESCE(t.""PlanId"", s.""Id"", f.""Id""),
                       COALESCE(p.""Version"", s.""Version"", f.""Version"", 1),
                       'active',
                       t.""CreatedAt"",
                       NULL,
                       'backfill: 34-4 migration',
                       now(),
                       now()
                FROM tenants t
                LEFT JOIN plans p ON p.""Id"" = t.""PlanId""
                LEFT JOIN plans s ON s.""Slug"" = t.""Plan"" AND s.""Status"" = 'active'
                LEFT JOIN plans f ON f.""Slug"" = 'free' AND f.""Status"" = 'active'
                WHERE t.""DeletedAt"" IS NULL
                  AND COALESCE(t.""PlanId"", s.""Id"", f.""Id"") IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_plan_assignments");
        }
    }
}
