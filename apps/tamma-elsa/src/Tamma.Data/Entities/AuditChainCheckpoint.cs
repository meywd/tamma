namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-2 (AC5) — a signed anchor of an audit hash-chain head at a point in
/// time. The signature (HMAC-SHA256 over the canonical
/// <c>scope ‖ head_sequence ‖ head_hash ‖ signed_at</c> preimage, using a key
/// from the Epic 29 cabinet) makes the anchor forgery-resistant even against an
/// attacker with direct DB write access to the records table.
///
/// <para><b>Always control-plane-resident</b>, for BOTH platform and tenant
/// scopes. Keeping tenant checkpoints OUTSIDE the tenant's own schema means a
/// tenant with DB write access to its schema cannot rewrite both its records AND
/// its anchor — the anchor lives where the tenant cannot reach it. Tenant-scope
/// rows set <see cref="TenantId"/>; platform-scope rows leave it null.</para>
/// </summary>
public class AuditChainCheckpoint
{
    /// <summary>Surrogate PK — Postgres <c>gen_random_uuid()</c> default.</summary>
    public Guid Id { get; set; }

    /// <summary><c>'platform'</c> or <c>'tenant'</c> — which chain this anchors.</summary>
    public string Scope { get; set; } = null!;

    /// <summary>Set for tenant scope; null for platform scope (CHECK-enforced).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The chain head <c>chain_sequence</c> this checkpoint anchors.</summary>
    public long HeadSequence { get; set; }

    /// <summary>The chain head <c>record_hash</c> (lowercase-hex) at <see cref="HeadSequence"/>.</summary>
    public string HeadHash { get; set; } = null!;

    /// <summary>When the anchor was signed (UTC).</summary>
    public DateTime SignedAt { get; set; }

    /// <summary>HMAC-SHA256 signature over the canonical checkpoint preimage.</summary>
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>Which cabinet signing-key version produced <see cref="Signature"/>;
    /// lets rotation not strand historical checkpoints (AC5).</summary>
    public int KeyVersion { get; set; }

    /// <summary>Row creation timestamp (Postgres <c>now()</c> default).</summary>
    public DateTime CreatedAt { get; set; }
}
