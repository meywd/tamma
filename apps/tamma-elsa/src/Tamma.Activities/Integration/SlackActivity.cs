using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Integration;

/// <summary>
/// ELSA activity for Slack communication.
/// Supports sending messages to channels and direct messages to users.
/// </summary>
[Activity(
    "Tamma.Integration",
    "Slack Communication",
    "Send messages via Slack to channels or users",
    Kind = ActivityKind.Task
)]
public class SlackActivity : CodeActivity<SlackOperationResult>
{
    private readonly ILogger<SlackActivity>? _logger;
    private readonly IIntegrationService? _integrationService;
    private readonly IMentorshipSessionRepository? _repository;

    /// <summary>Slack action to perform</summary>
    [Input(Description = "Action: SendChannel, SendDirect, SendAssessment, SendGuidance, SendNotification")]
    public Input<SlackAction> Action { get; set; } = default!;

    /// <summary>Target channel name (for channel messages)</summary>
    [Input(Description = "Channel name")]
    public Input<string?> Channel { get; set; } = default!;

    /// <summary>Target user ID (for direct messages)</summary>
    [Input(Description = "User Slack ID")]
    public Input<string?> UserId { get; set; } = default!;

    /// <summary>Message content</summary>
    [Input(Description = "Message content")]
    public Input<string> Message { get; set; } = default!;

    /// <summary>Session ID for context</summary>
    [Input(Description = "Session ID")]
    public Input<Guid?> SessionId { get; set; } = default!;

    /// <summary>Message type for formatting</summary>
    [Input(Description = "Message type: Info, Warning, Success, Error")]
    public Input<MessageType> MessageType { get; set; } = new(Integration.MessageType.Info);

    [JsonConstructor]
    public SlackActivity() { }

    public SlackActivity(
        ILogger<SlackActivity> logger,
        IIntegrationService integrationService,
        IMentorshipSessionRepository repository)
    {
        _logger = logger;
        _integrationService = integrationService;
        _repository = repository;
    }

    /// <summary>
    /// Execute the Slack operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var channel = Channel.Get(context);
        var userId = UserId.Get(context);
        var message = Message.Get(context);
        var sessionId = SessionId.Get(context);
        var messageType = MessageType.Get(context);

        _logger?.LogInformation(
            "Executing Slack action {Action}",
            action);

        try
        {
            var formattedMessage = FormatMessage(message, messageType);

            SlackOperationResult result = action switch
            {
                SlackAction.SendChannel => await SendToChannel(channel!, formattedMessage),
                SlackAction.SendDirect => await SendDirectMessage(userId!, formattedMessage),
                SlackAction.SendAssessment => await SendAssessmentRequest(userId!, message, sessionId),
                SlackAction.SendGuidance => await SendGuidanceMessage(userId!, message, sessionId),
                SlackAction.SendNotification => await SendNotification(userId!, channel, formattedMessage),
                _ => new SlackOperationResult { Success = false, Message = $"Unknown action: {action}" }
            };

            // Log the event if session is provided
            if (sessionId.HasValue && result.Success)
            {
                await _repository!.LogEventAsync(new Core.Entities.MentorshipEvent
                {
                    SessionId = sessionId.Value,
                    EventType = Core.Entities.EventTypes.Info
                });
            }

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Slack operation failed");
            context.SetResult(new SlackOperationResult
            {
                Success = false,
                Message = $"Operation failed: {ex.Message}"
            });
        }
    }

    private string FormatMessage(string message, MessageType type)
    {
        var emoji = type switch
        {
            Integration.MessageType.Info => ":information_source:",
            Integration.MessageType.Warning => ":warning:",
            Integration.MessageType.Success => ":white_check_mark:",
            Integration.MessageType.Error => ":x:",
            Integration.MessageType.Celebration => ":tada:",
            _ => ""
        };

        return $"{emoji} {message}";
    }

    private async Task<SlackOperationResult> SendToChannel(string channel, string message)
    {
        await _integrationService!.SendSlackMessageAsync(channel, message);
        return new SlackOperationResult
        {
            Success = true,
            Message = $"Message sent to #{channel}",
            Destination = channel
        };
    }

    private async Task<SlackOperationResult> SendDirectMessage(string userId, string message)
    {
        await _integrationService!.SendSlackDirectMessageAsync(userId, message);
        return new SlackOperationResult
        {
            Success = true,
            Message = $"DM sent to @{userId}",
            Destination = userId
        };
    }

    private async Task<SlackOperationResult> SendAssessmentRequest(
        string userId, string assessmentContent, Guid? sessionId)
    {
        var formattedMessage = $@"**Tamma Assessment Request** :clipboard:

{assessmentContent}

Please respond to this message with your answers.
_This assessment will help me understand your current understanding and provide better guidance._";

        await _integrationService!.SendSlackDirectMessageAsync(userId, formattedMessage);

        return new SlackOperationResult
        {
            Success = true,
            Message = "Assessment request sent",
            Destination = userId,
            WaitingForResponse = true
        };
    }

    private async Task<SlackOperationResult> SendGuidanceMessage(
        string userId, string guidanceContent, Guid? sessionId)
    {
        var formattedMessage = $@"**Tamma Guidance** :bulb:

{guidanceContent}

_Reply if you have questions or need more help!_";

        await _integrationService!.SendSlackDirectMessageAsync(userId, formattedMessage);

        return new SlackOperationResult
        {
            Success = true,
            Message = "Guidance sent",
            Destination = userId
        };
    }

    private async Task<SlackOperationResult> SendNotification(
        string? userId, string? channel, string message)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            await _integrationService!.SendSlackDirectMessageAsync(userId, message);
        }

        if (!string.IsNullOrEmpty(channel))
        {
            await _integrationService!.SendSlackMessageAsync(channel, message);
        }

        return new SlackOperationResult
        {
            Success = true,
            Message = "Notification sent",
            Destination = userId ?? channel ?? "unknown"
        };
    }
}

/// <summary>
/// Slack actions available
/// </summary>
public enum SlackAction
{
    SendChannel,
    SendDirect,
    SendAssessment,
    SendGuidance,
    SendNotification
}

/// <summary>
/// Message type for formatting
/// </summary>
public enum MessageType
{
    Info,
    Warning,
    Success,
    Error,
    Celebration
}

/// <summary>
/// Result of a Slack operation
/// </summary>
public class SlackOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Destination { get; set; }
    public bool WaitingForResponse { get; set; }
}
