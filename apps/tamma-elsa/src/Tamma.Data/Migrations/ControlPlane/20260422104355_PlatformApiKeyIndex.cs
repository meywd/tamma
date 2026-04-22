using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class PlatformApiKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_api_key_index",
                columns: table => new
                {
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HashedSuffix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_api_key_index", x => x.KeyPrefix);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_ApiKeyId",
                table: "platform_api_key_index",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_KeyPrefix_HashedSuffix",
                table: "platform_api_key_index",
                columns: new[] { "KeyPrefix", "HashedSuffix" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_TenantId",
                table: "platform_api_key_index",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_api_key_index");
        }
    }
}
