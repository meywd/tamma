using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Api.Services.Secrets.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConsumerRefs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RotationSchedule = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{\"Kind\":\"None\"}'::jsonb"),
                    LastRotatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRotationDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActiveVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "secret_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SecretId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    KekId = table.Column<byte>(type: "smallint", nullable: false),
                    FormatVersion = table.Column<byte>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_versions_secrets_SecretId",
                        column: x => x.SecretId,
                        principalTable: "secrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secret_versions_KekId",
                table: "secret_versions",
                column: "KekId");

            migrationBuilder.CreateIndex(
                name: "IX_secret_versions_SecretId_VersionNumber",
                table: "secret_versions",
                columns: new[] { "SecretId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Scope_Name",
                table: "secrets",
                columns: new[] { "Scope", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "secret_versions");

            migrationBuilder.DropTable(
                name: "secrets");
        }
    }
}
