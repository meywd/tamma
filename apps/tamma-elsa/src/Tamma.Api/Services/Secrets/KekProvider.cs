using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 — process-wide Key-Encryption-Key (KEK) cabinet for the
/// per-tenant connection-string envelope. Holds the active "primary"
/// KEK plus an optional "secondary" KEK used during the rotation
/// overlap window (Doc 01 §8.2). R2-H13 adds a small ring of retired
/// keys so that envelopes still tagged with a previous
/// <c>KekVersion</c> can be decrypted by version (no two-key fallback
/// heuristic).
///
/// <para>Configuration shape:</para>
/// <list type="bullet">
///   <item><description><c>Cranl:EncryptionKey</c> — base64-encoded
///     32 bytes. The primary KEK. This re-uses the same env var that
///     <see cref="Provisioning.TenantSecretProtector"/> already reads
///     so an existing deployment does not need a second secret. When
///     unset the provider falls back to a derived key (matches the
///     <see cref="Provisioning.TenantSecretProtector.FromConfiguration(IConfiguration, ILogger?)"/>
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
///   <item><description><c>Tamma:Kek:RetainedHistorySize</c> — int,
///     default 2. Maximum number of retired KEK slots the cabinet
///     keeps in memory after promotion. Operators bump this when they
///     plan to delay re-encryption or run rotations back-to-back.
///     Lower bound 1 (one historical slot is required for the H13
///     "explicit version → key" lookup to handle a row stuck at the
///     immediately-previous version).</description></item>
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
/// answers "give me the bytes for the active primary / secondary /
/// retired slot N".</para>
/// </summary>
public sealed class KekProvider
{
    private const int KekSize = 32;
    private const int DefaultRetainedHistorySize = 2;
    private const int MinRetainedHistorySize = 1;

    public const string PrimaryConfigKey = "Cranl:EncryptionKey";
    public const string SecondaryConfigKey = "Tamma:Kek:Secondary";
    public const string ActiveVersionConfigKey = "Tamma:Kek:ActiveVersion";
    public const string RetainedHistorySizeConfigKey = "Tamma:Kek:RetainedHistorySize";

    private readonly ILogger<KekProvider> _logger;
    private readonly object _lock = new();

    private byte[]? _primary;
    private byte[]? _secondary;
    private int _activeVersion;
    private int _secondaryVersion;
    // R2-H13: small ring of retired KEKs keyed by version. The decryptor
    // looks these up directly when a tenant row carries a stale
    // KekVersion. The list is bounded by RetainedHistorySize.
    private readonly LinkedList<KekSlot> _retiredKeys = new();
    private readonly int _retainedHistorySize;

    public KekProvider(IConfiguration configuration, ILogger<KekProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        _primary = LoadKek(configuration[PrimaryConfigKey], PrimaryConfigKey);
        _secondary = LoadKek(configuration[SecondaryConfigKey], SecondaryConfigKey);
        _activeVersion = configuration.GetValue<int?>(ActiveVersionConfigKey) ?? 1;
        // The secondary, when configured at startup, represents the
        // PREVIOUS primary version (rotation step 2 in the runbook).
        _secondaryVersion = _secondary is null ? 0 : Math.Max(1, _activeVersion - 1);
        var configuredHistorySize = configuration.GetValue<int?>(RetainedHistorySizeConfigKey)
            ?? DefaultRetainedHistorySize;
        _retainedHistorySize = Math.Max(MinRetainedHistorySize, configuredHistorySize);

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
                + "secondaryConfigured={Secondary} activeVersion={Version} "
                + "retainedHistorySize={History}",
                _primary is not null,
                _secondary is not null,
                _activeVersion,
                _retainedHistorySize);
        }
    }

    /// <summary>
    /// Maximum number of retired KEK slots kept in memory after
    /// promotion. R2-H13: the startup health check uses this to refuse
    /// to boot when there are tenant rows further behind than the
    /// cabinet can decrypt.
    /// </summary>
    public int RetainedHistorySize => _retainedHistorySize;

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
    /// R2-H13: look up the KEK slot for a specific version. Returns
    /// the matching slot when the version corresponds to the active
    /// primary, the rotation-window secondary, or a retired key still
    /// inside the <see cref="RetainedHistorySize"/> ring. Returns null
    /// when the version is unknown — caller decides whether to fail or
    /// fall back to the heuristic two-slot path (legacy callers only).
    /// </summary>
    /// <param name="version">Version to look up. Must be positive.</param>
    public KekSlot? GetByVersion(int version)
    {
        if (version <= 0) return null;
        lock (_lock)
        {
            if (version == _activeVersion && _primary is not null)
            {
                return new KekSlot(version, (byte[])_primary.Clone(), KekSlotKind.Primary);
            }
            if (version == _secondaryVersion && _secondary is not null && _secondaryVersion > 0)
            {
                return new KekSlot(version, (byte[])_secondary.Clone(), KekSlotKind.Secondary);
            }
            foreach (var slot in _retiredKeys)
            {
                if (slot.Version == version)
                {
                    return slot with { Material = (byte[])slot.Material.Clone() };
                }
            }
            return null;
        }
    }

    /// <summary>
    /// R2-H13: snapshot of every key currently decryptable by the
    /// cabinet, in version order (newest first). Used by the startup
    /// health check.
    /// </summary>
    public IReadOnlyList<KekSlot> GetAllSlots()
    {
        lock (_lock)
        {
            var list = new List<KekSlot>();
            if (_primary is not null)
            {
                list.Add(new KekSlot(_activeVersion, (byte[])_primary.Clone(), KekSlotKind.Primary));
            }
            if (_secondary is not null && _secondaryVersion > 0)
            {
                list.Add(new KekSlot(_secondaryVersion, (byte[])_secondary.Clone(), KekSlotKind.Secondary));
            }
            foreach (var slot in _retiredKeys)
            {
                list.Add(slot with { Material = (byte[])slot.Material.Clone() });
            }
            return list.OrderByDescending(s => s.Version).ToList();
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
            // The secondary that gets staged during rotation IS the new
            // primary-to-be (version = active + 1). The previous primary
            // is what continues to decrypt-on-fallback at version
            // _activeVersion. Track the upcoming version explicitly so
            // GetByVersion can answer correctly during the rotation
            // window.
            _secondaryVersion = _activeVersion + 1;
        }
        _logger.LogInformation(
            "KekProvider: secondary KEK staged. Rotation window is now active. "
            + "secondaryVersion={Version}",
            _secondaryVersion);
    }

    /// <summary>
    /// Promote the staged secondary to primary, bump the active version
    /// number, and retire the previous primary by clearing the
    /// secondary slot. Called by
    /// <see cref="KekRotationCoordinator"/> after every tenant row has
    /// been re-encrypted under the new key.
    ///
    /// <para>R2-H13: the previous primary is moved into the retired-keys
    /// ring rather than zeroed immediately. It stays decryptable for
    /// rows that may not yet be re-encrypted (e.g. a row added to the
    /// table mid-rotation that the rotation worker hasn't seen). The
    /// ring size is bounded by <see cref="RetainedHistorySize"/>;
    /// older keys are zeroed and dropped.</para>
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

            // R2-H13: retain the previous primary in the historical
            // ring so we can decrypt rows still tagged with the old
            // KekVersion.
            if (_primary is not null)
            {
                var retired = new KekSlot(_activeVersion, _primary, KekSlotKind.Retired);
                _retiredKeys.AddFirst(retired);
                while (_retiredKeys.Count > _retainedHistorySize)
                {
                    var oldest = _retiredKeys.Last!;
                    _retiredKeys.RemoveLast();
                    CryptographicOperations.ZeroMemory(oldest.Value.Material);
                }
            }

            _primary = _secondary;
            _secondary = null;
            _secondaryVersion = 0;
            _activeVersion = newActiveVersion;
        }

        _logger.LogInformation(
            "KekProvider: secondary promoted to primary. Active version is now "
            + "{Version}; rotation window closed. retainedHistory={RetainedCount}",
            newActiveVersion,
            _retiredKeys.Count);
    }

    /// <summary>
    /// R2-H14: load a staged secondary that was persisted to durable
    /// storage by an earlier in-flight rotation. Used at startup by
    /// <see cref="KekRotationCoordinator"/> when it discovers a
    /// pending <c>kek_rotations</c> row whose secondary has not yet
    /// been promoted (or zeroed). Re-uses the same locking + slot
    /// versioning rules as <see cref="StageSecondary"/>.
    /// </summary>
    public void RestoreStagedSecondary(byte[] material, int newSecondaryVersion)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material.Length != KekSize)
        {
            throw new ArgumentException(
                $"KEK must be exactly {KekSize} bytes (got {material.Length}).",
                nameof(material));
        }
        if (newSecondaryVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newSecondaryVersion));
        }
        lock (_lock)
        {
            _secondary = (byte[])material.Clone();
            _secondaryVersion = newSecondaryVersion;
        }
        _logger.LogInformation(
            "KekProvider: restored staged secondary from durable storage. "
            + "secondaryVersion={Version}",
            newSecondaryVersion);
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

/// <summary>
/// R2-H13: a single slot in the KEK cabinet — version + key material +
/// what role the slot is playing right now.
/// </summary>
public sealed record KekSlot(int Version, byte[] Material, KekSlotKind Kind);

/// <summary>
/// Role of a KEK slot at the moment of lookup.
/// </summary>
public enum KekSlotKind
{
    /// <summary>The active primary — used for new encrypts.</summary>
    Primary,
    /// <summary>The staged secondary — being re-encrypted to during a rotation window.</summary>
    Secondary,
    /// <summary>A retired key — still in the cabinet for rows not yet re-encrypted.</summary>
    Retired,
}
