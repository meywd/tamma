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
/// to a suspended monitor. A single <see cref="AgentWebhookSignalKey"/>
/// expands to up to three dictionary keys so the webhook side (which has
/// the run id but not the session id) and the monitor side (which has the
/// session id but may not yet have the run id) can always find each other:
/// <list type="bullet">
///   <item><c>run:{repo}:{runId}</c> — preferred match; written by the
///   webhook and read by the monitor once it knows the run id.</item>
///   <item><c>branch:{repo}:{branch}</c> — branch-only alias; lets the
///   webhook match a pre-discovery waiter without knowing the session id.</item>
///   <item><c>branch:{repo}:{branch}:{sessionId}</c> — session-scoped alias;
///   disambiguates multiple concurrent dispatches on the same branch by
///   the same installation.</item>
/// </list>
/// </summary>
public sealed record AgentWebhookSignalKey(string Repository, string? HeadBranch, string? SessionId, long? WorkflowRunId)
{
    /// <summary>
    /// Canonical string form for the run-id path.
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

    /// <summary>
    /// All dictionary keys this logical identifier should publish to. The
    /// monitor registers waiters on every form it knows; the webhook
    /// receiver tries each form until one matches.
    /// </summary>
    internal IEnumerable<string> ExpandKeys()
    {
        var repo = (Repository ?? string.Empty).ToLowerInvariant();
        if (WorkflowRunId is not null)
        {
            yield return $"run:{repo}:{WorkflowRunId.Value}";
        }
        if (!string.IsNullOrEmpty(HeadBranch))
        {
            var branch = HeadBranch!.ToLowerInvariant();
            yield return $"branch:{repo}:{branch}";
            if (!string.IsNullOrEmpty(SessionId))
            {
                yield return $"branch:{repo}:{branch}:{SessionId}";
            }
        }
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
        var aliases = key.ExpandKeys().Distinct().ToArray();
        if (aliases.Length == 0)
        {
            _logger?.LogWarning("Webhook-signal wait requested with an empty key — rejecting");
            return null;
        }

        var waiter = new Waiter();
        var registered = new List<string>(aliases.Length);

        foreach (var alias in aliases)
        {
            if (_waiters.TryAdd(alias, waiter))
            {
                registered.Add(alias);
            }
            else
            {
                // Defensive guard: another in-flight monitor already owns
                // this alias. Skip it but keep any aliases we did register
                // — the webhook has more than one way to match.
                _logger?.LogDebug(
                    "Webhook-signal alias {Alias} already taken — skipping", alias);
            }
        }

        if (registered.Count == 0)
        {
            _logger?.LogWarning(
                "Webhook-signal wait could not register any of {Count} aliases (duplicate keys)",
                aliases.Length);
            return null;
        }

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
            foreach (var alias in registered)
            {
                _waiters.TryRemove(alias, out _);
            }
        }
    }

    public bool PublishSignal(AgentWebhookSignalKey key, AgentWebhookSignal signal)
    {
        // Try every expansion of the publish key (run-id, branch, branch+session).
        // Order matters: run-id first to prefer the exact-match path.
        var tried = key.ExpandKeys().Distinct().ToList();

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
