using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Round-2 review fix M8 + H8 — extend
    /// <c>platform_queued_tasks</c> with two operational columns:
    /// <list type="bullet">
    ///   <item><description><c>ClaimedBy</c> (varchar 128, nullable):
    ///     id of the worker holding the row's reservation. Populated by
    ///     <c>PlatformQueuedTaskRepository.ReserveNextAsync(workerId, ...)</c>;
    ///     cleared when the row returns to <c>pending</c> (failure
    ///     retry, reaper recovery). Previously the workerId was
    ///     accepted by the API but silently discarded — operators had
    ///     no way to identify the original claimant on a stuck
    ///     row.</description></item>
    ///   <item><description><c>UnprocessableAt</c> (timestamptz,
    ///     nullable): set when a worker observes the row but has no
    ///     <c>IPlatformTaskHandler</c> registered for the row's
    ///     <c>Type</c>. The row stays in <c>pending</c> + bumps
    ///     <c>RetryCount</c> instead of being immediately dead-lettered,
    ///     so a deploy that subsequently registers the handler can
    ///     pick up the parked work. Falls through to
    ///     <c>dead_letter</c> only after <c>RetryCount</c> reaches the
    ///     configured ceiling.</description></item>
    /// </list>
    /// Both columns are nullable with no default — existing rows survive
    /// untouched, and the worker fills them in lazily on the next claim.
    /// </summary>
    /// <inheritdoc />
    public partial class PlatformQueuedTaskClaimedByUnprocessable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "platform_queued_tasks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnprocessableAt",
                table: "platform_queued_tasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "platform_queued_tasks");

            migrationBuilder.DropColumn(
                name: "UnprocessableAt",
                table: "platform_queued_tasks");
        }
    }
}
