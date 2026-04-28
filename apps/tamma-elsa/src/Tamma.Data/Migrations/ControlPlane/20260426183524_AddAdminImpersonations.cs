using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 28-R2 follow-up B — first-class audit table for platform-admin
    /// impersonation sessions (SOC2 / ISO 27001 evidence).
    ///
    /// <para>One row per session: INSERT at <c>BeginImpersonation</c>,
    /// UPDATE setting <c>EndedAt</c> + <c>EndedReason</c> at session end
    /// (explicit exit / JWT expiry / forced revoke). Active-session
    /// queries hit a partial index on <c>EndedAt IS NULL</c> so the
    /// "who's currently impersonating?" incident-response surface stays
    /// O(active-count) regardless of historical volume.</para>
    ///
    /// <para>The <c>Reason</c> column is constrained at the DB level
    /// (<c>chk_impersonation_reason_charset</c>) to the same charset
    /// whitelist used for <c>X-Admin-Note</c> (Story 28-R2 / M17) so a
    /// malicious operator can't smuggle a log-forging or SSE-poisoning
    /// payload through the audit trail.</para>
    ///
    /// <para>FKs into <c>users</c> + <c>tenants</c> are RESTRICT — audit
    /// rows must outlive a deleted actor or target. SOC2 explicitly
    /// requires the trail to survive personnel changes.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddAdminImpersonations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_impersonations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ImpersonatorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImpersonatorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_impersonations", x => x.Id);
                    table.CheckConstraint("chk_impersonation_reason_charset", "\"Reason\" ~ '^[A-Za-z0-9 .,;:_!@#$%&()\\-]{1,500}$'");
                    table.ForeignKey(
                        name: "FK_admin_impersonations_tenants_TargetTenantId",
                        column: x => x.TargetTenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_admin_impersonations_users_ImpersonatorUserId",
                        column: x => x.ImpersonatorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_admin_impersonations_users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_active",
                table: "admin_impersonations",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_impersonator",
                table: "admin_impersonations",
                column: "ImpersonatorUserId");

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_target_tenant",
                table: "admin_impersonations",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_impersonations_TargetUserId",
                table: "admin_impersonations",
                column: "TargetUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_impersonations");
        }
    }
}
