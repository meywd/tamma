using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.Design.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Design;

/// <summary>
/// Story 3.7 — delivers the generated design proposal to the reviewer. When a repository +
/// issue number are supplied the proposal is posted to the issue as a comment via the
/// MEDIATED git seam (<see cref="TammaApiClient.UpdateIssueStatusAsync"/> →
/// <c>PATCH /api/v1/git/{repo}/issues/{n}</c>); the per-tenant git token is resolved and
/// used server-side and NEVER travels to the engine (TAMMA001 — the engine holds no git
/// credential). When no issue coordinates are supplied the activity falls back to "api"
/// mode: the proposal is already durable in workflow state / on the approval bookmark, so a
/// reviewer can decide via the resume API.
///
/// <para>Fail-soft (mirrors <c>DeliverClarifyingQuestionsActivity</c>): a delivery failure
/// returns a <c>Success=false</c> <see cref="DesignDeliveryResult"/> rather than throwing,
/// so the workflow still arms the approval bookmark (the reviewer can still decide via API)
/// and records a truthful <c>DESIGN.PROPOSAL.DELIVERED</c> audit row.</para>
/// </summary>
[Activity(
    "Tamma.Design",
    "Deliver Design Proposal",
    "Post the design proposal to the issue via the mediated git seam, or surface via workflow state",
    Kind = ActivityKind.Task
)]
public class DeliverDesignProposalActivity : CodeActivity<DesignDeliveryResult>
{
    private readonly ILogger<DeliverDesignProposalActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Design session id")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Issue / requirement id the design is for")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Repository slug (owner/repo) for the mediated issue-comment post; empty → api mode")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Issue number for the mediated issue-comment post; <= 0 → api mode")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Generated design proposal (JSON DesignProposal)")]
    public Input<string> ProposalJson { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for the acting scope; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public DeliverDesignProposalActivity() { }

    public DeliverDesignProposalActivity(
        ILogger<DeliverDesignProposalActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var repository = Repository.GetOrDefault(context);
        var issueNumber = IssueNumber.Get(context);
        var proposalJson = ProposalJson.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var ct = context.CancellationToken;

        var message = FormatProposalMessage(proposalJson, sessionId);

        // api mode — no issue coordinates: the proposal is already durable in workflow state
        // / on the approval bookmark, surfaced to a reviewer via the resume API.
        if (string.IsNullOrWhiteSpace(repository) || issueNumber <= 0)
        {
            _logger?.LogInformation(
                "API delivery mode: design proposal surfaced in workflow state for session {SessionId}",
                sessionId);
            context.SetResult(new DesignDeliveryResult
            {
                Success = true,
                Channel = "api",
                Message = "Design proposal available via workflow state / resume API",
                DeliveredAt = DateTime.UtcNow,
            });
            return;
        }

        try
        {
            var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
            var response = await apiClient.UpdateIssueStatusAsync(
                repository,
                issueNumber,
                new GitUpdateIssueRequest { Body = message, CorrelationId = correlationId },
                tenantId,
                ct).ConfigureAwait(false);

            var success = response is { Success: true };
            _logger?.LogInformation(
                "Delivered design proposal to {Repo}#{Issue} for session {SessionId} (success={Success})",
                repository, issueNumber, sessionId, success);

            context.SetResult(new DesignDeliveryResult
            {
                Success = success,
                Channel = "issue-comment",
                Message = success
                    ? $"Design proposal posted to {repository}#{issueNumber}"
                    : $"Delivery failed: {response?.FailureReason ?? "git mediation endpoint unavailable"}",
                DeliveredAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to deliver design proposal for session {SessionId}", sessionId);
            context.SetResult(new DesignDeliveryResult
            {
                Success = false,
                Channel = "issue-comment",
                Message = $"Delivery failed: {ex.Message}",
                DeliveredAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Format the design proposal into a human-readable review comment body. Pure —
    /// exposed for unit testing.</summary>
    public static string FormatProposalMessage(string? proposalJson, Guid sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Tamma: Design Proposal for Review**");
        sb.AppendLine();

        try
        {
            var proposal = JsonSerializer.Deserialize<Tamma.Core.Documents.Types.Design>(
                proposalJson ?? "", Tamma.Core.Documents.DocumentJson.Options);
            if (proposal is not null && !string.IsNullOrWhiteSpace(proposal.Summary))
            {
                sb.AppendLine("**Summary**");
                sb.AppendLine(proposal.Summary);
                sb.AppendLine();

                if (proposal.Alternatives.Count > 0)
                {
                    sb.AppendLine("**Alternatives considered**");
                    for (var i = 0; i < proposal.Alternatives.Count; i++)
                    {
                        var alt = proposal.Alternatives[i];
                        sb.AppendLine($"{i + 1}. **{alt.Name}** — {alt.Tradeoffs}");
                    }
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(proposal.Recommendation))
                {
                    sb.AppendLine("**Recommendation**");
                    sb.AppendLine(proposal.Recommendation);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(proposal.ConstraintEvaluation))
                {
                    sb.AppendLine("**Constraint evaluation**");
                    sb.AppendLine(proposal.ConstraintEvaluation);
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine(proposalJson);
            }
        }
        catch
        {
            sb.AppendLine(proposalJson);
        }

        sb.AppendLine("---");
        sb.AppendLine($"_Reply by approving or rejecting this design. Design session: {sessionId}_");
        return sb.ToString();
    }
}
