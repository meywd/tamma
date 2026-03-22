using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Blocker.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Bookmark-based activity that compiles a full context dump and escalates to a senior developer.
/// The workflow suspends and waits for the senior to respond via API or Slack.
/// </summary>
[Activity(
    "Tamma.Blocker",
    "Escalate To Senior",
    "Compile context and notify senior developer for blocker resolution",
    Kind = ActivityKind.Task
)]
public class EscalateToSeniorActivity : Activity
{
    private readonly ILogger<EscalateToSeniorActivity>? _logger;
    private readonly IIntegrationService? _integrationService;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story ID</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Junior developer ID</summary>
    [Input(Description = "Junior developer identifier")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>Blocker type classification</summary>
    [Input(Description = "Classified blocker type")]
    public Input<string> BlockerType { get; set; } = default!;

    /// <summary>Blocker severity</summary>
    [Input(Description = "Blocker severity")]
    public Input<string> BlockerSeverity { get; set; } = default!;

    /// <summary>Diagnosis details from AI</summary>
    [Input(Description = "Diagnosis details")]
    public Input<string> DiagnosisDetails { get; set; } = default!;

    /// <summary>Previous resolution attempts</summary>
    [Input(Description = "Previous resolution attempts")]
    public Input<List<string>> PreviousAttempts { get; set; } = default!;

    /// <summary>Aggregated signals (optional)</summary>
    [Input(Description = "Aggregated signals from collection")]
    public Input<AggregatedSignals?> Signals { get; set; } = default!;

    /// <summary>Whether the escalation was resolved by the senior</summary>
    [Output(Description = "Whether the senior resolved the blocker")]
    public Output<bool> Resolved { get; set; } = default!;

    /// <summary>Senior's response</summary>
    [Output(Description = "Senior's response")]
    public Output<string?> SeniorResponse { get; set; } = default!;

    [JsonConstructor]
    public EscalateToSeniorActivity() { }

    public EscalateToSeniorActivity(
        ILogger<EscalateToSeniorActivity> logger,
        IIntegrationService integrationService,
        IConfiguration configuration)
    {
        _logger = logger;
        _integrationService = integrationService;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var juniorId = JuniorId.Get(context);
        var blockerType = BlockerType.Get(context);
        var severity = BlockerSeverity.Get(context);
        var diagnosisDetails = DiagnosisDetails.Get(context);
        var previousAttempts = PreviousAttempts.Get(context) ?? new List<string>();
        var signals = Signals.Get(context);

        _logger?.LogInformation(
            "Escalating blocker to senior: Session={SessionId}, Type={BlockerType}, Severity={Severity}",
            sessionId, blockerType, severity);

        // Compile context dump
        var escalationContext = new EscalationContext
        {
            SessionId = sessionId,
            StoryId = storyId,
            JuniorId = juniorId,
            BlockerType = Enum.TryParse<BlockerCategory>(blockerType, out var bt)
                ? bt
                : BlockerCategory.TechnicalKnowledgeGap,
            Severity = Enum.TryParse<BlockerDiagnosisSeverity>(severity, out var sev)
                ? sev
                : BlockerDiagnosisSeverity.High,
            DiagnosisDetails = diagnosisDetails,
            PreviousAttempts = previousAttempts,
            Signals = signals,
            EscalatedAt = DateTime.UtcNow
        };

        // Notify senior via configured channel
        await NotifySenior(escalationContext);

        var payload = new EscalationPayload
        {
            SessionId = sessionId,
            StoryId = storyId,
            JuniorId = juniorId
        };

        // Create bookmark — workflow suspends and waits for senior response
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = $"blocker-escalation-{sessionId}",
            Payload = payload,
            Callback = OnResumeAsync,
            AutoBurn = true
        });
    }

    private async Task NotifySenior(EscalationContext escalation)
    {
        var escalationChannel = _configuration?["BlockerDiagnosis:EscalationChannel"] ?? "slack";
        var seniorChannel = _configuration?["BlockerDiagnosis:SeniorNotificationChannel"] ?? "#mentorship-escalations";

        var message = $@"**Tamma: Blocker Escalation**

A junior developer needs senior help with a blocker that could not be resolved through automated guidance.

*Session:* {escalation.SessionId}
*Story:* {escalation.StoryId}
*Junior:* {escalation.JuniorId}
*Blocker Type:* {escalation.BlockerType}
*Severity:* {escalation.Severity}

*Diagnosis:*
{escalation.DiagnosisDetails}

*Previous Attempts ({escalation.PreviousAttempts.Count}):*
{string.Join("\n", escalation.PreviousAttempts.Select((a, i) => $"{i + 1}. {a}"))}

Please respond to this escalation via the Tamma API or reply in this thread.";

        try
        {
            if (escalationChannel == "slack" && _integrationService != null)
            {
                await _integrationService.SendSlackMessageAsync(seniorChannel, message);
            }

            _logger?.LogInformation("Senior notification sent for session {SessionId}", escalation.SessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send senior notification — bookmark still created");
        }
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var resolved = input.TryGetValue("Resolved", out var r) && r is true;
        var seniorResponse = input.TryGetValue("SeniorResponse", out var sr)
            ? sr?.ToString()
            : null;

        _logger?.LogInformation(
            "Senior escalation resumed: Resolved={Resolved}",
            resolved);

        context.Set(Resolved, resolved);
        context.Set(SeniorResponse, seniorResponse);

        await context.CompleteActivityAsync();
    }
}
