using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The <c>platform-task:*</c> plane of the Action Catalog (Story 43-2 AC7): one
/// member per <c>IPlatformTaskHandler</c> implementation, keyed by its
/// <c>TaskType</c> wire. Re-derived from the tree on 2026-07-27: 9 types match
/// <c>: IPlatformTaskHandler</c> of which one is the registry itself
/// (<c>PlatformTaskHandlerRegistry</c> implements <c>IPlatformTaskHandlerRegistry</c>)
/// — <b>8</b> genuine handlers, matching the design's figure.
///
/// <para>
/// The wire strings are the EXISTING persisted task-type vocabulary — each is
/// declared as a constant on another type (e.g. <c>RetireScheduler.TaskType</c>),
/// and <c>Tamma.Core</c> cannot reference <c>Tamma.Api</c> (it has zero project
/// references), so the values are restated here and pinned byte-for-byte against
/// the real constants by
/// <c>Tamma.Activities.Tests/Actions/PlatformTaskCatalogSweepTests</c>, which
/// also composes with <c>PlatformTaskHandlerRegistry</c>'s ctor throw on
/// duplicate task types.
/// </para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<PlatformTaskKind>))]
public enum PlatformTaskKind
{
    /// <summary><c>RetireSecretVersionTaskHandler</c> — <c>RetireScheduler.TaskType</c>.</summary>
    [Wire("RETIRE_SECRET_VERSION")] RetireSecretVersion,

    /// <summary><c>ActivateScheduledPlanTaskHandler</c> — <c>ActivateScheduledPlanTaskPayload.TaskType</c>.</summary>
    [Wire("plan.activate_scheduled")] ActivateScheduledPlan,

    /// <summary><c>MoveTenantTaskHandler</c> — <c>MoveTenantTaskPayload.TaskType</c>.</summary>
    [Wire("tenant.move")] MoveTenant,

    /// <summary><c>CranlProvisionPlatformTaskHandler</c> — <c>CranlTenantProviderV2.ProvisioningTaskType</c>.</summary>
    [Wire("provisioning.tenant")] ProvisionTenant,

    /// <summary><c>ProvisionTenantV2TaskHandler</c> — <c>ProvisionTenantV2TaskPayload.TaskType</c> (the V2 saga).</summary>
    [Wire("provisioning.tenant.v2")] ProvisionTenantV2,

    /// <summary><c>CranlDeprovisionPlatformTaskHandler</c> — <c>CranlTenantProviderV2.DeprovisioningTaskType</c>.</summary>
    [Wire("provisioning.tenant.deprovision")] DeprovisionTenant,

    /// <summary><c>BillingWebhookFollowupTaskHandler</c> — <c>BillingWebhookEventTypes.FollowupTaskType</c>.</summary>
    [Wire("billing.webhook.followup")] BillingWebhookFollowup,

    /// <summary><c>CreateBillingCustomerTaskHandler</c> — <c>CreateBillingCustomerTaskHandler.TaskTypeName</c>.</summary>
    [Wire("billing.customer.create")] CreateBillingCustomer,
}

/// <summary><see cref="PlatformTaskKind"/> wire helper.</summary>
public static class PlatformTaskKindExtensions
{
    /// <summary>The canonical wire string for <paramref name="kind"/>.</summary>
    public static string ToWire(this PlatformTaskKind kind) => EnumWire<PlatformTaskKind>.ToWire(kind);
}
