using System.Security.Cryptography;
using System.Text;

namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-2 — the well-known, PUBLIC genesis anchor for every audit
/// hash-chain (per-tenant and platform). It is NOT a secret; its only job is to
/// give the first record in a scope a deterministic <c>prev_hash</c> so an
/// external auditor can reproduce the whole chain from a documented starting
/// point.
///
/// <para>The genesis hash is <c>SHA-256("tamma.audit.chain.genesis.v1")</c>,
/// rendered lowercase-hex. Because the preimage is documented inline, anyone
/// can recompute it and verify the chain independently.</para>
///
/// <para><see cref="CanonicalVersion"/> is mixed into every canonical
/// serialization so a future format change is a NEW version (detectable),
/// never a silent edit that would invalidate historical records.</para>
/// </summary>
public static class AuditChainGenesis
{
    /// <summary>The documented genesis preimage — a public constant string.</summary>
    public const string Preimage = "tamma.audit.chain.genesis.v1";

    /// <summary>
    /// Canonicalization format version. Bump ONLY when the canonical field
    /// order / encoding changes; a bump makes old records verify under the old
    /// version and new records under the new one instead of silently breaking.
    /// </summary>
    public const byte CanonicalVersion = 1;

    /// <summary>The 32-byte genesis hash, lowercase-hex (64 chars).</summary>
    public static readonly string HashHex = ComputeGenesisHex();

    private static string ComputeGenesisHex()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Preimage));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
