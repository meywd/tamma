using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 — central catalogue of <c>TENANT.*</c> event types and a
/// thin builder that materialises a <see cref="PlatformEvent"/> ready for
/// <c>IPlatformEventPublisher.AppendAndPublishAsync</c>.
///
/// <para>Keeping the constants in one file avoids string-typo drift across
/// the eleven create steps + ten delete steps and gives the
/// <c>TENANT.*</c> dashboards (Story 28-11) a single grep target. The
/// names track Doc 03 §2.1 event taxonomy:</para>
///
/// <list type="bullet">
///   <item><description><c>TENANT.PROVISIONING_REQUESTED</c> — verify-email
///     trigger; published before the workflow starts.</description></item>
///   <item><description><c>TENANT.PROVISION.STEP_STARTED / STEP_COMPLETED /
///     STEP_FAILED</c> — per-step lifecycle markers, idempotent via the
///     partial unique <c>(tenant_id, type, tags-&gt;&gt;'step',
///     tags-&gt;&gt;'attempt')</c> index from Story 28-1 / 28-6.</description></item>
///   <item><description><c>TENANT.CREATED.SUCCESS</c> — terminal success on
///     create; the user-task asks specifically for this name. Aliased
///     internally as <c>TENANT.PROVISIONED.SUCCESS</c> too because Doc
///     03 §2.3 names the public surface that way; both are emitted so
///     downstream subscribers expecting either name receive the event.</description></item>
///   <item><description><c>TENANT.PROVISION.FAILED</c> — terminal failure
///     after compensation, with a <c>compensation_outcome</c> tag.</description></item>
///   <item><description><c>TENANT.DELETE.REQUESTED</c> — published when the
///     delete workflow flips the tenant to <c>deleting</c>.</description></item>
///   <item><description><c>TENANT.DELETE.STEP_STARTED / STEP_COMPLETED /
///     STEP_FAILED</c> — per-step delete markers.</description></item>
///   <item><description><c>TENANT.DELETED.SUCCESS</c> — terminal delete
///     success.</description></item>
/// </list>
/// </summary>
public static class TenantLifecycleEvents
{
    public const string ProvisioningRequested = "TENANT.PROVISIONING_REQUESTED";

    public const string ProvisionStepStarted = "TENANT.PROVISION.STEP_STARTED";
    public const string ProvisionStepCompleted = "TENANT.PROVISION.STEP_COMPLETED";
    public const string ProvisionStepFailed = "TENANT.PROVISION.STEP_FAILED";

    public const string CreatedSuccess = "TENANT.CREATED.SUCCESS";
    public const string ProvisionedSuccess = "TENANT.PROVISIONED.SUCCESS";
    public const string ProvisionFailed = "TENANT.PROVISION.FAILED";

    public const string DeleteRequested = "TENANT.DELETE.REQUESTED";
    public const string DeleteStepStarted = "TENANT.DELETE.STEP_STARTED";
    public const string DeleteStepCompleted = "TENANT.DELETE.STEP_COMPLETED";
    public const string DeleteStepFailed = "TENANT.DELETE.STEP_FAILED";
    public const string DeletedSuccess = "TENANT.DELETED.SUCCESS";
    public const string DeleteCancelled = "TENANT.DELETE_CANCELLED";

    /// <summary>
    /// Build a <see cref="PlatformEvent"/> with the well-known shape used
    /// by the lifecycle workflows: <c>TenantId</c> populated, JSON tag
    /// blob with workflow correlation, JSON metadata stamped with the
    /// system event source.
    ///
    /// <para>Tags carry <c>step</c> and <c>attempt</c> so the partial
    /// unique step-dedup index swallows replays of the same step on the
    /// same attempt without duplicate rows. <paramref name="extraTags"/>
    /// merges into the base tag blob.</para>
    /// </summary>
    public static PlatformEvent BuildEvent(
        string type,
        Guid tenantId,
        string? step = null,
        int? attempt = null,
        Guid? userId = null,
        IReadOnlyDictionary<string, string?>? extraTags = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("type must be supplied", nameof(type));

        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
        };
        if (step is not null) tags["step"] = step;
        if (attempt is not null) tags["attempt"] = attempt.Value.ToString();
        if (userId is not null) tags["userId"] = userId.Value.ToString("D");
        if (extraTags is not null)
        {
            foreach (var kv in extraTags)
            {
                if (kv.Value is null) continue;
                tags[kv.Key] = kv.Value;
            }
        }

        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            UserId = userId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
        };
    }
}
