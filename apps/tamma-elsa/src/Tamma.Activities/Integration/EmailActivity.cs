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
/// ELSA activity for email notifications.
/// Sends email notifications for important mentorship events.
/// </summary>
[Activity(
    "Tamma.Integration",
    "Email Notification",
    "Send email notifications for mentorship events",
    Kind = ActivityKind.Task
)]
public class EmailActivity : CodeActivity<EmailOperationResult>
{
    private readonly ILogger<EmailActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    /// <summary>Recipient email address</summary>
    [Input(Description = "Recipient email address")]
    public Input<string> To { get; set; } = default!;

    /// <summary>Email subject</summary>
    [Input(Description = "Email subject")]
    public Input<string> Subject { get; set; } = default!;

    /// <summary>Email body (HTML supported)</summary>
    [Input(Description = "Email body")]
    public Input<string> Body { get; set; } = default!;

    /// <summary>Email template to use</summary>
    [Input(Description = "Template: SessionStarted, SessionCompleted, BlockerDetected, ReviewRequired, Custom")]
    public Input<EmailTemplate> Template { get; set; } = new(EmailTemplate.Custom);

    /// <summary>Template data for variable substitution</summary>
    [Input(Description = "Template data")]
    public Input<Dictionary<string, string>?> TemplateData { get; set; } = default!;

    /// <summary>CC recipients</summary>
    [Input(Description = "CC recipients (comma-separated)")]
    public Input<string?> Cc { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for the acting scope; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmailActivity() { }

    /// <summary>
    /// Story 38 (Phase 2, Batch C) — thin-client DI constructor. No <c>IIntegrationService</c>
    /// and no SMTP/Resend credential: the send routes through the outbox-backed email-mediation
    /// endpoint via <see cref="TammaApiClient"/>, which OWNS the terminal <c>EMAIL.*</c> audit
    /// event — this activity never emits its own.
    /// </summary>
    public EmailActivity(
        ILogger<EmailActivity> logger,
        TammaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Execute the email operation
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var to = To.Get(context);
        var subject = Subject.Get(context);
        var body = Body.Get(context);
        var template = Template.Get(context);
        var templateData = TemplateData.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));
        var correlationId = context.WorkflowExecutionContext.Id;
        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var ct = context.CancellationToken;

        _logger?.LogInformation(
            "Sending email to {To} with subject: {Subject}",
            to, subject);

        try
        {
            // Apply template if specified
            var finalBody = template != EmailTemplate.Custom
                ? ApplyTemplate(template, body, templateData)
                : body;

            // Route through the outbox-backed mediation endpoint (the API owns the EMAIL.*
            // audit event; this activity emits none). A null / success:false envelope is an
            // unexpected send failure — throw so the outer catch reports Success=false, exactly
            // as the composite's void SendEmailAsync did when it threw.
            var response = await apiClient.SendEmailAsync(new EmailSendRequest
            {
                To = to,
                Subject = subject,
                Body = finalBody,
                CorrelationId = correlationId,
            }, tenantId, ct);

            if (response is null || !response.Success)
                throw new InvalidOperationException(
                    response?.FailureReason ?? "email mediation endpoint unavailable");

            _logger?.LogInformation("Email sent successfully to {To}", to);

            context.SetResult(new EmailOperationResult
            {
                Success = true,
                Message = $"Email sent to {to}",
                Recipient = to
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send email to {To}", to);
            context.SetResult(new EmailOperationResult
            {
                Success = false,
                Message = $"Failed to send email: {ex.Message}",
                Recipient = to
            });
        }
    }

    private string ApplyTemplate(EmailTemplate template, string body, Dictionary<string, string>? data)
    {
        var baseTemplate = template switch
        {
            EmailTemplate.SessionStarted => @"
<html>
<body style='font-family: Arial, sans-serif;'>
    <h2>Mentorship Session Started</h2>
    <p>A new mentorship session has been started.</p>
    {{CONTENT}}
    <hr>
    <p style='color: #666; font-size: 12px;'>This is an automated message from Tamma Mentorship System.</p>
</body>
</html>",

            EmailTemplate.SessionCompleted => @"
<html>
<body style='font-family: Arial, sans-serif;'>
    <h2>Congratulations! Session Completed</h2>
    <p>Your mentorship session has been successfully completed.</p>
    {{CONTENT}}
    <hr>
    <p style='color: #666; font-size: 12px;'>This is an automated message from Tamma Mentorship System.</p>
</body>
</html>",

            EmailTemplate.BlockerDetected => @"
<html>
<body style='font-family: Arial, sans-serif;'>
    <h2>Attention Required: Blocker Detected</h2>
    <p>A blocker has been detected that may need your attention.</p>
    {{CONTENT}}
    <hr>
    <p style='color: #666; font-size: 12px;'>This is an automated message from Tamma Mentorship System.</p>
</body>
</html>",

            EmailTemplate.ReviewRequired => @"
<html>
<body style='font-family: Arial, sans-serif;'>
    <h2>Code Review Required</h2>
    <p>A pull request is ready for your review.</p>
    {{CONTENT}}
    <hr>
    <p style='color: #666; font-size: 12px;'>This is an automated message from Tamma Mentorship System.</p>
</body>
</html>",

            _ => "{{CONTENT}}"
        };

        var result = baseTemplate.Replace("{{CONTENT}}", body);

        // Apply template data substitution
        if (data != null)
        {
            foreach (var (key, value) in data)
            {
                result = result.Replace($"{{{{{key}}}}}", value);
            }
        }

        return result;
    }
}

/// <summary>
/// Email templates available
/// </summary>
public enum EmailTemplate
{
    Custom,
    SessionStarted,
    SessionCompleted,
    BlockerDetected,
    ReviewRequired
}

/// <summary>
/// Result of an email operation
/// </summary>
public class EmailOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Recipient { get; set; }
}
