using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 19-3 AC-7 — <see cref="WebhookSignalRegistry"/> behaviour.
/// Validates the key-derivation contract so the webhook receiver and the
/// monitor service agree on which (repo, run) pair matches.
/// </summary>
[TestFixture]
public class WebhookSignalRegistryTests
{
    private static AgentWebhookSignal MakeSignal(long runId = 1_000, string conclusion = "success") =>
        new(
            WorkflowRunId: runId,
            Status: "completed",
            Conclusion: conclusion,
            WorkflowRunUrl: $"https://github.com/acme/widgets/actions/runs/{runId}",
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTime.UtcNow,
            ArtifactsUrl: $"https://api.github.com/repos/acme/widgets/actions/runs/{runId}/artifacts");

    [Test]
    public void Key_ToKey_PrefersRunId_WhenPresent()
    {
        var withRun = new AgentWebhookSignalKey("Acme/Widgets", "main", "sess_a", WorkflowRunId: 42).ToKey();
        var branchOnly = new AgentWebhookSignalKey("Acme/Widgets", "main", "sess_a", WorkflowRunId: null).ToKey();

        withRun.Should().Be("run:acme/widgets:42");
        branchOnly.Should().Be("branch:acme/widgets:main:sess_a");
    }

    [Test]
    public async Task Publish_WithRunIdKey_WakesBranchFallbackWaiter()
    {
        var registry = new WebhookSignalRegistry();
        var waitKey = new AgentWebhookSignalKey(
            Repository: "acme/widgets",
            HeadBranch: "tamma/issue-42",
            SessionId: "sess_abc",
            WorkflowRunId: null);

        var waitTask = registry.WaitForSignalAsync(waitKey, TimeSpan.FromSeconds(5));

        // Give the waiter a moment to register its entry in the dictionary.
        await Task.Delay(50);
        registry.PendingWaiterCount.Should().BeGreaterThan(0,
            "the waiter should have registered before publish");

        var publishKey = new AgentWebhookSignalKey(
            Repository: "acme/widgets",
            HeadBranch: "tamma/issue-42",
            SessionId: null,
            WorkflowRunId: 12345);

        var matched = registry.PublishSignal(publishKey, MakeSignal(12345));
        matched.Should().BeTrue("publishing by run-id must still match the branch-fallback waiter");

        var signal = await waitTask;
        signal.Should().NotBeNull();
        signal!.WorkflowRunId.Should().Be(12345);
    }

    [Test]
    public async Task Wait_ReturnsNull_OnTimeout()
    {
        var registry = new WebhookSignalRegistry();
        var key = new AgentWebhookSignalKey("acme/widgets", "branch", "sess", null);

        var result = await registry.WaitForSignalAsync(key, TimeSpan.FromMilliseconds(100));

        result.Should().BeNull();
        registry.PendingWaiterCount.Should().Be(0, "the waiter must clean up after a timeout");
    }

    [Test]
    public void Publish_WithNoWaiter_ReturnsFalse()
    {
        var registry = new WebhookSignalRegistry();
        var key = new AgentWebhookSignalKey("acme/widgets", "branch", null, WorkflowRunId: 99);

        var matched = registry.PublishSignal(key, MakeSignal(99));

        matched.Should().BeFalse("non-Tamma-dispatched workflow_runs have no waiter");
    }

    [Test]
    public async Task Wait_ReleasesEntry_OnCancellation()
    {
        var registry = new WebhookSignalRegistry();
        var key = new AgentWebhookSignalKey("acme/widgets", "branch", "sess", null);
        using var cts = new CancellationTokenSource();

        var task = registry.WaitForSignalAsync(key, TimeSpan.FromMinutes(5), cts.Token);

        await Task.Delay(50);
        registry.PendingWaiterCount.Should().BeGreaterThan(0);

        cts.Cancel();
        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        registry.PendingWaiterCount.Should().Be(0);
    }
}
