namespace Tamma.Data.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = null!;
    public string OwnerId { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public string Label { get; set; } = null!;
    public string[] Permissions { get; set; } = [];
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RotatedFromId { get; set; }

    /// <summary>
    /// Encrypted plaintext of the API key for the <c>installation</c> scope.
    /// Audit finding 018 — TS stored an at-rest encrypted copy on the
    /// installation row so rotation could re-provision the same plaintext to
    /// every GitHub Actions secret. The C# port moved key storage to this
    /// table (one-to-many keys per owner) and dropped the column; restoring
    /// it here keeps the rotation re-push path viable once the secrets
    /// provisioner lands (cross-ref finding 013). NULL for non-installation
    /// scopes (e.g. user/service keys, where the plaintext is never recoverable
    /// — those flows generate-and-show-once.)
    /// </summary>
    public byte[]? EncryptedPlaintext { get; set; }
}
