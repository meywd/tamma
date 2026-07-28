using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Api.Services.Secrets.Rotation;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// REFLECTION SWEEP binding the catalog's <c>platform-task:*</c> plane to the
/// real <see cref="IPlatformTaskHandler"/> implementations (the Core-side keyset
/// test is self-referential and says so). Two bindings compose:
/// the WIRES are pinned byte-for-byte against the REAL task-type constants
/// (which Tamma.Core cannot reference — it has zero project references), and the
/// TYPE SET is swept by reflection so a new handler class fails until catalogued.
/// <c>PlatformTaskHandlerRegistry</c>'s ctor already throws on duplicate task
/// types; this composes with that guarantee rather than duplicating it.
/// </summary>
[TestFixture]
public class PlatformTaskCatalogSweepTests
{
    private static IReadOnlyList<Type> HandlerTypes() =>
        typeof(IPlatformTaskHandler).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IPlatformTaskHandler).IsAssignableFrom(t))
            .ToArray();

    [Test]
    public void Catalog_wires_are_byte_identical_to_the_real_task_type_constants()
    {
        // Compile-time references to the constants each handler sources its
        // TaskType from (43-2 C4) — renaming any constant breaks this build.
        var realTaskTypes = new[]
        {
            RetireScheduler.TaskType,
            ActivateScheduledPlanTaskPayload.TaskType,
            MoveTenantTaskPayload.TaskType,
            CranlTenantProviderV2.ProvisioningTaskType,
            ProvisionTenantV2TaskPayload.TaskType,
            CranlTenantProviderV2.DeprovisioningTaskType,
            BillingWebhookEventTypes.FollowupTaskType,
            CreateBillingCustomerTaskHandler.TaskTypeName,
        };

        var catalogued = ActionCatalog.ByKey.Keys
            .Where(k => k.Ns == ActionNamespace.PlatformTask)
            .Select(k => k.Key);

        catalogued.Should().BeEquivalentTo(realTaskTypes,
            "platform-task wires ARE the persisted task-type vocabulary; the catalog restates them "
            + "only because Tamma.Core cannot reference Tamma.Api, and this pin keeps the restatement honest");
    }

    [Test]
    public void Every_handler_class_and_every_catalogued_task_match_bidirectionally()
    {
        // SiteKeys on the platform-task plane carry the handler types' full names,
        // so the sweep matches classes without instantiating them (handlers take
        // scoped DI dependencies).
        var handlerTypeNames = HandlerTypes().Select(t => t.FullName!).ToArray();

        var cataloguedSites = ActionCatalog.All
            .Where(d => d.Key.Ns == ActionNamespace.PlatformTask)
            .Select(d => d.SiteKey)
            .ToArray();

        cataloguedSites.Should().BeEquivalentTo(handlerTypeNames,
            "a new IPlatformTaskHandler must be catalogued and a deleted one un-catalogued");
    }

    [Test]
    public void The_handler_count_is_pinned_at_8()
    {
        // 43-2 C4: a naive ': IPlatformTaskHandler' grep returns 9 — one is the
        // registry (implements IPlatformTaskHandlerRegistry). Reflection sees the
        // true 8.
        HandlerTypes().Should().HaveCount(8);
    }
}
