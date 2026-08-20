using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Crash detection for the agent run's non-durable wait.
///
/// <para><b>The failure.</b> <c>ExecuteAgentActivity</c> awaits the agent inline (dispatch
/// → discover → poll to terminal, up to ~35 minutes) with no bookmark and nothing written
/// to the workflow store in between. A deploy or crash inside that window leaves the cycle
/// instance <c>Running</c>/<c>Executing</c> with nothing for the scheduler to resume from:
/// it never finishes, never faults, and is never reported — AND it still counts against
/// the ADL's <c>MaxConcurrent</c>, so with the default of 1 a single orphan stops the loop
/// dispatching anything ever again. A resumable bookmark around the wait is story 40-2;
/// this sweep guarantees the orphan is detected, audited and cleared meanwhile.</para>
///
/// <para>The discrimination that matters is <c>Executing</c> vs <c>Suspended</c>: a
/// workflow legitimately waiting on a human, a webhook or a timer is Suspended and must
/// never be touched, however long it waits.</para>
/// </summary>
[TestFixture]
public class OrphanedCycleRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

    [Test]
    public async Task OnlyExecutingInstances_AreEvenConsidered()
    {
        var h = new Harness();
        await h.SweepAsync();

        h.Filters.Should().NotBeEmpty();
        h.Filters.Should().OnlyContain(f =>
            f.WorkflowStatus == WorkflowStatus.Running
            && f.WorkflowSubStatus == WorkflowSubStatus.Executing,
            "a Suspended instance is waiting on a bookmark by design — sweeping it would "
            + "cancel live human gates and timers");
    }

    [Test]
    public async Task AFreshExecutingInstance_IsLeftAlone()
    {
        var h = new Harness(Instance("cycle-1", updatedAt: Now.AddMinutes(-10)));

        await h.SweepAsync();

        h.Cancelled.Should().BeEmpty("10 minutes into a 35-minute agent wait is normal");
    }

    [Test]
    public async Task AnInstanceStaleBeyondTheWindow_IsTerminated()
    {
        var h = new Harness(Instance("cycle-1", updatedAt: Now.AddMinutes(-200)));

        await h.SweepAsync();

        h.Cancelled.Should().ContainSingle().Which.Should().Be("cycle-1",
            "an orphan holds an ADL concurrency slot forever unless something clears it");
    }

    [Test]
    public async Task StalenessIsMeasuredFromTheLastWrite_NotCreation()
    {
        // Created long ago but written to recently — the cycle is alive and progressing.
        var h = new Harness(Instance("cycle-1", updatedAt: Now.AddMinutes(-5), createdAt: Now.AddDays(-2)));

        await h.SweepAsync();

        h.Cancelled.Should().BeEmpty();
    }

    [Test]
    public async Task SweepsEveryConfiguredDefinition()
    {
        var h = new Harness(Instance("cycle-1", updatedAt: Now.AddMinutes(-200)))
        {
            DefinitionIds = new[] { "single-issue-cycle", "another-cycle" },
        };

        await h.SweepAsync();

        h.Filters.Select(f => f.DefinitionId).Should()
            .BeEquivalentTo(new[] { "single-issue-cycle", "another-cycle" });
    }

    [Test]
    public void TheDefaultWindowExceedsTheLongestLegitimateAgentWait()
    {
        // ExecuteAgentActivity's TimeoutMinutes defaults to 30, plus discovery and the
        // webhook safety window (~35 total). A window at or under that would cancel
        // healthy runs, which is strictly worse than the orphan it is trying to clear.
        new OrphanedCycleRecoveryOptions().StaleAfter
            .Should().BeGreaterThan(TimeSpan.FromMinutes(35));
    }

    private static WorkflowInstance Instance(
        string id, DateTimeOffset updatedAt, DateTimeOffset? createdAt = null) => new()
        {
            Id = id,
            DefinitionId = "single-issue-cycle",
            Status = WorkflowStatus.Running,
            SubStatus = WorkflowSubStatus.Executing,
            CreatedAt = createdAt ?? updatedAt,
            UpdatedAt = updatedAt,
        };

    private sealed class Harness
    {
        private readonly WorkflowInstance[] _instances;
        public List<WorkflowInstanceFilter> Filters { get; } = new();
        public List<string> Cancelled { get; } = new();
        public string[] DefinitionIds { get; set; } = new[] { "single-issue-cycle" };

        public Harness(params WorkflowInstance[] instances) => _instances = instances;

        public async Task SweepAsync()
        {
            var store = new Mock<IWorkflowInstanceStore>();
            store.Setup(s => s.FindManyAsync(It.IsAny<WorkflowInstanceFilter>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowInstanceFilter, CancellationToken>((f, _) => Filters.Add(f))
                .ReturnsAsync((WorkflowInstanceFilter f, CancellationToken _) =>
                    _instances.Where(i => i.DefinitionId == f.DefinitionId));

            var canceller = new Mock<IWorkflowCancellationService>();
            canceller.Setup(c => c.CancelWorkflowsAsync(
                    It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<string>, CancellationToken>((ids, _) => Cancelled.AddRange(ids))
                .ReturnsAsync(1);

            var services = new ServiceCollection();
            services.AddSingleton(store.Object);
            services.AddSingleton(canceller.Object);
            await using var provider = services.BuildServiceProvider();

            var service = new OrphanedCycleRecoveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new OrphanedCycleRecoveryOptions
                {
                    StaleAfter = TimeSpan.FromMinutes(90),
                    DefinitionIds = DefinitionIds,
                }),
                new FixedTimeProvider(Now),
                NullLogger<OrphanedCycleRecoveryService>.Instance);

            await service.InvokeSweepForTestsAsync(CancellationToken.None);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
