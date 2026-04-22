using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 29-3 EF entity for the <c>secret_reveal_tokens</c> table. One
/// row per reveal-token issued on create or rotate. The row carries
/// only the HMAC-SHA256 hash of the bearer token — the plaintext token
/// is returned to the caller exactly once and never persisted in
/// cleartext, so a DB dump does not leak the ability to reveal a
/// secret.
///
/// <para><b>Lifecycle</b> — tokens move through the three-state enum
/// <c>Status</c>:
/// <list type="number">
///   <item><description><c>unused</c> — issued, not yet consumed and
///     not yet past <see cref="ExpiresAt"/>.</description></item>
///   <item><description><c>consumed</c> — caller hit the reveal
///     endpoint with this token and received the plaintext. A second
///     call returns 410 Gone.</description></item>
///   <item><description><c>expired</c> — the background sweeper flips
///     <c>unused</c> rows to this status once their
///     <see cref="ExpiresAt"/> has passed without a consume call. A
///     reveal call on an expired row returns 410 with the
///     <c>expired</c> error code.</description></item>
/// </list></para>
///
/// <para><b>Indexes</b> — the migration pins a partial index on
/// <c>(status) WHERE status = 'unused'</c> with an
/// <c>expires_at</c> include so the 30-second sweep query stays cheap
/// as the table grows, plus a unique index on <c>token_hash</c> so
/// reveal-by-token lookups are a single btree probe.</para>
///
/// <para>Lives on the same physical database as the
/// <see cref="SecretRow"/> / <see cref="SecretVersionRow"/> tables (the
/// Story 29-2 schema). Platform-scoped secrets on the control-plane DB;
/// tenant-scoped secrets on the per-tenant DB — the reveal-token row
/// inherits the scope implicitly via the connection.</para>
/// </summary>
[Table("secret_reveal_tokens")]
[Index(nameof(TokenHash), IsUnique = true)]
[Index(nameof(Status), nameof(ExpiresAt))]
public class SecretRevealTokenRow
{
    /// <summary>Stable identifier — UUID v4 server-generated.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// HMAC-SHA256 hash of the raw token, produced under the Story 29-2
    /// primary KEK via <c>HMACSHA256(kek, tokenBytes)</c>. 32 bytes.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();

    /// <summary>Foreign key to the <c>secrets</c> table.</summary>
    public Guid SecretId { get; set; }

    /// <summary>Version number this token reveals.</summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// User id of the operator that created / rotated the secret and
    /// thus owns this reveal token. <see cref="Guid.Empty"/> for
    /// system-initiated flows (e.g. the <c>platform</c> scope admin
    /// endpoint when no user context is present in tests).
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>UTC create timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp past which the token no longer reveals its value.
    /// Set to <c>CreatedAt + 60 s</c> per Story 29-3 AC1.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// UTC timestamp of the consume call that burned this token. Null
    /// when the token has not been consumed yet.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// Current lifecycle status. One of <c>unused</c>, <c>consumed</c>,
    /// <c>expired</c>. Persisted as a short string so the schema is not
    /// coupled to a CLR enum ordering.
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string Status { get; set; } = "unused";

    /// <summary>
    /// Optional user-agent captured at reveal time for the audit
    /// event. Null until consumed.
    /// </summary>
    [MaxLength(512)]
    public string? ConsumedUserAgent { get; set; }

    /// <summary>
    /// SHA-256 hash of the remote IP that consumed the token. Stored
    /// hashed so the audit row does not leak the operator's IP
    /// directly. Hex-encoded 64 chars. Null until consumed.
    /// </summary>
    [MaxLength(64)]
    public string? ConsumedIpHash { get; set; }
}
