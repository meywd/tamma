using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Integration;

/// <summary>
/// ELSA activity for JIRA integration.
/// Supports updating tickets, adding comments, and transitioning status.
/// </summary>
[Activity(
    "Tamma.Integration",
    "JIRA Integration",
    "Interact with JIRA for ticket management",
    Kind = ActivityKind.Task
)]
public class JiraActivity : CodeActivity<JiraOperationResult>
{
    private readonly ILogger<JiraActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>JIRA action to perform</summary>
    [Input(Description = "Action: GetTicket, UpdateStatus, AddComment, LinkPR")]
    public Input<JiraAction> Action { get; set; } = default!;

    /// <summary>JIRA ticket ID or key</summary>
    [Input(Description = "JIRA ticket ID or key (e.g., TAMMA-123)")]
    public Input<string> TicketId { get; set; } = default!;

    /// <summary>New status for the ticket</summary>
    [Input(Description = "New ticket status")]
    public Input<string?> Status { get; set; } = default!;

    /// <summary>Comment to add to the ticket</summary>
    [Input(Description = "Comment text")]
    public Input<string?> Comment { get; set; } = default!;

    /// <summary>PR URL to link</summary>
    [Input(Description = "Pull request URL to link")]
    public Input<string?> PullRequestUrl { get; set; } = default!;

    /// <summary>Custom fields to update</summary>
    [Input(Description = "Custom fields as key-value pairs")]
    public Input<Dictionary<string, object>?> CustomFields { get; set; } = default!;

    [JsonConstructor]
    public JiraActivity() { }

    public JiraActivity(
        ILogger<JiraActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    /// <summary>
    /// Execute the JIRA operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var ticketId = TicketId.Get(context);
        var status = Status.Get(context);
        var comment = Comment.Get(context);
        var prUrl = PullRequestUrl.Get(context);
        var customFields = CustomFields.Get(context);

        _logger?.LogInformation(
            "Executing JIRA action {Action} on ticket {TicketId}",
            action, ticketId);

        try
        {
            JiraOperationResult result = action switch
            {
                JiraAction.GetTicket => await GetTicket(ticketId),
                JiraAction.UpdateStatus => await UpdateStatus(ticketId, status!),
                JiraAction.AddComment => await AddComment(ticketId, comment!),
                JiraAction.LinkPR => await LinkPullRequest(ticketId, prUrl!),
                JiraAction.UpdateFields => await UpdateCustomFields(ticketId, customFields!),
                _ => new JiraOperationResult { Success = false, Message = $"Unknown action: {action}" }
            };

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "JIRA operation failed");
            context.SetResult(new JiraOperationResult
            {
                Success = false,
                Message = $"Operation failed: {ex.Message}"
            });
        }
    }

    private async Task<JiraOperationResult> GetTicket(string ticketId)
    {
        var ticket = await _integrationService!.GetJiraTicketAsync(ticketId);

        if (ticket == null)
        {
            return new JiraOperationResult
            {
                Success = false,
                Message = $"Ticket {ticketId} not found"
            };
        }

        return new JiraOperationResult
        {
            Success = true,
            Message = $"Retrieved ticket {ticketId}",
            TicketKey = ticket.Key,
            TicketSummary = ticket.Summary,
            TicketStatus = ticket.Status,
            TicketPriority = ticket.Priority
        };
    }

    private async Task<JiraOperationResult> UpdateStatus(string ticketId, string newStatus)
    {
        var result = await _integrationService!.UpdateJiraTicketAsync(ticketId, new JiraTicketUpdate
        {
            Status = newStatus
        });

        return new JiraOperationResult
        {
            Success = result.Success,
            Message = result.Success
                ? $"Updated ticket {ticketId} status to {newStatus}"
                : result.Error,
            TicketKey = result.TicketKey,
            TicketStatus = newStatus
        };
    }

    private async Task<JiraOperationResult> AddComment(string ticketId, string comment)
    {
        var result = await _integrationService!.UpdateJiraTicketAsync(ticketId, new JiraTicketUpdate
        {
            Comment = comment
        });

        return new JiraOperationResult
        {
            Success = result.Success,
            Message = result.Success
                ? $"Added comment to ticket {ticketId}"
                : result.Error,
            TicketKey = result.TicketKey
        };
    }

    private async Task<JiraOperationResult> LinkPullRequest(string ticketId, string prUrl)
    {
        var comment = $"**Pull Request Linked**\n\nPR: {prUrl}\n\n_Linked automatically by Tamma Mentorship System_";

        var result = await _integrationService!.UpdateJiraTicketAsync(ticketId, new JiraTicketUpdate
        {
            Comment = comment
        });

        return new JiraOperationResult
        {
            Success = result.Success,
            Message = result.Success
                ? $"Linked PR to ticket {ticketId}"
                : result.Error,
            TicketKey = result.TicketKey,
            PullRequestUrl = prUrl
        };
    }

    private async Task<JiraOperationResult> UpdateCustomFields(string ticketId, Dictionary<string, object> fields)
    {
        var result = await _integrationService!.UpdateJiraTicketAsync(ticketId, new JiraTicketUpdate
        {
            CustomFields = fields
        });

        return new JiraOperationResult
        {
            Success = result.Success,
            Message = result.Success
                ? $"Updated custom fields on ticket {ticketId}"
                : result.Error,
            TicketKey = result.TicketKey
        };
    }
}

/// <summary>
/// JIRA actions available
/// </summary>
public enum JiraAction
{
    GetTicket,
    UpdateStatus,
    AddComment,
    LinkPR,
    UpdateFields
}

/// <summary>
/// Result of a JIRA operation
/// </summary>
public class JiraOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? TicketKey { get; set; }
    public string? TicketSummary { get; set; }
    public string? TicketStatus { get; set; }
    public string? TicketPriority { get; set; }
    public string? PullRequestUrl { get; set; }
}
