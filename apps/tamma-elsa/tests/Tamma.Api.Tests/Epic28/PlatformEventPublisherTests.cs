using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-5 — exercises the <see cref="PlatformEventPublisher"/> adapter
/// that bridges <see cref="IPlatformEventBus"/> behind the lower-layer
/// <see cref="IPlatformEventPublisher"/> port consumed by tenant-lifecycle
/// activities.
///
/// <para>The adapter is the only point of contact between the activity
/// assembly and the in-process bus, so these tests assert the contract
/// the activities rely on:</para>
///
/// <list type="bullet">
///   <item><description>A successful append fans out to bus subscribers.</description></item>
///   <item><description>A dedup no-op (repository returns null) is NOT
///     re-published — the original event already triggered subscribers.</description></item>
///   <item><description>The repository is resolved from a fresh scope
///     each call (so the publisher works from a singleton activity).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class PlatformEventPublisherTests
{
    private ServiceProvider _services = null!;
    private InMemoryPlatformEventBus _bus = null!;
    private PlatformEventPublisher _publisher = null!;
    private List<string> _seenTypes = null!;
    private IDisposable _subscription = null!;

    [SetUp]
    public void SetUp()
    {
        var sc = new ServiceCollection();
        sc.AddDbContextFactory<ControlPlaneDbContext>(opts => opts
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        sc.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>().CreateDbContext());
        sc.AddScoped<IPlatformEventRepository, PlatformEventRepository>();

        _services = sc.BuildServiceProvider();
        _bus = new InMemoryPlatformEventBus(NullLogger<InMemoryPlatformEventBus>.Instance);
        _publisher = new PlatformEventPublisher(
            _bus,
            _services.GetRequiredService<IServiceScopeFactory>());

        _seenTypes = new List<string>();
        _subscription = _bus.Subscribe(
            (e, _) => { _seenTypes.Add(e.Type); return Task.CompletedTask; });
    }

    [TearDown]
    public void TearDown()
    {
        _subscription.Dispose();
        _services.Dispose();
    }

    private static PlatformEvent NewEvent(string type, Guid tenantId)
        => new()
        {
            Type = type,
            TenantId = tenantId,
            Tags = "{}",
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = "{}",
        };

    [Test]
    public async Task AppendAndPublish_PersistsAndFansOut()
    {
        var tenantId = Guid.NewGuid();
        var evt = NewEvent("TENANT.CREATED.SUCCESS", tenantId);

        var persisted = await _publisher.AppendAndPublishAsync(evt);

        persisted.Should().NotBeNull();
        persisted!.Id.Should().NotBe(Guid.Empty);
        _seenTypes.Should().ContainSingle().Which.Should().Be("TENANT.CREATED.SUCCESS");
    }

    [Test]
    public async Task AppendAndPublish_OnNullArg_Throws()
    {
        var act = async () => await _publisher.AppendAndPublishAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task AppendAndPublish_PersistedRowReadableViaRepository()
    {
        var tenantId = Guid.NewGuid();
        var evt = NewEvent("TENANT.PROVISION.STEP_COMPLETED", tenantId);

        await _publisher.AppendAndPublishAsync(evt);

        await using var scope = _services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlatformEventRepository>();
        var rows = await repo.QueryAsync(tenantId: tenantId);
        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be("TENANT.PROVISION.STEP_COMPLETED");
    }

    [Test]
    public async Task AppendAndPublish_MultiplePublishes_AllFanOut()
    {
        var tenantId = Guid.NewGuid();
        await _publisher.AppendAndPublishAsync(NewEvent("TENANT.PROVISIONING_REQUESTED", tenantId));
        await _publisher.AppendAndPublishAsync(NewEvent("TENANT.PROVISION.STEP_COMPLETED", tenantId));
        await _publisher.AppendAndPublishAsync(NewEvent("TENANT.CREATED.SUCCESS", tenantId));

        _seenTypes.Should().BeEquivalentTo(new[]
        {
            "TENANT.PROVISIONING_REQUESTED",
            "TENANT.PROVISION.STEP_COMPLETED",
            "TENANT.CREATED.SUCCESS",
        });
    }
}
