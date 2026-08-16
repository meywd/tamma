using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.Assessment.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Assessment;

/// <summary>
/// Delivers assessment questions to the junior developer via the configured channel
/// (Slack DM, API response, or email). Includes context summary so the junior can reference it.
/// </summary>
[Activity(
    "Tamma.Assessment",
    "Deliver Questions",
    "Send assessment questions to junior via configured channel",
    Kind = ActivityKind.Task
)]
public class DeliverQuestionsActivity : CodeActivity<DeliveryResult>
{
    private readonly ILogger<DeliverQuestionsActivity>? _logger;
    private readonly TammaApiClient? _apiClient;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer ID")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Generated questions (JSON serialized QuestionSet)</summary>
    [Input(Description = "Questions to deliver (JSON)")]
    public Input<string> QuestionsJson { get; set; } = default!;

    /// <summary>Attempt number for tracking</summary>
    [Input(Description = "Assessment attempt number", DefaultValue = 1)]
    public Input<int> AttemptNumber { get; set; } = new(1);

    [Input(Description = "Tenant id (GUID string) for the acting scope; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public DeliverQuestionsActivity() { }

    /// <summary>
    /// Story 38 (Phase 2, Batch C) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no email credential: the email delivery channel routes through the outbox-backed
    /// email-mediation endpoint via <see cref="TammaApiClient"/> (the API owns the <c>EMAIL.*</c>
    /// audit event); the Slack channel already goes through the <see cref="MediatedSlack"/> seam.
    /// </summary>
    public DeliverQuestionsActivity(
        ILogger<DeliverQuestionsActivity> logger,
        TammaApiClient apiClient,
        IMentorshipSessionRepository repository,
        IConfiguration configuration)
    {
        _logger = logger;
        _apiClient = apiClient;
        _repository = repository;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var questionsJson = QuestionsJson.Get(context);
        var attemptNumber = AttemptNumber.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Delivering assessment questions for session {SessionId}, attempt {AttemptNumber}",
            sessionId, attemptNumber);

        try
        {
            var channel = _configuration?["Assessment:DeliveryChannel"] ?? "slack";
            var junior = await _repository!.GetJuniorByIdAsync(juniorId);

            var message = FormatQuestionMessage(questionsJson, sessionId, attemptNumber);

            switch (channel.ToLowerInvariant())
            {
                case "slack":
                    if (junior?.SlackId != null)
                    {
                        // Story 38-3b — enqueue the DM intent via the API seam (engine
                        // holds no Slack credential); fire-and-forget, fail-soft.
                        await MediatedSlack.QueueDirectMessageAsync(
                            context, junior.SlackId, message, "Info", "SendAssessment", context.CancellationToken);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Junior {JuniorId} has no Slack ID, falling back to API delivery",
                            juniorId);
                    }
                    break;

                case "email":
                    if (junior?.Email != null)
                    {
                        // Mediated, outbox-backed send (the API owns the EMAIL.* audit event).
                        // A null / success:false envelope is an unexpected send failure — throw
                        // so the outer catch reports delivery failure, exactly as the composite's
                        // void SendEmailAsync did when it threw.
                        var emailResponse = await apiClient.SendEmailAsync(new EmailSendRequest
                        {
                            To = junior.Email,
                            Subject = $"Tamma Assessment - Session {sessionId}",
                            Body = message,
                            CorrelationId = correlationId,
                        }, tenantId, ct);

                        if (emailResponse is null || !emailResponse.Success)
                            throw new InvalidOperationException(
                                emailResponse?.FailureReason ?? "email mediation endpoint unavailable");
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Junior {JuniorId} has no email, falling back to API delivery",
                            juniorId);
                    }
                    break;

                case "api":
                default:
                    // API mode: questions are available via workflow state / bookmark
                    _logger?.LogInformation(
                        "API delivery mode: questions stored in workflow for session {SessionId}",
                        sessionId);
                    break;
            }

            // Log the delivery event
            await _repository!.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.Info,
                Trigger = $"assessment_questions_delivered_attempt_{attemptNumber}"
            });

            context.SetResult(new DeliveryResult
            {
                Success = true,
                Channel = channel,
                Message = $"Questions delivered via {channel}",
                DeliveredAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to deliver questions for session {SessionId}", sessionId);

            context.SetResult(new DeliveryResult
            {
                Success = false,
                Channel = "error",
                Message = $"Delivery failed: {ex.Message}",
                DeliveredAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Format assessment questions into a human-readable message
    /// </summary>
    private static string FormatQuestionMessage(string questionsJson, Guid sessionId, int attemptNumber)
    {
        var sb = new StringBuilder();

        sb.AppendLine("**Tamma Assessment**");
        sb.AppendLine();

        if (attemptNumber > 1)
        {
            sb.AppendLine($"_Follow-up assessment (attempt {attemptNumber}) - please address the specific areas below._");
            sb.AppendLine();
        }

        // Try to parse questions from JSON, fall back to raw text
        try
        {
            var questionSet = System.Text.Json.JsonSerializer.Deserialize<QuestionSet>(questionsJson);
            if (questionSet != null)
            {
                if (!string.IsNullOrEmpty(questionSet.ContextSummary))
                {
                    sb.AppendLine("**Context:**");
                    sb.AppendLine(questionSet.ContextSummary);
                    sb.AppendLine();
                }

                sb.AppendLine("**Please answer the following questions to demonstrate your understanding:**");
                sb.AppendLine();

                for (var i = 0; i < questionSet.Questions.Count; i++)
                {
                    sb.AppendLine($"{i + 1}. {questionSet.Questions[i]}");
                    sb.AppendLine();
                }
            }
        }
        catch
        {
            sb.AppendLine(questionsJson);
        }

        sb.AppendLine("---");
        sb.AppendLine($"_Reply with your answers. Session: {sessionId}_");

        return sb.ToString();
    }
}
