using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// R2-H14 — adds <c>kek_rotations</c> CP table for durable
    /// recording of in-flight + completed KEK rotations. The
    /// <c>StagedSecondaryProtected</c> column carries the new KEK
    /// material encrypted by the OLD primary so a process crash
    /// mid-rotation can resume by reloading the row.
    /// </summary>
    /// <inheritdoc />
    public partial class KekRotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kek_rotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VersionOld = table.Column<int>(type: "integer", nullable: false),
                    VersionNew = table.Column<int>(type: "integer", nullable: false),
                    StagedSecondaryProtected = table.Column<byte[]>(type: "bytea", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kek_rotations", x => x.Id);
                    table.CheckConstraint(
                        "CK_kek_rotations_status",
                        "\"Status\" IN ('pending','running','completed','failed','cancelled')");
                });

            // Hot read: the active in-flight row. Partial index keeps
            // the index tight; most rows are completed / failed and
            // not on the hot read path.
            migrationBuilder.CreateIndex(
                name: "IX_kek_rotations_Status",
                table: "kek_rotations",
                column: "Status",
                filter: "\"Status\" IN ('pending','running')");

            // Reverse-chronological list for the operator dashboard.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_kek_rotations_StartedAt\" "
                + "ON \"kek_rotations\" (\"StartedAt\" DESC)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "kek_rotations");
        }
    }
}
