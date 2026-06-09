using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class TenancyP0_PlanPlacementPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlacementPolicy",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "shared");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_placement_policy",
                table: "plans",
                sql: "\"PlacementPolicy\" IN ('shared','dedicated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_placement_policy",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "PlacementPolicy",
                table: "plans");
        }
    }
}
