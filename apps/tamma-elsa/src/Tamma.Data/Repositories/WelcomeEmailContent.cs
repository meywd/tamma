using System.Net;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 28-5 AC5 — renders the welcome-email subject/body for the
/// control-plane outbox row inserted by <c>QueueWelcomeEmailActivity</c>.
/// Lives in <c>Tamma.Data</c> (not <c>Tamma.Api</c>'s <c>EmailTemplates</c>)
/// because the Elsa activities project references <c>Tamma.Data</c> but not
/// <c>Tamma.Api</c>. Copy mirrors <c>EmailTemplates.WelcomeEmail</c> so the
/// rendered mail is identical regardless of which path enqueues it.
/// </summary>
public static class WelcomeEmailContent
{
    public const string Template = "welcome";

    public static (string Subject, string Html, string Text) Render(string tenantName)
    {
        var encodedTenant = WebUtility.HtmlEncode(tenantName);

        var html = $$"""
            <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
              <h2>Welcome to {{encodedTenant}}</h2>
              <p>Your Tamma account is ready. You're signed in to <strong>{{encodedTenant}}</strong> — head to the dashboard to invite your team and kick off your first workflow.</p>
              <p style="color: #999; font-size: 14px;">Need help? Reply to this email and we'll get back to you.</p>
            </div>
            """;

        var text = $"""
            Welcome to Tamma! Your account is ready and you're signed in to {tenantName}.

            Head to the dashboard to invite your team and kick off your first workflow.
            """;

        return ($"Welcome to Tamma - {tenantName}", html, text);
    }
}
