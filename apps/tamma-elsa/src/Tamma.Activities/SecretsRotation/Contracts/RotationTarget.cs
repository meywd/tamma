namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 — lightweight DTO passed to <see cref="IRotationHandler"/>
/// methods. Carries only the fields the handler needs: the secret's
/// id, its scope + tenant id, the consumer-ref system + identifier
/// parsed from <c>ConsumerRef[0]</c>, the version numbers involved.
///
/// <para>Kept in <c>Tamma.Activities</c> so handlers can implement the
/// contract without taking a dependency on the full
/// <c>SecretMetadata</c> record that lives in <c>Tamma.Api</c>.</para>
/// </summary>
/// <param name="SecretId">Store-generated secret id.</param>
/// <param name="Name">Human slug (for log lines).</param>
/// <param name="TenantId">Owning tenant for tenant-scoped secrets; null
/// for platform-scoped.</param>
/// <param name="ConsumerSystem">First <c>ConsumerRef.System</c> —
/// matches <see cref="IRotationHandler.System"/>.</param>
/// <param name="ConsumerIdentifier">First <c>ConsumerRef.Identifier</c>
/// — handler-specific (e.g. <c>role=tamma_app;db=tamma_control</c>).</param>
/// <param name="NewVersionNumber">Monotonic number for the version the
/// current rotation is introducing.</param>
/// <param name="PreviousVersionNumber">Monotonic number of the version
/// currently <c>Active</c>; zero when this is the first rotation.</param>
public sealed record RotationTarget(
    Guid SecretId,
    string Name,
    Guid? TenantId,
    string ConsumerSystem,
    string ConsumerIdentifier,
    int NewVersionNumber,
    int PreviousVersionNumber);
