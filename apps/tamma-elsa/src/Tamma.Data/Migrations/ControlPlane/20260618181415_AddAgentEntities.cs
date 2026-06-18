using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddAgentEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    OwnerTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                    table.CheckConstraint("ck_agents_visibility_ownership", "(\"Visibility\" = 0 AND \"OwnerTenantId\" IS NULL AND \"OwnerUserId\" IS NULL) OR (\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL) OR (\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL AND \"OwnerTenantId\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "agent_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_versions_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_versions_agent_version",
                table: "agent_versions",
                columns: new[] { "AgentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_private_tenant_name",
                table: "agents",
                columns: new[] { "OwnerTenantId", "Name" },
                unique: true,
                filter: "\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agents_private_user_name",
                table: "agents",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true,
                filter: "\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agents_public_name_role",
                table: "agents",
                columns: new[] { "Name", "Role" },
                unique: true,
                filter: "\"Visibility\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_versions");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
