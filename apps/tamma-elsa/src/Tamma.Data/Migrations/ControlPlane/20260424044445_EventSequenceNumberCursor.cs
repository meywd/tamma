using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 5.6 (Wave C.2 follow-up — alert evaluator cursor bug fix).
    /// Switches the <c>AlertRuleEvaluator</c>'s cursor scheme from a
    /// <c>(LastEventAt, LastEventId)</c> composite (where the
    /// tiebreak ran on a Guid <i>string</i> compare and dropped events
    /// whose Guid sorted ≤ the cursor on the same-tick boundary) to
    /// per-stream <c>BIGSERIAL</c> sequence numbers.
    ///
    /// <para>Schema delta:</para>
    /// <list type="bullet">
    ///   <item><description><c>domain_events.SequenceNumber</c> —
    ///     server-assigned <c>BIGSERIAL</c>. Strictly monotonic per
    ///     insertion, immune to <c>CreatedAt</c> ties. Backed by a
    ///     unique index that doubles as the cursor-scan covering
    ///     index.</description></item>
    ///   <item><description><c>platform_events.SequenceNumber</c> —
    ///     same shape, independent sequence (the two tables don't
    ///     share an ordering plane).</description></item>
    ///   <item><description><c>alert_evaluator_cursor</c>: drops
    ///     <c>LastEventAt</c> + <c>LastEventId</c>, adds
    ///     <c>LastDomainSequenceNumber</c> +
    ///     <c>LastPlatformSequenceNumber</c> (both <c>BIGINT NOT
    ///     NULL DEFAULT 0</c>). The reset to 0 is intentional — on
    ///     first deploy after this migration the evaluator
    ///     re-processes any events past the high-water mark, but the
    ///     in-memory throttle map + sink-side rate limiter
    ///     de-duplicate the resulting fires. The previous cursor's
    ///     timestamp/id was unsafe to translate (string-compared
    ///     against a Guid would still skip events) so a clean reset
    ///     is the safer choice.</description></item>
    /// </list>
    /// </summary>
    /// <inheritdoc />
    public partial class EventSequenceNumberCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── domain_events.SequenceNumber ──────────────────────
            migrationBuilder.AddColumn<long>(
                name: "SequenceNumber",
                table: "domain_events",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation(
                    "Npgsql:ValueGenerationStrategy",
                    NpgsqlValueGenerationStrategy.SerialColumn);

            migrationBuilder.CreateIndex(
                name: "UX_domain_events_SequenceNumber",
                table: "domain_events",
                column: "SequenceNumber",
                unique: true);

            // ── platform_events.SequenceNumber ────────────────────
            migrationBuilder.AddColumn<long>(
                name: "SequenceNumber",
                table: "platform_events",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation(
                    "Npgsql:ValueGenerationStrategy",
                    NpgsqlValueGenerationStrategy.SerialColumn);

            migrationBuilder.CreateIndex(
                name: "UX_platform_events_SequenceNumber",
                table: "platform_events",
                column: "SequenceNumber",
                unique: true);

            // ── alert_evaluator_cursor cursor swap ────────────────
            migrationBuilder.DropColumn(
                name: "LastEventAt",
                table: "alert_evaluator_cursor");

            migrationBuilder.DropColumn(
                name: "LastEventId",
                table: "alert_evaluator_cursor");

            migrationBuilder.AddColumn<long>(
                name: "LastDomainSequenceNumber",
                table: "alert_evaluator_cursor",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastPlatformSequenceNumber",
                table: "alert_evaluator_cursor",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse cursor swap first (safest order — drop the new
            // columns before re-adding the old ones, so a subsequent
            // re-Up doesn't collide on column names).
            migrationBuilder.DropColumn(
                name: "LastDomainSequenceNumber",
                table: "alert_evaluator_cursor");

            migrationBuilder.DropColumn(
                name: "LastPlatformSequenceNumber",
                table: "alert_evaluator_cursor");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEventAt",
                table: "alert_evaluator_cursor",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<Guid>(
                name: "LastEventId",
                table: "alert_evaluator_cursor",
                type: "uuid",
                nullable: true);

            // ── platform_events SequenceNumber rollback ───────────
            migrationBuilder.DropIndex(
                name: "UX_platform_events_SequenceNumber",
                table: "platform_events");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "platform_events");

            // ── domain_events SequenceNumber rollback ─────────────
            migrationBuilder.DropIndex(
                name: "UX_domain_events_SequenceNumber",
                table: "domain_events");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "domain_events");
        }
    }
}
