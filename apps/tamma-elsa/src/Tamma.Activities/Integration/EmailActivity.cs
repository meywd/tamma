using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Interfaces;

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
    private readonly IIntegrationService? _integrationService;

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

    [JsonConstructor]
    public EmailActivity() { }

    public EmailActivity(
        ILogger<EmailActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
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

        _logger?.LogInformation(
            "Sending email to {To} with subject: {Subject}",
            to, subject);

        try
        {
            // Apply template if specified
            var finalBody = template != EmailTemplate.Custom
                ? ApplyTemplate(template, body, templateData)
                : body;

            await _integrationService!.SendEmailAsync(to, subject, finalBody);

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
