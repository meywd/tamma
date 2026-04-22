using System.Collections.Frozen;

namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// Default <see cref="IKekProvider"/>. Reads two base64-encoded
/// 32-byte KEKs from environment variables:
/// <list type="bullet">
///   <item><description><c>TAMMA_SECRET_STORE_KEK_PRIMARY</c> —
///     required. Format: <c>kekId:base64(32-byte-key)</c> (e.g.
///     <c>1:JL2X...=</c>). The slot id at the start tells the
///     envelope which byte to embed; on rotation, the operator
///     stages the NEW key in <c>_PRIMARY</c> and moves the old key
///     to <c>_SECONDARY</c>.</description></item>
///   <item><description><c>TAMMA_SECRET_STORE_KEK_SECONDARY</c> —
///     optional. Same format. Lets the process decrypt envelopes
///     wrapped under the old KEK while the rewrap pass catches
///     up.</description></item>
/// </list>
///
/// <para>Validation at construction time:
/// <list type="bullet">
///   <item><description>Primary env var must be set — throws on
///     startup otherwise so the host fails fast (caller wires this
///     check via the DI registration in
///     <see cref="Tamma.Api.Extensions.SecretsServiceCollectionExtensions"/>).</description></item>
///   <item><description>Both keys must base64-decode to exactly 32
///     bytes (AES-256).</description></item>
///   <item><description>Slot ids must be in <c>[0, 255]</c> and
///     unique.</description></item>
/// </list></para>
///
/// <para>Once constructed the provider is immutable and
/// thread-safe — registered as a singleton.</para>
/// </summary>
public sealed class EnvKekProvider : IKekProvider
{
    /// <summary>Env-var name for the primary (write-side) KEK.</summary>
    public const string PrimaryEnvVar = "TAMMA_SECRET_STORE_KEK_PRIMARY";

    /// <summary>Env-var name for the secondary (decrypt-only) KEK.</summary>
    public const string SecondaryEnvVar = "TAMMA_SECRET_STORE_KEK_SECONDARY";

    /// <summary>AES-256 key length in bytes.</summary>
    public const int KekLengthBytes = 32;

    private readonly FrozenDictionary<byte, byte[]> _keksBySlot;

    /// <inheritdoc />
    public byte PrimaryKekId { get; }

    /// <summary>
    /// Construct from explicit env-var sources. The standard
    /// production wire-up uses <see cref="FromEnvironment"/>; this
    /// constructor exists so unit tests can inject deterministic
    /// values without having to mutate process-wide environment
    /// state.
    /// </summary>
    public EnvKekProvider(string primarySpec, string? secondarySpec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primarySpec);

        var (primaryId, primaryKey) = ParseKekSpec(primarySpec, PrimaryEnvVar);
        var slots = new Dictionary<byte, byte[]> { [primaryId] = primaryKey };

        if (!string.IsNullOrWhiteSpace(secondarySpec))
        {
            var (secondaryId, secondaryKey) = ParseKekSpec(secondarySpec, SecondaryEnvVar);
            if (secondaryId == primaryId)
            {
                throw new InvalidOperationException(
                    $"Primary and secondary KEKs share slot id {primaryId}. " +
                    "Each slot must be unique so envelopes can be unambiguously " +
                    "routed during a rotation overlap.");
            }
            slots[secondaryId] = secondaryKey;
        }

        PrimaryKekId = primaryId;
        _keksBySlot = slots.ToFrozenDictionary();
    }

    /// <summary>
    /// Build from process environment. Throws
    /// <see cref="InvalidOperationException"/> when the primary env
    /// var is missing — adopted by the DI extension's startup health
    /// check so a misconfigured host fails immediately rather than
    /// at first use.
    /// </summary>
    public static EnvKekProvider FromEnvironment()
    {
        var primary = Environment.GetEnvironmentVariable(PrimaryEnvVar);
        if (string.IsNullOrWhiteSpace(primary))
        {
            throw new InvalidOperationException(
                $"Required env var {PrimaryEnvVar} is not set. " +
                "Generate a 32-byte key (`openssl rand -base64 32`) and " +
                "export as 'kekId:base64key' (e.g. '1:JL2X...=').");
        }
        var secondary = Environment.GetEnvironmentVariable(SecondaryEnvVar);
        return new EnvKekProvider(primary, secondary);
    }

    /// <inheritdoc />
    public byte[] GetKek(byte kekId)
    {
        if (!_keksBySlot.TryGetValue(kekId, out var key))
            throw new KekNotAvailableException(kekId);
        // Defensive copy so a caller can't zero our backing buffer.
        return (byte[])key.Clone();
    }

    /// <inheritdoc />
    public bool TryGetKek(byte kekId, out byte[]? key)
    {
        if (_keksBySlot.TryGetValue(kekId, out var stored))
        {
            key = (byte[])stored.Clone();
            return true;
        }
        key = null;
        return false;
    }

    private static (byte SlotId, byte[] Key) ParseKekSpec(string spec, string sourceName)
    {
        // Spec format: "<slotId>:<base64-32-byte-key>".
        // Slot id is decimal 0..255; the colon separator is required
        // so a future migration can carry richer metadata in the
        // remainder without ambiguity.
        var colonIndex = spec.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= spec.Length - 1)
        {
            throw new InvalidOperationException(
                $"{sourceName}: expected format 'slotId:base64Key' but got " +
                $"a value with no colon separator (or colon at the boundary).");
        }

        var slotPart = spec[..colonIndex];
        var keyPart = spec[(colonIndex + 1)..];

        if (!byte.TryParse(slotPart, out var slotId))
        {
            throw new InvalidOperationException(
                $"{sourceName}: slot id '{slotPart}' is not a byte (0..255).");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyPart);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{sourceName}: key material is not valid base64.", ex);
        }

        if (keyBytes.Length != KekLengthBytes)
        {
            throw new InvalidOperationException(
                $"{sourceName}: key length is {keyBytes.Length} bytes; " +
                $"expected {KekLengthBytes} (AES-256).");
        }

        return (slotId, keyBytes);
    }
}
