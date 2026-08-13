using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// ELSA activity that triggers a CI pipeline run for a given repository and branch.
/// Supports real CI integration and mock mode for testing.
///
/// <para><b>Epic 31 P3 (seam 4).</b> The real path is REPOINTED off the raw
/// <c>POST {Engine:CallbackUrl}/api/engine/trigger-ci</c> HTTP call (which
/// required a <c>workflowFile</c> this activity never sent, so it could only
/// 400) and onto the governed CI-mediation plane via
/// <see cref="TammaApiClient.TriggerTestsAsync"/>
/// (<c>POST /api/v1/ci/{owner}/{repo}/test-runs</c> — guard → per-tenant driver
/// → one DCB event, server-side). The activity's result contract is unchanged:
/// <see cref="CITriggerResult"/> with a POLLABLE <c>RunId</c> on success and
/// <c>Success=false</c> + <c>Error</c> on any failure (never a throw out of the
/// activity). <c>/api/engine/trigger-ci</c> itself stays mapped, delegating to
/// the same mediation core.</para>
/// </summary>
[Activity(
    "Tamma.Testing",
    "Trigger CI",
    "Trigger a CI/CD pipeline run for the specified repository and branch",
    Kind = ActivityKind.Task
)]
public class TriggerCIActivity : CodeActivity<CITriggerResult>
{
    private readonly ILogger<TriggerCIActivity> _logger;
    private readonly IConfiguration _configuration;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch to run CI against</summary>
    [Input(Description = "Branch name to run CI against")]
    public Input<string> Branch { get; set; } = default!;

    /// <summary>Commit SHA to target (optional)</summary>
    [Input(Description = "Specific commit SHA to target")]
    public Input<string?> CommitSha { get; set; } = default!;

    [JsonConstructor]
    public TriggerCIActivity()
    {
        _logger = null!;
        _configuration = null!;
        _apiClient = null;
    }

    public TriggerCIActivity(
        ILogger<TriggerCIActivity> logger,
        IConfiguration configuration,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _configuration = configuration;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var repository = Repository.Get(context);
        var branch = Branch.Get(context);
        var logger = _logger ?? context.GetRequiredService<ILogger<TriggerCIActivity>>();

        logger.LogInformation(
            "Triggering CI pipeline for session {SessionId}, repo {Repository}, branch {Branch}",
            sessionId, repository, branch);

        try
        {
            var configuration = _configuration ?? context.GetRequiredService<IConfiguration>();
            var useMock = configuration.GetValue<bool>("Testing:UseMock");

            CITriggerResult result;
            if (useMock)
            {
                result = SimulateCITrigger(sessionId, repository, branch);
            }
            else
            {
                result = await TriggerRealCI(context, sessionId, repository, branch, logger);
            }

            logger.LogInformation(
                "CI pipeline triggered: RunId={RunId}, Success={Success}",
                result.RunId, result.Success);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger CI pipeline for session {SessionId}", sessionId);

            context.SetResult(new CITriggerResult
            {
                Success = false,
                Error = $"Failed to trigger CI: {ex.Message}",
                TriggeredAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// The mediated real path: POST the governed CI plane's test-runs endpoint.
    /// The per-tenant git/CI credential is resolved and used SERVER-side; this
    /// activity carries none. A null response (transport failure, guard 403,
    /// token 503, governance 409) maps to a failed result — fail-closed, no throw.
    /// </summary>
    private async Task<CITriggerResult> TriggerRealCI(
        ActivityExecutionContext context, Guid sessionId, string repository, string branch,
        ILogger logger)
    {
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var repo = NormalizeRepository(repository);
        var tenantId = ResolveTenantId(context);

        var response = await apiClient.TriggerTestsAsync(
            repo,
            new LlmCall.Models.CiTriggerTestsRequest
            {
                Branch = branch,
                CorrelationId = $"ci-trigger-{sessionId:N}",
            },
            tenantId,
            context.CancellationToken);

        if (response is null)
        {
            logger.LogWarning(
                "CI mediation call returned no response for {Repository}/{Branch} — failing closed",
                repo, branch);
            return new CITriggerResult
            {
                Success = false,
                Error = "CI mediation call failed (transport, auth, or governance denial)",
                TriggeredAt = DateTime.UtcNow
            };
        }

        if (!response.Success)
        {
            // §4.3 safety net — EXACT-code match only: anything other than
            // capability_unsupported stays an ordinary error (mis-classifying
            // a real failure as "unsupported" would silently skip the CI gate).
            var unsupported = string.Equals(
                response.FailureCode, "capability_unsupported", StringComparison.Ordinal);
            return new CITriggerResult
            {
                Success = false,
                Unsupported = unsupported,
                Error = response.FailureReason ?? response.FailureCode ?? "CI trigger failed",
                TriggeredAt = DateTime.UtcNow
            };
        }

        return new CITriggerResult
        {
            Success = true,
            // The mediation plane returns a POLLABLE platform run id (P1 made
            // dispatch re-fetch the run). Falls back to a synthetic id only if
            // the platform could not surface one.
            RunId = string.IsNullOrWhiteSpace(response.TestRun?.RunId)
                ? Guid.NewGuid().ToString()
                : response.TestRun!.RunId,
            PipelineUrl = string.Empty,
            TriggeredAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Normalize a repository reference (an <c>owner/repo</c> full name or a
    /// browser URL like <c>https://github.com/owner/repo.git</c>) to the
    /// <c>owner/repo</c> shape the mediation endpoints take as two path segments.
    /// </summary>
    internal static string NormalizeRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return string.Empty;
        var value = repository.Trim().TrimEnd('/');

        var schemeIdx = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            value = value[(schemeIdx + 3)..];
            var firstSlash = value.IndexOf('/');
            value = firstSlash >= 0 ? value[(firstSlash + 1)..] : value;
        }
        else if (value.Contains(':') && value.Contains('@'))
        {
            // scp-like git remote: git@host:owner/repo.git
            value = value[(value.IndexOf(':') + 1)..];
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? $"{segments[^2]}/{segments[^1]}"
            : value;
    }

    /// <summary>Ambient tenant scope — the MediatedLlmText convention (a Guid or
    /// string workflow variable; anything else ⇒ platform scope).</summary>
    private static string? ResolveTenantId(ActivityExecutionContext context)
    {
        var raw = context.GetVariable<object?>("TenantId")
                  ?? context.GetVariable<object?>("AccountId");
        return raw switch
        {
            Guid g when g != Guid.Empty => g.ToString(),
            string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p.ToString(),
            _ => null,
        };
    }

    private static CITriggerResult SimulateCITrigger(
        Guid sessionId, string repository, string branch)
    {
        var runId = $"run-{Guid.NewGuid():N}";

        return new CITriggerResult
        {
            Success = true,
            RunId = runId,
            PipelineUrl = $"https://ci.example.com/{repository}/runs/{runId}",
            TriggeredAt = DateTime.UtcNow
        };
    }
}
