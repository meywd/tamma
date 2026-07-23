using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.Clarify.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Clarify;

/// <summary>
/// Story 3.5 — delivers the generated clarifying questions to the stakeholder. When
/// a repository + issue number are supplied the questions are posted to the issue as a
/// comment via the MEDIATED git seam (<see cref="TammaApiClient.UpdateIssueStatusAsync"/>
/// → <c>PATCH /api/v1/git/{repo}/issues/{n}</c>); the per-tenant git token is resolved
/// and used server-side and NEVER travels to the engine (TAMMA001 — the engine holds no
/// git credential). When no issue coordinates are supplied the activity falls back to
/// "api" mode: the questions are already durable in workflow state / on the answer
/// bookmark, so a stakeholder can answer via the resume API.
///
/// <para>Fail-soft (mirrors <c>DeliverQuestionsActivity</c>): a delivery failure returns
/// a <c>Success=false</c> <see cref="ClarifyDeliveryResult"/> rather than throwing, so
/// the workflow still arms the answer bookmark (the human can still respond via API) and
/// records a truthful <c>CLARIFY.QUESTIONS.DELIVERED</c> audit row.</para>
/// </summary>
[Activity(
    "Tamma.Clarify",
    "Deliver Clarifying Questions",
    "Post clarifying questions to the issue via the mediated git seam, or surface via workflow state",
    Kind = ActivityKind.Task
)]
public class DeliverClarifyingQuestionsActivity : CodeActivity<ClarifyDeliveryResult>
{
    private readonly ILogger<DeliverClarifyingQuestionsActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Clarify session id")]
    public Input<Guid> SessionId { get; set; } = default!;

    [Input(Description = "Issue / requirement id the ambiguity is about")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Repository slug (owner/repo) for the mediated issue-comment post; empty → api mode")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Issue number for the mediated issue-comment post; <= 0 → api mode")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Generated clarifying questions (JSON ClarifyQuestionSet)")]
    public Input<string> QuestionsJson { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for the acting scope; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public DeliverClarifyingQuestionsActivity() { }

    public DeliverClarifyingQuestionsActivity(
        ILogger<DeliverClarifyingQuestionsActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var repository = Repository.Get(context);
        var issueNumber = IssueNumber.Get(context);
        var questionsJson = QuestionsJson.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var ct = context.CancellationToken;

        var message = FormatQuestionMessage(questionsJson, sessionId);

        // api mode — no issue coordinates: the questions are already durable in workflow
        // state / on the answer bookmark, surfaced to a stakeholder via the resume API.
        if (string.IsNullOrWhiteSpace(repository) || issueNumber <= 0)
        {
            _logger?.LogInformation(
                "API delivery mode: clarifying questions surfaced in workflow state for session {SessionId}",
                sessionId);
            context.SetResult(new ClarifyDeliveryResult
            {
                Success = true,
                Channel = "api",
                Message = "Questions available via workflow state / resume API",
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
                "Delivered clarifying questions to {Repo}#{Issue} for session {SessionId} (success={Success})",
                repository, issueNumber, sessionId, success);

            context.SetResult(new ClarifyDeliveryResult
            {
                Success = success,
                Channel = "issue-comment",
                Message = success
                    ? $"Questions posted to {repository}#{issueNumber}"
                    : $"Delivery failed: {response?.FailureReason ?? "git mediation endpoint unavailable"}",
                DeliveredAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to deliver clarifying questions for session {SessionId}", sessionId);
            context.SetResult(new ClarifyDeliveryResult
            {
                Success = false,
                Channel = "issue-comment",
                Message = $"Delivery failed: {ex.Message}",
                DeliveredAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Format the clarifying-question set into a human-readable comment body from the
    /// typed 39-3 <see cref="Tamma.Core.Documents.Types.Clarification"/> questions-phase payload
    /// (Story 39-13 D9). Pure — exposed for unit testing; fail-soft (falls back to raw json).</summary>
    public static string FormatQuestionMessage(string? questionsJson, Guid sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Tamma: Clarifying Questions**");
        sb.AppendLine();
        sb.AppendLine("This requirement has some ambiguities. Please answer the questions below so development can proceed with a clear, unambiguous spec.");
        sb.AppendLine();

        try
        {
            var doc = JsonSerializer.Deserialize<Tamma.Core.Documents.Types.Clarification>(
                questionsJson ?? "", Tamma.Core.Documents.DocumentJson.Options);
            if (doc is not null && doc.Questions.Count > 0)
            {
                for (var i = 0; i < doc.Questions.Count; i++)
                    sb.AppendLine($"{i + 1}. {doc.Questions[i]}");
            }
            else
            {
                sb.AppendLine(questionsJson);
            }
        }
        catch
        {
            sb.AppendLine(questionsJson);
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"_Reply with your answers. Clarify session: {sessionId}_");
        return sb.ToString();
    }
}
