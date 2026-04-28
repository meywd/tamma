namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Inputs for <see cref="ISecretStore.CreateAsync"/>. Mirrors the
/// shape of <see cref="SecretMetadata"/> but excludes the fields the
/// store generates (<c>Id</c>, timestamps, version number, next-due).
///
/// <para><see cref="InitialPlaintext"/> is optional — for "import"
/// scenarios where the operator is migrating an existing secret value
/// into the cabinet (Story 29-9). When null, the secret row is
/// created with <c>ActiveVersionNumber = 0</c> and no plaintext until
/// a subsequent <see cref="ISecretStore.RotateAsync"/> mints the first
/// version.</para>
/// </summary>
public sealed record CreateSecretRequest(
    string Name,
    SecretScope Scope,
    Guid? TenantId,
    SecretPurpose Purpose,
    IReadOnlyList<ConsumerRef> ConsumerRefs,
    Guid OwnerUserId,
    RotationSchedule RotationSchedule,
    string? InitialPlaintext = null);

/// <summary>
/// Inputs for <see cref="ISecretStore.RotateAsync"/>. The caller
/// either supplies the new value (operator-driven rotation) or asks
/// the store to generate one (auto-rotation). Exactly one of
/// <see cref="NewPlaintext"/> / <see cref="GenerateLength"/> must be
/// non-null — the store enforces this.
///
/// <para>The store-generated path uses
/// <c>RandomNumberGenerator.GetBytes</c> + base64url encoding so the
/// resulting string is URL-safe (no padding, no <c>+</c>/<c>/</c>).</para>
/// </summary>
/// <param name="NewPlaintext">Operator-supplied plaintext. Mutually
/// exclusive with <see cref="GenerateLength"/>.</param>
/// <param name="GenerateLength">Number of random bytes to generate
/// before base64url encoding. Mutually exclusive with
/// <see cref="NewPlaintext"/>. Must be in <c>[16, 256]</c>.</param>
/// <param name="GraceWindow">How long the previous version stays
/// readable in <see cref="SecretVersionStatus.RetiredGrace"/>. Default
/// 5 minutes — long enough to drain in-flight requests, short enough
/// that a leaked previous value is quickly out of reach.</param>
public sealed record RotateSecretRequest(
    string? NewPlaintext = null,
    int? GenerateLength = null,
    TimeSpan? GraceWindow = null);

/// <summary>
/// Filter knobs for <see cref="ISecretStore.ListAsync"/>. All optional
/// — passing the default record returns every secret the caller's
/// scope can see (the store layers an authorisation filter on top of
/// this).
/// </summary>
/// <param name="Scope">Restrict to platform / tenant scope; null
/// returns both.</param>
/// <param name="TenantId">Restrict to a single tenant; null returns
/// all tenants the caller can see. Ignored when
/// <see cref="Scope"/> is <see cref="SecretScope.Platform"/>.</param>
/// <param name="Purpose">Restrict to a single purpose.</param>
/// <param name="NamePrefix">Restrict to names starting with the
/// supplied slug prefix (e.g. <c>db/</c> matches every DB-credential
/// secret).</param>
public sealed record SecretListFilter(
    SecretScope? Scope = null,
    Guid? TenantId = null,
    SecretPurpose? Purpose = null,
    string? NamePrefix = null);

/// <summary>
/// Out-of-band plaintext payload returned to a registered rotation
/// handler. Story 29-1 AC1 forbids <see cref="ISecretStore"/> from
/// surfacing plaintext on the HTTP-visible read API; this record
/// captures the only path that does — the in-process callback into a
/// rotation handler.
/// </summary>
/// <param name="Plaintext">UTF-8 plaintext.</param>
/// <param name="CreatedAt">UTC timestamp when the version was minted.</param>
public sealed record SecretValue(string Plaintext, DateTimeOffset CreatedAt);
