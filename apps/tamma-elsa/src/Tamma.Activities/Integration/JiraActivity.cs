using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

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
    private readonly TammaApiClient? _apiClient;

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

    [Input(Description = "Tenant id (GUID string) for JIRA credential resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public JiraActivity() { }

    /// <summary>
    /// Story 38 (Phase 2, Batch C) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no JIRA credential: every JIRA op routes through the JIRA-mediation endpoints via
    /// <see cref="TammaApiClient"/>, where the credential lives.
    /// </summary>
    public JiraActivity(
        ILogger<JiraActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Execute the JIRA operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var action = Action.Get(context);
        var ticketId = TicketId.Get(context);
        var status = Status.GetOrDefault(context);
        var comment = Comment.GetOrDefault(context);
        var prUrl = PullRequestUrl.GetOrDefault(context);
        var customFields = CustomFields.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Executing JIRA action {Action} on ticket {TicketId}",
            action, ticketId);

        try
        {
            JiraOperationResult result = action switch
            {
                JiraAction.GetTicket => await GetTicket(apiClient, ticketId, correlationId, tenantId, ct),
                JiraAction.UpdateStatus => await UpdateStatus(apiClient, ticketId, status!, correlationId, tenantId, ct),
                JiraAction.AddComment => await AddComment(apiClient, ticketId, comment!, correlationId, tenantId, ct),
                JiraAction.LinkPR => await LinkPullRequest(apiClient, ticketId, prUrl!, correlationId, tenantId, ct),
                JiraAction.UpdateFields => await UpdateCustomFields(apiClient, ticketId, customFields!, correlationId, tenantId, ct),
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

    private static async Task<JiraOperationResult> GetTicket(
        TammaApiClient apiClient, string ticketId, string correlationId, string? tenantId, CancellationToken ct)
    {
        var response = await apiClient.GetJiraTicketAsync(ticketId, correlationId, tenantId, ct);

        // A null / failed response (mediation unavailable / platform error) is an UNEXPECTED
        // failure — throw so the outer catch surfaces "Operation failed" (mirrors the composite
        // GetJiraTicketAsync which threw on error, distinct from a found-but-null "not found").
        if (response is null || !response.Success)
            throw new InvalidOperationException(
                response?.FailureReason ?? "jira mediation endpoint unavailable");

        var ticket = GitMediationMapping.ToJiraTicket(response.Ticket);
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

    private static async Task<JiraOperationResult> UpdateStatus(
        TammaApiClient apiClient, string ticketId, string newStatus, string correlationId, string? tenantId, CancellationToken ct)
    {
        var result = GitMediationMapping.ToJiraTicketResult(
            await apiClient.UpdateJiraTicketAsync(ticketId, new JiraUpdateTicketRequest
            {
                Status = newStatus,
                CorrelationId = correlationId,
            }, tenantId, ct));

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

    private static async Task<JiraOperationResult> AddComment(
        TammaApiClient apiClient, string ticketId, string comment, string correlationId, string? tenantId, CancellationToken ct)
    {
        var result = GitMediationMapping.ToJiraTicketResult(
            await apiClient.UpdateJiraTicketAsync(ticketId, new JiraUpdateTicketRequest
            {
                Comment = comment,
                CorrelationId = correlationId,
            }, tenantId, ct));

        return new JiraOperationResult
        {
            Success = result.Success,
            Message = result.Success
                ? $"Added comment to ticket {ticketId}"
                : result.Error,
            TicketKey = result.TicketKey
        };
    }

    private static async Task<JiraOperationResult> LinkPullRequest(
        TammaApiClient apiClient, string ticketId, string prUrl, string correlationId, string? tenantId, CancellationToken ct)
    {
        var comment = $"**Pull Request Linked**\n\nPR: {prUrl}\n\n_Linked automatically by Tamma Mentorship System_";

        var result = GitMediationMapping.ToJiraTicketResult(
            await apiClient.UpdateJiraTicketAsync(ticketId, new JiraUpdateTicketRequest
            {
                Comment = comment,
                CorrelationId = correlationId,
            }, tenantId, ct));

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

    private static async Task<JiraOperationResult> UpdateCustomFields(
        TammaApiClient apiClient, string ticketId, Dictionary<string, object> fields, string correlationId, string? tenantId, CancellationToken ct)
    {
        var result = GitMediationMapping.ToJiraTicketResult(
            await apiClient.UpdateJiraTicketAsync(ticketId, new JiraUpdateTicketRequest
            {
                CustomFields = fields,
                CorrelationId = correlationId,
            }, tenantId, ct));

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
