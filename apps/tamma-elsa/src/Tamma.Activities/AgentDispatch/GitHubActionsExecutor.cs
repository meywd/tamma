using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// <see cref="IAgentExecutor"/> that dispatches the agent to the user's
/// GitHub Actions runner (story 19-5 AC-3). Composes the three phase
/// services (dispatch / monitor / collect) into a single end-to-end
/// execution.
///
/// <para>Matches the "Option B" recommendation from the story: the
/// executor calls the underlying services directly rather than
/// programmatically invoking Elsa activities. The activities and the
/// executor share services, so there's one implementation of each
/// phase.</para>
/// </summary>
public sealed class GitHubActionsExecutor : IAgentExecutor
{
    public string Mode => ExecutionModeNames.GitHubActions;

    private readonly IAgentDispatchService _dispatch;
    private readonly IAgentMonitorService _monitor;
    private readonly IAgentResultCollectorService _collector;
    private readonly ILogger<GitHubActionsExecutor>? _logger;
    private readonly AgentMonitorOptions _monitorOptions;

    public GitHubActionsExecutor(
        IAgentDispatchService dispatch,
        IAgentMonitorService monitor,
        IAgentResultCollectorService collector,
        AgentMonitorOptions? monitorOptions = null,
        ILogger<GitHubActionsExecutor>? logger = null)
    {
        _dispatch = dispatch;
        _monitor = monitor;
        _collector = collector;
        _monitorOptions = monitorOptions ?? AgentMonitorOptions.Default;
        _logger = logger;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Dispatch.
        var dispatchResult = await _dispatch.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (!dispatchResult.Success)
        {
            _logger?.LogWarning(
                "GitHubActionsExecutor dispatch failed for {Repository}/{Branch}: {Error}",
                request.Repository, request.BranchName, dispatchResult.ErrorMessage);
            return AgentExecutionResult.Failed(
                dispatchResult.ErrorMessage ?? "Dispatch failed",
                request.AgentProvider,
                ExecutionModeNames.GitHubActions);
        }

        // 2. Monitor using the dispatch timestamp + request-provided timeout.
        var monitorOptions = new AgentMonitorOptions(
            PollIntervalSeconds: _monitorOptions.PollIntervalSeconds,
            TimeoutMinutes: Math.Max(_monitorOptions.TimeoutMinutes, request.TimeoutMinutes + 5),
            DiscoveryTimeoutSeconds: _monitorOptions.DiscoveryTimeoutSeconds,
            MaxConsecutiveErrors: _monitorOptions.MaxConsecutiveErrors);

        AgentMonitorResult monitorResult;
        try
        {
            monitorResult = await _monitor.MonitorAsync(
                request, dispatchResult.DispatchedAt, monitorOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Monitor step failed for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            return AgentExecutionResult.Failed(
                $"Monitor failed: {ex.Message}",
                request.AgentProvider,
                ExecutionModeNames.GitHubActions);
        }

        // 3. Collect — always run, even for non-success conclusions, so
        //    the caller still gets whatever git/PR state is available
        //    (matches story 19-4 AC-5 fallback behaviour).
        try
        {
            return await _collector.CollectAsync(request, monitorResult, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Collect step failed for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            return AgentExecutionResult.Failed(
                $"Collect failed: {ex.Message}",
                request.AgentProvider,
                ExecutionModeNames.GitHubActions);
        }
    }
}
