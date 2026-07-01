using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 — hand-rolled subclass of the concrete <see cref="TammaApiClient"/>
/// (its agent-dispatch methods are <c>virtual</c>) used by the thin phase-service
/// tests. Lets tests seed mediated responses and assert the engine-side
/// orchestration (dispatch mapping, the monitor poll/discover loop, collect
/// mapping) WITHOUT any real HTTP. The base ctor gets a throwaway HttpClient +
/// NullLogger — no request ever leaves.
/// </summary>
internal sealed class FakeTammaApiClient : TammaApiClient
{
    public FakeTammaApiClient()
        : base(new HttpClient(), NullLogger<TammaApiClient>.Instance, null, null)
    {
    }

    // ── Dispatch ──────────────────────────────────────────────────────────
    public Func<string, AgentDispatchRunApiRequest, string?, AgentDispatchRunApiResponse?>? OnDispatch { get; set; }
    public List<DispatchCall> DispatchCalls { get; } = new();

    public override Task<AgentDispatchRunApiResponse?> DispatchAgentRunAsync(
        string repo, AgentDispatchRunApiRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        DispatchCalls.Add(new DispatchCall(repo, request, tenantId));
        return Task.FromResult(OnDispatch?.Invoke(repo, request, tenantId));
    }

    // ── Discover (the monitor's discovery phase) ──────────────────────────
    public Queue<AgentRunStatusApiResponse?> DiscoverQueue { get; } = new();
    public AgentRunStatusApiResponse? DefaultDiscover { get; set; }
    public int DiscoverCalls { get; private set; }

    public override Task<AgentRunStatusApiResponse?> DiscoverAgentRunAsync(
        string repo, string branch, DateTime createdAfter, string? correlationId = null,
        string? tenantId = null, CancellationToken ct = default)
    {
        DiscoverCalls++;
        return Task.FromResult(DiscoverQueue.Count > 0 ? DiscoverQueue.Dequeue() : DefaultDiscover);
    }

    // ── Poll one run by id ────────────────────────────────────────────────
    public Queue<AgentRunStatusApiResponse?> GetRunQueue { get; } = new();
    public AgentRunStatusApiResponse? DefaultGetRun { get; set; }
    public int GetRunCalls { get; private set; }

    public override Task<AgentRunStatusApiResponse?> GetAgentRunAsync(
        string repo, long runId, string? correlationId = null, string? tenantId = null, CancellationToken ct = default)
    {
        GetRunCalls++;
        return Task.FromResult(GetRunQueue.Count > 0 ? GetRunQueue.Dequeue() : DefaultGetRun);
    }

    // ── Collect ───────────────────────────────────────────────────────────
    public Func<string, long, CollectAgentRunApiRequest, string?, AgentRunResultsApiResponse?>? OnCollect { get; set; }
    public List<CollectCall> CollectCalls { get; } = new();

    public override Task<AgentRunResultsApiResponse?> CollectAgentResultsAsync(
        string repo, long runId, CollectAgentRunApiRequest request, string? tenantId = null, CancellationToken ct = default)
    {
        CollectCalls.Add(new CollectCall(repo, runId, request, tenantId));
        return Task.FromResult(OnCollect?.Invoke(repo, runId, request, tenantId));
    }

    // ── Installation resolution (webhook wait-key scoping) ────────────────
    public long? InstallationId { get; set; } = 100L;
    public int InstallationCalls { get; private set; }

    public override Task<AgentInstallationApiResponse?> ResolveAgentInstallationIdAsync(
        string repo, string? tenantId = null, CancellationToken ct = default)
    {
        InstallationCalls++;
        return Task.FromResult<AgentInstallationApiResponse?>(
            new AgentInstallationApiResponse { Success = true, InstallationId = InstallationId });
    }
}

internal sealed record DispatchCall(string Repo, AgentDispatchRunApiRequest Request, string? TenantId);
internal sealed record CollectCall(string Repo, long RunId, CollectAgentRunApiRequest Request, string? TenantId);

/// <summary>
/// Indirection over <c>Task.Delay</c> so monitor tests skip real time
/// (relocated from the deleted FakeGitHubActionsClient.cs).
/// </summary>
internal sealed class ImmediateDelayProvider : IDelayProvider
{
    public int CallCount { get; private set; }
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
