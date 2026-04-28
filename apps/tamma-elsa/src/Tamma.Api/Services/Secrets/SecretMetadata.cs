namespace Tamma.Api.Services.Secrets;

/// <summary>
/// The fully-typed metadata row that the secret cabinet stores about
/// each managed secret, per Story 29-1 AC2. Plaintext is never on this
/// record — only the descriptive shape (name, scope, purpose, who
/// owns it, who consumes it, when to rotate it, the active version
/// number).
///
/// <para>Construct via <see cref="SecretMetadataFactory.Create"/> /
/// <see cref="SecretMetadataFactory.WithRotation"/> rather than the
/// primary constructor — the factory enforces the AC10 invariants
/// (e.g. a <see cref="SecretPurpose.DbCredential"/> with
/// <see cref="SecretScope.Tenant"/> requires a non-null
/// <see cref="TenantId"/>).</para>
/// </summary>
/// <param name="Id">Stable identifier — UUID v7-style guid generated
/// at create time.</param>
/// <param name="Name">Slug unique per <c>(scope, tenantId?)</c> per
/// AC7. Lower-kebab-case with optional <c>/</c> path
/// separators (e.g. <c>db/app-role</c>, <c>cranl/api-key</c>).</param>
/// <param name="Scope">Platform vs tenant scope.</param>
/// <param name="TenantId">Owning tenant when scope is
/// <see cref="SecretScope.Tenant"/>; null for platform-scoped
/// secrets.</param>
/// <param name="Purpose">Typed purpose — drives default rotation
/// cadence and admin-UI iconography.</param>
/// <param name="ConsumerRefs">Downstream consumers, rendered via
/// <see cref="ConsumerRefLookup"/>. May be empty for newly-created
/// secrets that haven't been wired yet.</param>
/// <param name="OwnerUserId">User id of the operator that created /
/// last edited the secret. Audit only — does not gate access.</param>
/// <param name="RotationSchedule">Cadence — None / Days / Cron.</param>
/// <param name="LastRotatedAt">UTC timestamp of the last successful
/// rotation. Null when no rotation has happened yet (e.g. the secret
/// was just created).</param>
/// <param name="NextRotationDueAt">UTC timestamp when the next
/// rotation is due. Computed by
/// <see cref="RotationScheduleCalculator.NextDue"/>; null when the
/// schedule is <see cref="RotationScheduleKind.None"/>.</param>
/// <param name="ActiveVersionNumber">Version number of the row
/// currently in <see cref="SecretVersionStatus.Active"/> status. 0
/// when no version has been minted yet (a freshly created secret with
/// no plaintext is legal — it's a placeholder for an upcoming
/// rotation).</param>
/// <param name="CreatedAt">UTC create timestamp.</param>
/// <param name="UpdatedAt">UTC timestamp of the last metadata edit
/// (rotation handler activations bump this too).</param>
public sealed record SecretMetadata(
    Guid Id,
    string Name,
    SecretScope Scope,
    Guid? TenantId,
    SecretPurpose Purpose,
    IReadOnlyList<ConsumerRef> ConsumerRefs,
    Guid OwnerUserId,
    RotationSchedule RotationSchedule,
    DateTimeOffset? LastRotatedAt,
    DateTimeOffset? NextRotationDueAt,
    int ActiveVersionNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Build a <see cref="SecretRef"/> for this metadata row — the
    /// opaque identifier callers pass to <see cref="ISecretStore"/>
    /// for subsequent operations.
    /// </summary>
    public SecretRef ToRef() => new(Scope, TenantId, Name);
}
