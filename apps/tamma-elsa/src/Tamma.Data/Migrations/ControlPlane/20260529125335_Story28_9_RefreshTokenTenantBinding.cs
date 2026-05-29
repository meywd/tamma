using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 28-9 AC3 — binds refresh tokens to the tenant their access-token
    /// pair was minted for, links rotation siblings via a session-lineage
    /// pointer (<c>JtiChainHead</c>), and records WHY a row was revoked
    /// (<c>RevokedReason</c>) so SOC2 / SIEM tooling can distinguish a
    /// normal logout from a security event without consulting
    /// <c>platform_events</c>.
    ///
    /// <para><b>Columns added:</b>
    /// <list type="bullet">
    ///   <item><description><c>TenantId UUID NULL</c> — tenant the
    ///     refresh + access pair is scoped to. NULL for rootless tokens
    ///     issued at login when the user has 0 or 2+ memberships per
    ///     Story 28-9 AC4.</description></item>
    ///   <item><description><c>JtiChainHead UUID NULL</c> — the JTI of the
    ///     first access token in this rotation lineage. Rotation copies
    ///     the parent's chain head onto the child so reuse-detection can
    ///     revoke every sibling in one UPDATE. NULL for rows minted
    ///     before this story landed (pre-existing rows behave as if each
    ///     were its own chain head).</description></item>
    ///   <item><description><c>RevokedReason VARCHAR(32) NULL</c> —
    ///     closed enum of why the row was revoked. NULL parity with
    ///     <c>RevokedAt</c> is enforced by a CHECK constraint so SIEM
    ///     queries on <c>WHERE RevokedReason = 'reuse_detected'</c> can
    ///     trust the column.</description></item>
    /// </list></para>
    ///
    /// <para><b>Indexes:</b>
    /// <list type="bullet">
    ///   <item><description><c>IX_refresh_tokens_JtiChainHead</c> (partial,
    ///     <c>JtiChainHead IS NOT NULL</c>) — reuse-detection hot path:
    ///     <c>WHERE JtiChainHead = ? AND RevokedAt IS NULL</c>.</description></item>
    ///   <item><description><c>IX_refresh_tokens_UserId_TenantId</c>
    ///     (partial, <c>TenantId IS NOT NULL</c>) — admin tooling that
    ///     wants "every refresh token for user X in tenant Y".</description></item>
    /// </list></para>
    ///
    /// <para><b>Backwards compatibility:</b> all three columns are nullable
    /// + the indexes are partial, so existing pre-story rows continue to
    /// work as rootless / non-lineage tokens. The
    /// <c>/auth/refresh</c> handler treats a NULL <c>JtiChainHead</c> as
    /// "this row is its own chain" (no cross-row revocation). NULL
    /// <c>TenantId</c> is the rootless-token semantic per AC4.</para>
    /// </summary>
    public partial class Story28_9_RefreshTokenTenantBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JtiChainHead",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedReason",
                table: "refresh_tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_JtiChainHead",
                table: "refresh_tokens",
                column: "JtiChainHead",
                filter: "\"JtiChainHead\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId_TenantId",
                table: "refresh_tokens",
                columns: new[] { "UserId", "TenantId" },
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_refresh_tokens_RevokedReason",
                table: "refresh_tokens",
                sql: "\"RevokedReason\" IS NULL OR \"RevokedReason\" IN ('manual_logout','logout_all','rotation_consumed','switch_org','reuse_detected','password_reset','admin_force_logout')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_refresh_tokens_RevokedReason_NullParity",
                table: "refresh_tokens",
                sql: "(\"RevokedAt\" IS NULL) = (\"RevokedReason\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_JtiChainHead",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_UserId_TenantId",
                table: "refresh_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_refresh_tokens_RevokedReason",
                table: "refresh_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_refresh_tokens_RevokedReason_NullParity",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "JtiChainHead",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "RevokedReason",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "refresh_tokens");
        }
    }
}
