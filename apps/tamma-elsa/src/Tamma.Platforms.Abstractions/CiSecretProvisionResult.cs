namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — per-target outcome of a CI-secrets call. The provisioner
/// fans out across every target it was asked about, collects the
/// per-target results, and returns the list. Per-target failures do
/// NOT throw — the caller decides whether the partial success is
/// acceptable.
///
/// <para>Stable error codes (<see cref="Error"/> when
/// <see cref="Success"/> is false):</para>
/// <list type="bullet">
///   <item><c>scope_not_supported_on_platform</c> — capability gating
///         caught a scope the platform does not implement (e.g. User
///         scope on GitHub).</item>
///   <item><c>scope_target_mismatch</c> — caller passed a target
///         shape that does not match the scope (e.g. <c>Repo</c> scope
///         with an <c>Org</c> target).</item>
///   <item><c>masked_value_invalid: &lt;rule&gt;</c> — GitLab masked-value
///         pre-validation rejected the secret value
///         (length / charset / newlines).</item>
///   <item><c>auth_expired</c>, <c>permission_denied</c>,
///         <c>not_found</c>, <c>rate_limited</c>,
///         <c>service_unavailable</c>, <c>invalid_request:&lt;code&gt;</c>,
///         <c>unknown:&lt;reason&gt;</c> — flattened from
///         <see cref="PlatformError"/>.</item>
/// </list>
/// </summary>
/// <param name="Kind">Platform that produced the result.</param>
/// <param name="TargetDescriptor">
/// Stable, human-readable descriptor of the target — same format
/// <see cref="CiSecretTarget.Descriptor"/> emits. Never contains the
/// secret value.
/// </param>
/// <param name="Success">True iff the operation reached the platform
/// AND the platform accepted it.</param>
/// <param name="Error">
/// Null on success; one of the stable codes above on failure.
/// </param>
public sealed record CiSecretProvisionResult(
    PlatformKind Kind,
    string TargetDescriptor,
    bool Success,
    string? Error)
{
    /// <summary>
    /// Construct an Ok result.
    /// </summary>
    public static CiSecretProvisionResult Ok(
        PlatformKind kind, CiSecretTarget target) =>
        new(kind, target.Descriptor(), Success: true, Error: null);

    /// <summary>
    /// Construct a Failed result with a stable error code.
    /// </summary>
    public static CiSecretProvisionResult Failed(
        PlatformKind kind, CiSecretTarget target, string errorCode) =>
        new(kind, target.Descriptor(), Success: false, Error: errorCode);

    /// <summary>
    /// Flatten a <see cref="PlatformError"/> into the Failed form.
    /// </summary>
    public static CiSecretProvisionResult FromError(
        PlatformKind kind, CiSecretTarget target, PlatformError error)
    {
        var code = error switch
        {
            PlatformError.AuthExpired       => "auth_expired",
            PlatformError.PermissionDenied  => "permission_denied",
            PlatformError.NotFound          => "not_found",
            PlatformError.RateLimited       => "rate_limited",
            PlatformError.ServiceUnavailable => "service_unavailable",
            PlatformError.InvalidRequest ir => $"invalid_request:{ir.Code}",
            PlatformError.Unknown u         => $"unknown:{u.Reason}",
            _                               => $"unknown:{error.GetType().Name}",
        };
        return Failed(kind, target, code);
    }
}

/// <summary>
/// Story 31-8 — neutral metadata for an existing CI secret returned
/// from <see cref="ICiSecretsProvisioner.ListSecretsAsync"/>. The
/// platform never returns the secret VALUE on a list call; this is
/// strictly metadata.
/// </summary>
/// <param name="Name">Platform-side secret name / variable key.</param>
/// <param name="Scope">Scope this secret was provisioned at.</param>
/// <param name="TargetDescriptor">
/// Where it lives — same format as
/// <see cref="CiSecretTarget.Descriptor"/>.
/// </param>
/// <param name="UpdatedAtUtc">
/// Last update timestamp (UTC) when the platform reports it; null
/// when the platform's list endpoint does not surface modification
/// time (Gitea ≤ 1.24).
/// </param>
public sealed record CiSecretMetadataItem(
    string Name,
    CiSecretScope Scope,
    string TargetDescriptor,
    DateTimeOffset? UpdatedAtUtc);
