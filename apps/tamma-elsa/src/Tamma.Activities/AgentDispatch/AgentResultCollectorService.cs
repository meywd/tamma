using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 38-2 (Class-C cutover) — the thin <see cref="IAgentResultCollectorService"/>.
/// The multi-read aggregation (result artifact download+parse, PR-for-head lookup,
/// base...head compare, check runs) moved SERVER-SIDE into
/// <c>Tamma.Api.Services.AgentDispatch.ActionsResultAggregator</c> behind
/// <c>GET /api/v1/agent-dispatch/{owner}/{repo}/runs/{id}/results</c>. This engine
/// service now folds the monitor's terminal state into one request and maps the
/// single aggregated response back to <see cref="AgentExecutionResult"/> — holding
/// no <see cref="IGitHubActionsClient"/>, no Octokit, and no Actions token.
///
/// <para>The pure JSON parsing + clamp caps that used to live here are preserved in
/// <see cref="AgentResultArtifactParser"/> (now called server-side by the aggregator).</para>
/// </summary>
public sealed class AgentResultCollectorService : IAgentResultCollectorService
{
    /// <summary>
    /// Story 38-2 (review finding 2) — sentinel prefix stamped onto the
    /// <see cref="AgentExecutionResult.ErrorMessage"/> of a MEDIATION/authorization
    /// collect failure (null response / guard 403 / transport). It lets the thin
    /// <c>CollectAgentResultsActivity</c> tell a hard "collection never ran" failure
    /// (→ Failed) apart from a genuine "ran but couldn't read full git state" Partial.
    /// </summary>
    public const string CollectionUnavailableMarker = "agent result collection unavailable";

    private readonly TammaApiClient _api;
    private readonly ILogger<AgentResultCollectorService>? _logger;

    public AgentResultCollectorService(
        TammaApiClient api,
        ILogger<AgentResultCollectorService>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger;
    }

    public async Task<AgentExecutionResult> CollectAsync(
        AgentExecutionRequest request,
        AgentMonitorResult monitorResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(monitorResult);

        if (!TryParseRepository(request.Repository))
        {
            return AgentExecutionResult.Failed(
                $"Invalid repository format '{request.Repository}'",
                request.AgentProvider,
                ExecutionModeNames.GitHubActions);
        }

        var apiRequest = new CollectAgentRunApiRequest
        {
            BranchName = request.BranchName,
            Conclusion = monitorResult.Conclusion ?? "unknown",
            AgentProvider = request.AgentProvider,
            DurationSeconds = monitorResult.DurationSeconds,
            CorrelationId = request.SessionId,
        };

        var response = await _api.CollectAgentResultsAsync(
            request.Repository, monitorResult.WorkflowRunId, apiRequest, request.TenantId?.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return MapResponse(response, request.AgentProvider);
    }

    /// <summary>
    /// Story 38-2 (AC5) — pure map of the aggregated wire response → the
    /// <see cref="AgentExecutionResult"/> the surrounding workflow consumes.
    ///
    /// <para>A MEDIATION/authorization failure — a null response (guard 403 / auth 401 /
    /// transport 5xx nulled the body) OR a <c>success:false</c> envelope — means the
    /// collect call itself did not run. It is tagged with
    /// <see cref="CollectionUnavailableMarker"/> so <c>CollectAgentResultsActivity</c>
    /// routes it to the <b>Failed</b> outcome (NOT the soft Partial): a revoked mid-run
    /// authorization must not surface as a phantom-partial a downstream branch could
    /// proceed on.</para>
    ///
    /// <para>On a real aggregated body, <c>Success</c> is the AGENT's task success (from
    /// <c>agentSuccess</c>), matching the pre-cutover contract — a run that completed but
    /// couldn't read full git state stays a genuine Partial.</para>
    /// </summary>
    public static AgentExecutionResult MapResponse(AgentRunResultsApiResponse? response, string fallbackProvider)
    {
        if (response is null || !response.Success)
        {
            var reason = response?.FailureReason;
            var message = string.IsNullOrWhiteSpace(reason)
                ? CollectionUnavailableMarker
                : $"{CollectionUnavailableMarker}: {reason}";
            return AgentExecutionResult.Failed(message, fallbackProvider, ExecutionModeNames.GitHubActions);
        }

        return new AgentExecutionResult(
            Success: response.AgentSuccess,
            PrNumber: response.PrNumber,
            PrUrl: response.PrUrl,
            CommitSha: response.CommitSha ?? string.Empty,
            FilesChanged: response.FilesChanged?.ToArray() ?? Array.Empty<string>(),
            CommitsCount: response.CommitsCount,
            ChecksPassed: response.ChecksPassed,
            TokensUsed: response.TokensUsed,
            DurationSeconds: response.DurationSeconds,
            ErrorMessage: response.ErrorMessage,
            AgentLogSummary: response.AgentLogSummary,
            AgentProvider: string.IsNullOrEmpty(response.AgentProvider) ? fallbackProvider : response.AgentProvider!,
            AgentVersion: response.AgentVersion,
            ExecutionMode: ExecutionModeNames.GitHubActions);
    }

    private static bool TryParseRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]);
    }
}
