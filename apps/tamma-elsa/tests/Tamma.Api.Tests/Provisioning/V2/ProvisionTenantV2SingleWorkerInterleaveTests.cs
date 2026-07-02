using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Phase-B I1 regression — proves a SINGLE <see cref="PlatformTaskWorker"/>
/// process can complete a v2 provision that requires an inner
/// <c>provisioning.tenant</c> task to run on the SAME queue.
///
/// <para>Before the restructure the V2 saga block-polled the provider in an
/// in-process loop, pinning the worker's one-task-at-a-time slot for the
/// whole ~30-min probe budget. On a single worker the inner task the Cranl
/// provider enqueues was never reserved, so the provision timed out — the
/// old code needed ≥2 worker processes. After the fix the saga's InitialProbe
/// is single-shot: a still-provisioning probe DEFERS (returns the row to the
/// queue with a future <c>VisibleAt</c> + throws
/// <see cref="PlatformTaskDeferredException"/>), releasing the slot so the
/// inner task runs on the next tick of the SAME worker.</para>
///
/// <para>This test wires the real
/// <see cref="ProvisionTenantV2TaskHandler"/> + <see cref="ProvisionTenantV2Workflow"/>
/// against an InMemory control-plane DB and drives
/// <see cref="PlatformTaskWorker.ProcessOnceAsync"/> tick-by-tick.</para>
/// </summary>
[TestFixture]
public sealed class ProvisionTenantV2SingleWorkerInterleaveTests
{
    private const string InnerTaskType = "provisioning.tenant";

    private sealed class InnerStubHandler : IPlatformTaskHandler
    {
        public int Calls { get; private set; }
        public string TaskType => InnerTaskType;
        public Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task SingleWorker_SagaDefers_InnerTaskRuns_ThenResumeReachesReady()
    {
        var dbName = $"v2-interleave-{Guid.NewGuid():N}";
        var tenantId = Guid.NewGuid();

        // ── Provider: enqueues ONE inner provisioning.tenant task on first
        // ProvisionAsync (like CranlTenantProviderV2), and reports AppDeploying
        // on the first probe then Ready on the second (single-shot per resume).
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueDeploying(times: 1).EnqueueReady();

        var innerHandler = new InnerStubHandler();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();
        services.AddSingleton(Mock.Of<IPlatformEventPublisher>());
        services.AddSingleton(new TenantProviderRegistry(
            new ITenantInfrastructureProvider[] { new NullTenantProvider(), fake }));
        // Small probe interval so the deferred saga's VisibleAt window is short;
        // large enough that tick-2 runs before it re-opens.
        services.AddScoped(sp => new ProvisionTenantV2Workflow(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<TenantProviderRegistry>(),
            sp.GetRequiredService<IPlatformEventPublisher>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<ProvisionTenantV2Workflow>.Instance)
        {
            ProbeInterval = TimeSpan.FromMinutes(5),
            ProbeTimeout = TimeSpan.FromMinutes(30),
        });
        // Both platform-task handlers share the ONE queue / ONE worker.
        services.AddSingleton<IPlatformTaskHandler>(innerHandler);
        services.AddScoped<IPlatformTaskHandler, ProvisionTenantV2TaskHandler>();
        services.AddScoped<IPlatformTaskHandlerRegistry, PlatformTaskHandlerRegistry>();

        await using var sp = services.BuildServiceProvider();

        // Provider enqueues the inner task through its own scope on first call.
        var innerEnqueued = false;
        fake.OnProvision = async (_, _, _) =>
        {
            if (!innerEnqueued)
            {
                innerEnqueued = true;
                await using var scope = sp.CreateAsyncScope();
                var r = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
                await r.EnqueueAsync(new PlatformQueuedTask
                {
                    Type = InnerTaskType,
                    TenantId = tenantId,
                    Payload = "{}",
                });
            }
            return new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Pending, "queued", null, DateTimeOffset.UtcNow),
                new Dictionary<string, string>());
        };

        // Seed the tenant + enqueue the outer saga task (older ⇒ reserved first).
        Guid sagaId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = "acme-" + Guid.NewGuid().ToString("N")[..6],
                ProvisioningState = "none",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var repo = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
            var saga = await repo.EnqueueAsync(new PlatformQueuedTask
            {
                Type = ProvisionTenantV2TaskPayload.TaskType,
                TenantId = tenantId,
                Payload = JsonSerializer.Serialize(new ProvisionTenantV2TaskPayload
                {
                    TenantId = tenantId,
                    ProviderKey = "cranl",
                    Topology = ProvisioningTopology.DedicatedCompute,
                    Region = "germany-1",
                }),
            });
            sagaId = saga.Id;
        }

        var worker = new PlatformTaskWorker(
            sp,
            Options.Create(new PlatformTaskWorkerOptions
            {
                RunOnStartup = false,
                WorkerId = "single-worker",
                MaxRetries = 5,
            }),
            TimeProvider.System,
            NullLogger<PlatformTaskWorker>.Instance);

        // ── Tick 1: the ONLY reservable row is the saga. It provisions
        // (enqueuing the inner task), probes AppDeploying, and DEFERS —
        // releasing the slot. Nothing is completed/failed.
        (await worker.ProcessOnceAsync(default)).Should().BeTrue();
        innerHandler.Calls.Should().Be(0, "the inner task hasn't been reserved yet");
        var afterTick1 = await GetTaskAsync(sp, sagaId);
        afterTick1!.Status.Should().Be("pending", "the saga deferred itself back to pending");
        afterTick1.RetryCount.Should().Be(0, "a defer does not burn the retry budget");
        afterTick1.VisibleAt.Should().NotBeNull("the saga is invisible until its probe interval elapses");

        // ── Tick 2: the saga's VisibleAt is in the future, so the worker
        // reserves the INNER task instead and runs it. THIS is the proof the
        // single worker interleaved because the saga released the slot.
        (await worker.ProcessOnceAsync(default)).Should().BeTrue();
        innerHandler.Calls.Should().Be(1, "the single worker ran the inner task while the saga was deferred");
        (await GetTaskAsync(sp, sagaId))!.Status.Should().Be("pending", "the saga is still deferred");

        // Simulate the ProbeInterval window elapsing (deterministic — no real
        // wait): open the saga's VisibleAt so it is reservable again.
        await using (var scope = sp.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
            await repo.DeferAsync(sagaId, DateTime.UtcNow.AddSeconds(-1));
        }

        // ── Tick 3: the saga resumes, re-runs steps 1-6 (idempotent), probes
        // Ready, activates, and completes.
        (await worker.ProcessOnceAsync(default)).Should().BeTrue();

        var finalSaga = await GetTaskAsync(sp, sagaId);
        finalSaga!.Status.Should().Be("completed", "the resumed saga reached Ready and the worker marked it completed");

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var tenant = await db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
            tenant.ProvisioningState.Should().Be("ready");
        }

        fake.ProvisionCalls.Should().HaveCount(2, "provision runs once per saga invocation (idempotent on resume)");
        fake.DeprovisionCalls.Should().BeEmpty("happy path runs no compensation");
    }

    private static async Task<PlatformQueuedTask?> GetTaskAsync(IServiceProvider sp, Guid id)
    {
        await using var scope = sp.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
        return await repo.GetAsync(id);
    }
}
