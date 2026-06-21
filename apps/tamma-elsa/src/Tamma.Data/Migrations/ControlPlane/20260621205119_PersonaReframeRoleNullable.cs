using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class PersonaReframeRoleNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_public_name_role",
                table: "agents");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_agents_public_name",
                table: "agents",
                column: "Name",
                unique: true,
                filter: "\"Visibility\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_public_name",
                table: "agents");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_public_name_role",
                table: "agents",
                columns: new[] { "Name", "Role" },
                unique: true,
                filter: "\"Visibility\" = 0");
        }
    }
}
