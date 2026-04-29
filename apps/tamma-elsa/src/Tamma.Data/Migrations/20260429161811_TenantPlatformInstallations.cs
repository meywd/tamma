using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantPlatformInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_platform_installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    InstallationExternalId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CredentialSecretScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "tenant"),
                    CredentialSecretName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    WebhookSecretScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    WebhookSecretName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "connected"),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_platform_installations", x => x.Id);
                    table.CheckConstraint("CK_tenant_platform_installations_CredentialSecretScope", "\"CredentialSecretScope\" IN ('platform','tenant')");
                    table.CheckConstraint("CK_tenant_platform_installations_PlatformKind", "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
                    table.CheckConstraint("CK_tenant_platform_installations_Status", "\"Status\" IN ('connected','suspended','disconnected')");
                    table.CheckConstraint("CK_tenant_platform_installations_WebhookSecretScope", "\"WebhookSecretScope\" IS NULL OR \"WebhookSecretScope\" IN ('platform','tenant')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_platform_installations_PlatformKind_ExternalId",
                table: "tenant_platform_installations",
                columns: new[] { "PlatformKind", "InstallationExternalId" },
                filter: "\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_tenant_platform_installations_PrimaryPerKind",
                table: "tenant_platform_installations",
                columns: new[] { "TenantId", "PlatformKind" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_tenant_platform_installations_TenantId_Kind_ExternalId",
                table: "tenant_platform_installations",
                columns: new[] { "TenantId", "PlatformKind", "InstallationExternalId" },
                unique: true,
                filter: "\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_platform_installations");
        }
    }
}
