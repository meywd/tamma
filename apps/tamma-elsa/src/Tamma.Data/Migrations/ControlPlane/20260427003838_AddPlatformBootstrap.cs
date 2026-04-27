using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 28-R2 / PF-S9 — single-row sentinel that pins which user
    /// owns the bootstrap superadmin promotion. Closes the previous
    /// TOCTOU race where two concurrent first-user registrations both
    /// observed <c>existingUserCount == 0</c> and both received
    /// <c>platform_admin</c>.
    ///
    /// <para>Mathematics of "at most one row": the table has a unique
    /// PK on <c>Id</c> AND a CHECK constraint <c>"Id" = 1</c>. The PK
    /// rejects a second row with <c>Id=1</c>; the CHECK rejects every
    /// other value. Concurrent inserts therefore race for the single
    /// allowed row — exactly one wins, every loser receives a
    /// unique-violation that <see cref="Tamma.Data.Repositories.PlatformBootstrapRepository"/>
    /// catches and translates into a "fall back to user role"
    /// signal.</para>
    ///
    /// <para>FK to <c>users</c> is RESTRICT — an operator can't delete
    /// the bootstrap admin without explicitly clearing the sentinel.
    /// Soft-delete on users is an app-level convention; hard-delete is
    /// not permitted, so this FK is effectively a tripwire.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddPlatformBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_bootstrap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_bootstrap", x => x.Id);
                    table.CheckConstraint("ck_platform_bootstrap_singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_platform_bootstrap_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_bootstrap_UserId",
                table: "platform_bootstrap",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_bootstrap");
        }
    }
}
