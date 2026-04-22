using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 — process-wide Key-Encryption-Key (KEK) cabinet for the
/// per-tenant connection-string envelope. Holds the active "primary"
/// KEK plus an optional "secondary" KEK used during the rotation
/// overlap window (Doc 01 §8.2).
///
/// <para>Configuration shape:</para>
/// <list type="bullet">
///   <item><description><c>Cranl:EncryptionKey</c> — base64-encoded
///     32 bytes. The primary KEK. This re-uses the same env var that
///     <see cref="Provisioning.TenantSecretProtector"/> already reads
///     so an existing deployment does not need a second secret. When
///     unset the provider falls back to a derived key (matches the
///     <see cref="Provisioning.TenantSecretProtector.FromConfiguration"/>
///     behaviour) so dev rigs and the Null provisioner path keep
///     working.</description></item>
///   <item><description><c>Tamma:Kek:Secondary</c> — base64-encoded
///     32 bytes. Optional; populated during a rotation window so the
///     decryptor can fall back when an envelope was encrypted under
///     the previous primary. Once the rotation worker re-encrypts every
///     row under the new primary the operator clears this slot.</description></item>
///   <item><description><c>Tamma:Kek:ActiveVersion</c> — int, default
///     1. Bumped by the operator (or the rotation runbook) when a new
///     primary is deployed; the rotation worker looks for tenant rows
///     whose <c>KekVersion</c> is below this number and re-encrypts
///     them.</description></item>
/// </list>
///
/// <para>Thread-safety: the cabinet is mutable to support in-process
/// promotion at the end of a rotation. Reads + writes are guarded by a
/// short critical section so a decrypt that races a promotion either
/// sees the old pair or the new pair atomically — never a torn view.</para>
///
/// <para>This module owns NO cryptography — the actual AES-GCM
/// encrypt/decrypt is done by
/// <see cref="Provisioning.TenantSecretProtector"/>. KekProvider only
/// answers "give me the bytes for the active primary / secondary".</para>
/// </summary>
public sealed class KekProvider
{
    private const int KekSize = 32;

    public const string PrimaryConfigKey = "Cranl:EncryptionKey";
    public const string SecondaryConfigKey = "Tamma:Kek:Secondary";
    public const string ActiveVersionConfigKey = "Tamma:Kek:ActiveVersion";

    private readonly ILogger<KekProvider> _logger;
    private readonly object _lock = new();

    private byte[]? _primary;
    private byte[]? _secondary;
    private int _activeVersion;

    public KekProvider(IConfiguration configuration, ILogger<KekProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        _primary = LoadKek(configuration[PrimaryConfigKey], PrimaryConfigKey);
        _secondary = LoadKek(configuration[SecondaryConfigKey], SecondaryConfigKey);
        _activeVersion = configuration.GetValue<int?>(ActiveVersionConfigKey) ?? 1;

        if (_primary is null)
        {
            // No primary: matches the legacy "Null provisioner / dev"
            // branch in TenantSecretProtector. The decryptor surfaces
            // a clear error if it is then asked to decrypt.
            _logger.LogInformation(
                "KekProvider: no primary KEK configured ({ConfigKey}). "
                + "AES-GCM connection-string decryption is unavailable until "
                + "the operator sets a 32-byte base64 value.",
                PrimaryConfigKey);
        }
        else
        {
            _logger.LogInformation(
                "KekProvider initialised primaryConfigured={Primary} "
                + "secondaryConfigured={Secondary} activeVersion={Version}",
                _primary is not null,
                _secondary is not null,
                _activeVersion);
        }
    }

    /// <summary>
    /// Snapshot of the current cabinet state, used by tests and the
    /// rotation status endpoint. Callers receive copies of the byte
    /// arrays so they cannot mutate the cabinet by accident.
    /// </summary>
    public KekSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new KekSnapshot(
                Primary: _primary is null ? null : (byte[])_primary.Clone(),
                Secondary: _secondary is null ? null : (byte[])_secondary.Clone(),
                ActiveVersion: _activeVersion);
        }
    }

    /// <summary>
    /// Returns a copy of the primary KEK or null when no primary is
    /// configured.
    /// </summary>
    public byte[]? GetPrimary()
    {
        lock (_lock)
        {
            return _primary is null ? null : (byte[])_primary.Clone();
        }
    }

    /// <summary>
    /// Returns a copy of the secondary KEK or null when the rotation
    /// slot is empty (steady-state).
    /// </summary>
    public byte[]? GetSecondary()
    {
        lock (_lock)
        {
            return _secondary is null ? null : (byte[])_secondary.Clone();
        }
    }

    /// <summary>
    /// Currently-deployed KEK version. The rotation worker writes this
    /// into <c>tenants.KekVersion</c> after re-encrypting a row.
    /// </summary>
    public int GetActiveVersion()
    {
        lock (_lock)
        {
            return _activeVersion;
        }
    }

    /// <summary>
    /// Stage a new KEK as the secondary slot — used at the start of a
    /// rotation when the operator (or the
    /// <see cref="KekRotationCoordinator"/>) freshly mints a key. The
    /// secondary then becomes the encrypt target while the primary
    /// stays in place to decrypt the existing envelopes. Promotion to
    /// primary happens via <see cref="PromoteSecondaryToPrimary"/>
    /// once every row has been re-encrypted.
    /// </summary>
    public void StageSecondary(byte[] newSecondary)
    {
        ArgumentNullException.ThrowIfNull(newSecondary);
        if (newSecondary.Length != KekSize)
        {
            throw new ArgumentException(
                $"KEK must be exactly {KekSize} bytes (got {newSecondary.Length}).",
                nameof(newSecondary));
        }
        lock (_lock)
        {
            _secondary = (byte[])newSecondary.Clone();
        }
        _logger.LogInformation(
            "KekProvider: secondary KEK staged. Rotation window is now active.");
    }

    /// <summary>
    /// Promote the staged secondary to primary, bump the active version
    /// number, and retire the previous primary by clearing the
    /// secondary slot. Called by
    /// <see cref="KekRotationCoordinator"/> after every tenant row has
    /// been re-encrypted under the new key.
    /// </summary>
    public void PromoteSecondaryToPrimary(int newActiveVersion)
    {
        if (newActiveVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newActiveVersion),
                newActiveVersion,
                "Active version must be a positive integer.");
        }

        lock (_lock)
        {
            if (_secondary is null)
            {
                throw new InvalidOperationException(
                    "Cannot promote: no secondary KEK is staged.");
            }
            if (newActiveVersion <= _activeVersion)
            {
                throw new InvalidOperationException(
                    $"newActiveVersion ({newActiveVersion}) must exceed the "
                    + $"current active version ({_activeVersion}).");
            }

            // Zero the previous primary before letting the GC reclaim it.
            if (_primary is not null) CryptographicOperations.ZeroMemory(_primary);

            _primary = _secondary;
            _secondary = null;
            _activeVersion = newActiveVersion;
        }

        _logger.LogInformation(
            "KekProvider: secondary promoted to primary. Active version is now "
            + "{Version}; rotation window closed.",
            newActiveVersion);
    }

    private byte[]? LoadKek(string? configuredValue, string configKey)
    {
        if (string.IsNullOrWhiteSpace(configuredValue)) return null;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configuredValue);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{configKey} is not valid base64.", ex);
        }

        if (decoded.Length != KekSize)
        {
            throw new InvalidOperationException(
                $"{configKey} must decode to {KekSize} bytes (got {decoded.Length}).");
        }

        return decoded;
    }
}

/// <summary>
/// Immutable snapshot of <see cref="KekProvider"/> state.
/// </summary>
public sealed record KekSnapshot(
    byte[]? Primary,
    byte[]? Secondary,
    int ActiveVersion);
