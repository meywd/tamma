using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 19-3 AC-7 — in-process signal plane that lets the GitHub webhook
/// receiver wake a blocked <see cref="IAgentMonitorService"/> call without
/// polling.
///
/// <para>The monitor service calls <see cref="WaitForSignalAsync"/> with a
/// stable key derived from the dispatch (see
/// <see cref="AgentWebhookSignalKey"/>). When the webhook handler observes
/// a matching <c>workflow_run.completed</c> payload it calls
/// <see cref="PublishSignal"/>; any awaiter(s) complete with the payload
/// and the monitor returns immediately.</para>
///
/// <para>Scope: single process. Both the webhook receiver and the monitor
/// run inside Tamma.Api for SaaS deployments, so this is sufficient for
/// AC-7's "reduce GitHub API rate pressure" goal. For distributed
/// deployments where the ElsaServer process runs the activity, webhook
/// mode falls back to poll (Auto) or fails (explicit Webhook) — see
/// <see cref="AgentMonitorMode"/> for the resolution rules.</para>
///
/// <para>The registry is intentionally a plain CAS-backed ConcurrentDictionary
/// rather than an IBookmarkStore — it avoids any Elsa workflow-runtime
/// coupling so the service (and its tests) stay provider-agnostic.</para>
/// </summary>
public interface IWebhookSignalRegistry
{
    /// <summary>
    /// Register a wait on the given key. The returned task completes when
    /// <see cref="PublishSignal"/> is called with a matching key, when
    /// <paramref name="timeout"/> elapses, or when
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <returns>
    /// The signal payload on a successful wake, <c>null</c> on timeout.
    /// </returns>
    Task<AgentWebhookSignal?> WaitForSignalAsync(
        AgentWebhookSignalKey key,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wake the pending waiter matching <paramref name="key"/> (if any) and
    /// hand it <paramref name="signal"/>. Returns <c>true</c> when a waiter
    /// was matched; <c>false</c> when no bookmark is outstanding (not every
    /// workflow_run is a Tamma-dispatched one).
    /// </summary>
    bool PublishSignal(AgentWebhookSignalKey key, AgentWebhookSignal signal);

    /// <summary>
    /// Count of waiters currently parked. Used for diagnostics and tests.
    /// </summary>
    int PendingWaiterCount { get; }
}

/// <summary>
/// Stable identifier for matching a <c>workflow_run.completed</c> webhook
/// to a suspended monitor. The service pre-computes two keys on the wait
/// side and the webhook receiver picks whichever one it has:
/// <list type="bullet">
///   <item>
///     <c>repo + runId</c> — the preferred path. The monitor registers
///     this key only after the discovery phase has resolved the run id.
///   </item>
///   <item>
///     <c>repo + branch + sessionId</c> — used before discovery completes
///     (or when the webhook lands first) so we can correlate by the
///     pre-dispatch fields. The receiver matches on this when the payload
///     carries a matching <c>head_branch</c>.
///   </item>
/// </list>
/// </summary>
public sealed record AgentWebhookSignalKey(string Repository, string? HeadBranch, string? SessionId, long? WorkflowRunId)
{
    /// <summary>
    /// Canonical string form for use as a dictionary key. Casing is
    /// normalised so webhook-side lookups survive GitHub's mixed-case repo
    /// slugs.
    /// </summary>
    public string ToKey()
    {
        var repo = (Repository ?? string.Empty).ToLowerInvariant();
        if (WorkflowRunId is not null)
        {
            return $"run:{repo}:{WorkflowRunId.Value}";
        }

        var branch = (HeadBranch ?? string.Empty).ToLowerInvariant();
        var session = SessionId ?? string.Empty;
        return $"branch:{repo}:{branch}:{session}";
    }
}

/// <summary>
/// Payload carried on a webhook-mode wake-up. Mirrors the subset of the
/// <c>workflow_run.completed</c> payload that the monitor needs to satisfy
/// its output contract.
/// </summary>
public sealed record AgentWebhookSignal(
    long WorkflowRunId,
    string Status,
    string Conclusion,
    string WorkflowRunUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string ArtifactsUrl);

/// <summary>
/// Default <see cref="IWebhookSignalRegistry"/>. Singleton-safe. Thread-safe.
/// </summary>
public sealed class WebhookSignalRegistry : IWebhookSignalRegistry
{
    private readonly ConcurrentDictionary<string, Waiter> _waiters = new();
    private readonly ILogger<WebhookSignalRegistry>? _logger;

    public WebhookSignalRegistry(ILogger<WebhookSignalRegistry>? logger = null)
    {
        _logger = logger;
    }

    public int PendingWaiterCount => _waiters.Count;

    public async Task<AgentWebhookSignal?> WaitForSignalAsync(
        AgentWebhookSignalKey key,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var primary = key.ToKey();
        // Always also register the branch+session fallback key so a
        // webhook that arrives before discovery completes can still match.
        var fallback = new AgentWebhookSignalKey(
            key.Repository, key.HeadBranch, key.SessionId, WorkflowRunId: null).ToKey();

        var waiter = new Waiter();

        // De-dupe: multiple waiters on the same key would split the signal
        // unpredictably. In practice the monitor only registers one bookmark
        // per request so this is a defensive guard.
        if (!_waiters.TryAdd(primary, waiter))
        {
            _logger?.LogWarning(
                "Webhook-signal duplicate waiter on key {Key} — rejecting", primary);
            return null;
        }

        var hasFallback = primary != fallback && _waiters.TryAdd(fallback, waiter);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                return await waiter.Tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout (linked CTS canceled by CancelAfter, outer ct untouched).
                return null;
            }
        }
        finally
        {
            _waiters.TryRemove(primary, out _);
            if (hasFallback)
            {
                _waiters.TryRemove(fallback, out _);
            }
        }
    }

    public bool PublishSignal(AgentWebhookSignalKey key, AgentWebhookSignal signal)
    {
        // Try run-id path first, then the branch+session fallback.
        var tried = new List<string> { key.ToKey() };
        if (key.WorkflowRunId is not null && key.HeadBranch is not null)
        {
            tried.Add(new AgentWebhookSignalKey(
                key.Repository, key.HeadBranch, key.SessionId, WorkflowRunId: null).ToKey());
        }

        foreach (var candidate in tried)
        {
            if (_waiters.TryGetValue(candidate, out var waiter))
            {
                if (waiter.Tcs.TrySetResult(signal))
                {
                    _logger?.LogInformation(
                        "Webhook-signal published for key {Key} (run={RunId} conclusion={Conclusion})",
                        candidate, signal.WorkflowRunId, signal.Conclusion);
                    return true;
                }
            }
        }

        _logger?.LogDebug(
            "Webhook-signal unmatched (tried {TriedCount} keys) — no pending monitor for this run",
            tried.Count);
        return false;
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<AgentWebhookSignal> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
