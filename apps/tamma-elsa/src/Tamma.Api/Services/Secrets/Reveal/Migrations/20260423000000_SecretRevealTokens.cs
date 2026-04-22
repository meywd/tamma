using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Api.Services.Secrets.Reveal.Migrations
{
    /// <summary>
    /// Story 29-3 — adds the <c>secret_reveal_tokens</c> table backing
    /// the reveal-once UX. Hand-authored (rather than
    /// <c>dotnet ef migrations add</c>) because the design ships
    /// alongside the service in a single PR; the model-snapshot file
    /// is generated to match.
    ///
    /// <para>Index strategy:
    /// <list type="bullet">
    ///   <item><description>Unique on <c>token_hash</c> — every reveal
    ///     request is a single-probe lookup.</description></item>
    ///   <item><description>Partial on <c>(status, expires_at)</c>
    ///     <c>WHERE status = 'unused'</c> — the 30-second sweep query
    ///     scans only the open rows, not the consumed / expired
    ///     tail.</description></item>
    ///   <item><description>Non-partial fallback on
    ///     <c>(status, expires_at)</c> declared via the
    ///     <c>[Index]</c> attribute on the entity so ORMs that
    ///     re-derive the schema without partial support still get a
    ///     usable plan.</description></item>
    /// </list></para>
    /// </summary>
    public partial class SecretRevealTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secret_reveal_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false,
                        defaultValueSql: "gen_random_uuid()"),
                    TokenHash = table.Column<byte[]>(type: "bytea",
                        maxLength: 32, nullable: false),
                    SecretId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone", nullable: false,
                        defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTime>(
                        type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(
                        type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(
                        type: "character varying(16)", maxLength: 16,
                        nullable: false),
                    ConsumedUserAgent = table.Column<string>(
                        type: "character varying(512)", maxLength: 512,
                        nullable: true),
                    ConsumedIpHash = table.Column<string>(
                        type: "character varying(64)", maxLength: 64,
                        nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_reveal_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secret_reveal_tokens_TokenHash",
                table: "secret_reveal_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_secret_reveal_tokens_Status_ExpiresAt",
                table: "secret_reveal_tokens",
                columns: new[] { "Status", "ExpiresAt" });

            // Partial index that accelerates the sweeper's
            // "find me every unused row past its expiry" query. Not
            // expressible via DataAnnotations or the fluent API in
            // EF Core 8, so we drop to raw SQL here.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_secret_reveal_tokens_Unused_ExpiresAt\" " +
                "ON secret_reveal_tokens (\"ExpiresAt\") " +
                "WHERE \"Status\" = 'unused';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_secret_reveal_tokens_Unused_ExpiresAt\";");

            migrationBuilder.DropTable(name: "secret_reveal_tokens");
        }
    }
}
