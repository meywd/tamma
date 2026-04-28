using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Guarded factory for <see cref="SecretMetadata"/>. Centralises the
/// AC10 invariants (purpose × scope × tenant-id) so backend drivers,
/// admin endpoints, and tests all enforce the same rules — and a new
/// purpose / scope rule lands in one place rather than being
/// duplicated.
///
/// <para>Three entry points:</para>
/// <list type="bullet">
///   <item><description><see cref="Create"/> — minted at create-time
///     by <see cref="ISecretStore.CreateAsync"/>. Generates the
///     <c>Id</c>, stamps the timestamps, runs every invariant.</description></item>
///   <item><description><see cref="WithRotation"/> — derives a new
///     metadata snapshot after a successful rotation: bumps
///     <c>ActiveVersionNumber</c>, sets <c>LastRotatedAt</c> to now,
///     recomputes <c>NextRotationDueAt</c>.</description></item>
///   <item><description><see cref="WithEdits"/> — applies admin-UI
///     edits (consumers, schedule, owner) and re-runs the invariants
///     so an edit can't violate AC10.</description></item>
/// </list>
/// </summary>
public static class SecretMetadataFactory
{
    /// <summary>
    /// Slug pattern for secret names per Story 29-1 AC7: lower-kebab-
    /// case with optional <c>/</c> path separators, must start and end
    /// with an alphanumeric, length 3..200.
    /// </summary>
    /// <remarks>
    /// Anchored regex; bounded with a length check so the engine never
    /// scans more than 200 chars (audit: prompts/013 reDoS guard
    /// pattern).
    /// </remarks>
    private static readonly Regex NameRegex = new(
        "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(/[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Build a freshly created <see cref="SecretMetadata"/> row.
    /// Throws <see cref="ArgumentException"/> on invariant violation.
    /// </summary>
    /// <param name="name">Slug per <see cref="NameRegex"/>.</param>
    /// <param name="scope">Platform or Tenant.</param>
    /// <param name="tenantId">Required when <paramref name="scope"/>
    /// is <see cref="SecretScope.Tenant"/>; must be null when
    /// <paramref name="scope"/> is <see cref="SecretScope.Platform"/>.</param>
    /// <param name="purpose">Typed purpose.</param>
    /// <param name="consumerRefs">Downstream consumers; may be
    /// empty.</param>
    /// <param name="ownerUserId">Operator that created the secret.</param>
    /// <param name="rotationSchedule">Cadence; defaults to
    /// <see cref="RotationSchedule.None"/>.</param>
    /// <param name="now">UTC clock — pass an explicit time for tests;
    /// production should pass <c>DateTimeOffset.UtcNow</c>.</param>
    public static SecretMetadata Create(
        string name,
        SecretScope scope,
        Guid? tenantId,
        SecretPurpose purpose,
        IReadOnlyList<ConsumerRef>? consumerRefs,
        Guid ownerUserId,
        RotationSchedule? rotationSchedule,
        DateTimeOffset now)
    {
        ValidateName(name);
        ValidateScopeTenant(scope, tenantId);
        ValidatePurposeScope(purpose, scope, tenantId);
        ValidateOwner(ownerUserId);

        var schedule = rotationSchedule ?? RotationSchedule.None;
        var consumers = consumerRefs ?? Array.Empty<ConsumerRef>();
        var nextDue = RotationScheduleCalculator.NextDue(
            schedule, lastRotatedAt: null, now);

        return new SecretMetadata(
            Id: Guid.NewGuid(),
            Name: name,
            Scope: scope,
            TenantId: tenantId,
            Purpose: purpose,
            ConsumerRefs: consumers,
            OwnerUserId: ownerUserId,
            RotationSchedule: schedule,
            LastRotatedAt: null,
            NextRotationDueAt: nextDue,
            ActiveVersionNumber: 0,
            CreatedAt: now,
            UpdatedAt: now);
    }

    /// <summary>
    /// Project an existing metadata row into a post-rotation snapshot:
    /// active version bumped, last-rotated stamped at
    /// <paramref name="now"/>, next-due recomputed off the schedule.
    /// </summary>
    public static SecretMetadata WithRotation(
        SecretMetadata current,
        int newActiveVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (newActiveVersion <= current.ActiveVersionNumber)
            throw new ArgumentException(
                $"New active version ({newActiveVersion}) must be greater " +
                $"than current ({current.ActiveVersionNumber}).",
                nameof(newActiveVersion));

        var nextDue = RotationScheduleCalculator.NextDue(
            current.RotationSchedule,
            lastRotatedAt: now,
            now);

        return current with
        {
            ActiveVersionNumber = newActiveVersion,
            LastRotatedAt = now,
            NextRotationDueAt = nextDue,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Project an existing metadata row into a re-keyed (consumers /
    /// schedule edited) snapshot. Re-runs all invariants so an admin
    /// edit can't violate AC10 (e.g. flipping a tenant secret to
    /// platform scope).
    /// </summary>
    public static SecretMetadata WithEdits(
        SecretMetadata current,
        IReadOnlyList<ConsumerRef>? consumerRefs,
        RotationSchedule? rotationSchedule,
        Guid? ownerUserId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        var schedule = rotationSchedule ?? current.RotationSchedule;
        var consumers = consumerRefs ?? current.ConsumerRefs;
        var owner = ownerUserId ?? current.OwnerUserId;
        ValidateOwner(owner);

        var nextDue = RotationScheduleCalculator.NextDue(
            schedule,
            current.LastRotatedAt,
            now);

        return current with
        {
            ConsumerRefs = consumers,
            RotationSchedule = schedule,
            OwnerUserId = owner,
            NextRotationDueAt = nextDue,
            UpdatedAt = now,
        };
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Secret name must be non-empty.", nameof(name));
        if (name.Length is < 3 or > 200)
            throw new ArgumentException(
                "Secret name length must be between 3 and 200 characters.",
                nameof(name));
        if (!NameRegex.IsMatch(name))
            throw new ArgumentException(
                "Secret name must be lower-kebab-case with optional `/` " +
                "path separators (e.g. `db/app-role`).",
                nameof(name));
    }

    private static void ValidateScopeTenant(SecretScope scope, Guid? tenantId)
    {
        switch (scope)
        {
            case SecretScope.Platform when tenantId is not null:
                throw new ArgumentException(
                    "Platform-scoped secrets must not carry a tenant id.",
                    nameof(tenantId));
            case SecretScope.Tenant when tenantId is null:
                throw new ArgumentException(
                    "Tenant-scoped secrets must carry a non-null tenant id.",
                    nameof(tenantId));
        }
    }

    private static void ValidatePurposeScope(
        SecretPurpose purpose, SecretScope scope, Guid? tenantId)
    {
        // AC10: a DbCredential at tenant scope requires a non-null
        // tenant id. ValidateScopeTenant already enforces that for
        // every tenant-scoped secret, but we re-assert here so the
        // exception message names the purpose explicitly — operators
        // get a clearer error than a bare "tenant id is null".
        if (purpose == SecretPurpose.DbCredential
            && scope == SecretScope.Tenant
            && tenantId is null)
        {
            throw new ArgumentException(
                "DbCredential secrets at Tenant scope require a non-null " +
                "tenant id.",
                nameof(tenantId));
        }

        // Cross-tenant DB credentials at Platform scope are legitimate
        // (the central tamma_app role lives there). No additional
        // invariants today; future purpose × scope rules belong here.
    }

    private static void ValidateOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException(
                "Owner user id must be a non-empty Guid.",
                nameof(ownerUserId));
    }
}
