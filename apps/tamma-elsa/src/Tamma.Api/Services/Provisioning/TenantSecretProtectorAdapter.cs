using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Story 28-5 — adapter that exposes the existing
/// <see cref="TenantSecretProtector"/> via the lower-layer
/// <see cref="ITenantConnectionStringProtector"/> contract used by the
/// tenant-lifecycle activities. Lets us keep the activity assembly free
/// of Tamma.Api references.
///
/// <para>The KEK slot is hard-coded to <c>1</c> for now; Story 28-12 wires
/// the rotation map and starts returning the active slot from a registry.</para>
/// </summary>
public sealed class TenantSecretProtectorAdapter : ITenantConnectionStringProtector
{
    private readonly TenantSecretProtector _inner;

    public TenantSecretProtectorAdapter(TenantSecretProtector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public int CurrentKekVersion => 1;

    public byte[] Encrypt(string plaintext) => _inner.Encrypt(plaintext);
}
