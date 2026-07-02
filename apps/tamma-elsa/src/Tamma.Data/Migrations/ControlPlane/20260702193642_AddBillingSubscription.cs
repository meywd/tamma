using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddBillingSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PlanSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "free"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "active"),
                    CurrentPeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TrialEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ScheduledPlanSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ScheduledEffectiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StripeScheduleId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_subscriptions", x => x.Id);
                    table.CheckConstraint("ck_billing_subscriptions_status", "\"Status\" IN ('trialing','active','past_due','canceled','incomplete','incomplete_expired','unpaid')");
                    table.ForeignKey(
                        name: "FK_billing_subscriptions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_billing_subscriptions_StripeSubscriptionId",
                table: "billing_subscriptions",
                column: "StripeSubscriptionId",
                unique: true,
                filter: "\"StripeSubscriptionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_billing_subscriptions_TenantId_NonTerminal",
                table: "billing_subscriptions",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" NOT IN ('canceled','incomplete_expired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_subscriptions");
        }
    }
}
