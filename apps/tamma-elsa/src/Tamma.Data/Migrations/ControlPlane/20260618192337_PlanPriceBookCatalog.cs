using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class PlanPriceBookCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plans_Slug",
                table: "plans");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "plans",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "BillingInterval",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "monthly");

            migrationBuilder.AddColumn<bool>(
                name: "IsCustom",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesPlanId",
                table: "plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "plan_entitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetricKey = table.Column<string>(type: "text", nullable: false),
                    LimitValue = table.Column<long>(type: "bigint", nullable: true),
                    Period = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "monthly"),
                    OverageMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "block")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_entitlements", x => x.Id);
                    table.CheckConstraint("ck_plan_entitlements_overage", "\"OverageMode\" IN ('block','allow','meter')");
                    table.CheckConstraint("ck_plan_entitlements_period", "\"Period\" IN ('monthly','total')");
                    table.ForeignKey(
                        name: "FK_plan_entitlements_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    StringValue = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_features", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plan_features_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_prices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PricingMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "platform_provided"),
                    RecurringUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    SeatUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    MeteredComponent = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_prices", x => x.Id);
                    table.CheckConstraint("ck_plan_prices_mode", "\"PricingMode\" IN ('platform_provided','byok')");
                    table.ForeignKey(
                        name: "FK_plan_prices_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plans_SupersedesPlanId",
                table: "plans",
                column: "SupersedesPlanId");

            migrationBuilder.CreateIndex(
                name: "UX_plans_OneActivePerSlug",
                table: "plans",
                column: "Slug",
                unique: true,
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "UX_plans_Slug_Version",
                table: "plans",
                columns: new[] { "Slug", "Version" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_billing_interval",
                table: "plans",
                sql: "\"BillingInterval\" IN ('monthly','annual')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_status",
                table: "plans",
                sql: "\"Status\" IN ('active','deprecated','draft')");

            migrationBuilder.CreateIndex(
                name: "UX_plan_entitlements_PlanId_MetricKey",
                table: "plan_entitlements",
                columns: new[] { "PlanId", "MetricKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_plan_features_PlanId_FeatureKey",
                table: "plan_features",
                columns: new[] { "PlanId", "FeatureKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_plan_prices_PlanId_PricingMode",
                table: "plan_prices",
                columns: new[] { "PlanId", "PricingMode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_plans_plans_SupersedesPlanId",
                table: "plans",
                column: "SupersedesPlanId",
                principalTable: "plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_plans_plans_SupersedesPlanId",
                table: "plans");

            migrationBuilder.DropTable(
                name: "plan_entitlements");

            migrationBuilder.DropTable(
                name: "plan_features");

            migrationBuilder.DropTable(
                name: "plan_prices");

            migrationBuilder.DropIndex(
                name: "IX_plans_SupersedesPlanId",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "UX_plans_OneActivePerSlug",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "UX_plans_Slug_Version",
                table: "plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_billing_interval",
                table: "plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_status",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "BillingInterval",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "IsCustom",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "SupersedesPlanId",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "plans");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "plans",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_plans_Slug",
                table: "plans",
                column: "Slug",
                unique: true);
        }
    }
}
