namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// Runtime read path for the stopgap secrets migrated by Story 29-9.
/// Callers ask for a secret by its cabinet name (see
/// <see cref="StopgapSecretMap"/>); the resolver tries the cabinet
/// first, then (during the Story 29-9 grace window) falls back to
/// <see cref="IConfiguration"/> / env vars.
///
/// <para>Story 29-10 removes the fallback path — after that release,
/// <see cref="GetAsync"/> throws <see cref="MissingSecretException"/>
/// when a cabinet row is absent.</para>
///
/// <para>Resolution order (while fallback is active):</para>
/// <list type="number">
///   <item><description>Lookup in <see cref="ISecretStoreBackend"/> via
///     the cabinet's active version.</description></item>
///   <item><description>Config probe via
///     <see cref="StopgapSecretDescriptor.ConfigKeys"/>.</description></item>
///   <item><description>Env-var probe via
///     <see cref="StopgapSecretDescriptor.EnvVars"/>.</description></item>
/// </list>
/// </summary>
public interface IRuntimeSecretResolver
{
    /// <summary>
    /// Resolve the plaintext for a migrated stopgap secret. Returns
    /// null when neither the cabinet nor any fallback source has a
    /// value AND fallback is enabled; throws
    /// <see cref="MissingSecretException"/> when the cabinet is empty
    /// and fallback is disabled (Story 29-10 mode).
    /// </summary>
    /// <param name="cabinetName">Canonical cabinet name — one of the
    /// constants on <see cref="StopgapSecretMap"/> (e.g.
    /// <c>"anthropic/api-key"</c>).</param>
    Task<string?> GetAsync(string cabinetName, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="IRuntimeSecretResolver.GetAsync"/> when the
/// cabinet is empty and the env-var fallback has been disabled
/// (Story 29-10). Distinct from a "no value anywhere" return because
/// the fail-fast path explicitly surfaces a deployment error.
/// </summary>
public sealed class MissingSecretException : Exception
{
    public string CabinetName { get; }

    public MissingSecretException(string cabinetName)
        : base($"No cabinet row for '{cabinetName}'. " +
               "Run `dotnet run --project Tamma.Api -- migrate-secrets` " +
               "(Story 29-9) to import stopgap secrets into the cabinet.")
    {
        CabinetName = cabinetName;
    }

    public MissingSecretException(string cabinetName, Exception inner)
        : base($"No cabinet row for '{cabinetName}'.", inner)
    {
        CabinetName = cabinetName;
    }
}
