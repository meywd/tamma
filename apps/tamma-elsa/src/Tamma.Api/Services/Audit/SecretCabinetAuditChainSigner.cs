using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC5) — default <see cref="IAuditChainSigner"/>. Resolves the
/// HMAC-SHA256 signing key from the Epic 29 cabinet via
/// <see cref="IRuntimeSecretResolver"/> — the same seam Story 35-5's Stripe
/// signing secret uses (cabinet-first, with the Story 29-9 coexistence-window
/// config fallback). The key material only ever travels the cabinet's
/// out-of-band path; no plaintext env key is invented as the source of truth.
///
/// <para><b>Fail-closed (AC5):</b> when the cabinet has no key,
/// <see cref="SignAsync"/> throws and <see cref="VerifyAsync"/> returns false —
/// a missing key is never treated as "valid".</para>
///
/// <para><b>Key version.</b> Each checkpoint records the
/// <see cref="AuditChainSigningKeyVersion"/> that signed it so a future rotation
/// can validate historical anchors against the version that produced them. The
/// resolver seam currently exposes only the ACTIVE version's plaintext, so
/// verification uses the active key; multi-version key retention is a documented
/// follow-up gated on the cabinet exposing versioned plaintext reads.</para>
/// </summary>
public sealed class SecretCabinetAuditChainSigner : IAuditChainSigner
{
    /// <summary>The key version stamped on new checkpoints (see class remarks).</summary>
    public const int AuditChainSigningKeyVersion = 1;

    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// DI ctor. Resolves <see cref="IRuntimeSecretResolver"/> LAZILY via the
    /// provider (mirrors <c>StripeSigningSecretSource</c>) so the signer stays
    /// constructible even in composition roots — e.g. tests — that do not wire
    /// the stopgap resolver. When the resolver is absent, signing fails closed.
    /// </summary>
    public SecretCabinetAuditChainSigner(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task<(byte[] Signature, int KeyVersion)> SignAsync(
        string scope, Guid? tenantId, long headSequence, string headHashHex,
        DateTime signedAt, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(ct).ConfigureAwait(false);
        if (key is null)
        {
            throw new InvalidOperationException(
                "audit chain signing key is not available in the cabinet "
                + $"('{StopgapSecretMap.PlatformAuditChainSigningKey}'); cannot sign a "
                + "checkpoint. Provision the key (Story 29-9 migrate-secrets) — the chain "
                + "must fail closed rather than emit an unsigned anchor.");
        }

        var preimage = AuditChainCheckpointCanonicalizer.PreimageBytes(
            scope, tenantId, headSequence, headHashHex, signedAt);
        var signature = HMACSHA256.HashData(key, preimage);
        return (signature, AuditChainSigningKeyVersion);
    }

    public async Task<bool> VerifyAsync(
        AuditChainCheckpointView checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var key = await ResolveKeyAsync(ct).ConfigureAwait(false);
        if (key is null) return false; // fail-closed

        var preimage = AuditChainCheckpointCanonicalizer.PreimageBytes(
            checkpoint.Scope, checkpoint.TenantId, checkpoint.HeadSequence,
            checkpoint.HeadHash, checkpoint.SignedAt);
        var expected = HMACSHA256.HashData(key, preimage);
        return CryptographicOperations.FixedTimeEquals(expected, checkpoint.Signature);
    }

    private async Task<byte[]?> ResolveKeyAsync(CancellationToken ct)
    {
        var resolver = _serviceProvider.GetService<IRuntimeSecretResolver>();
        if (resolver is null) return null; // resolver not wired → fail-closed

        var material = await resolver
            .GetAsync(StopgapSecretMap.PlatformAuditChainSigningKey, ct)
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(material) ? null : Encoding.UTF8.GetBytes(material);
    }
}
