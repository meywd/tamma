using System.Net;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Pre-baked HTML + plain-text templates for the three transactional emails
/// Tamma sends today. All string inputs are HTML-encoded before substitution
/// to prevent reflected-XSS in webmail clients.
/// </summary>
/// <remarks>
/// These templates are deliberately inline (not Razor, not file-based) so
/// they are trivially diffable, testable in-process, and safe from template
/// injection. If the volume grows past ~10 templates we should revisit —
/// consider a minimal handlebars-style renderer or Razor components.
/// </remarks>
public static class EmailTemplates
{
    /// <summary>
    /// Verification email sent at the end of Story 18-1 registration.
    /// </summary>
    /// <param name="recipient">User's email address (HTML-encoded on render).</param>
    /// <param name="verificationUrl">Full click-through URL including the raw token.</param>
    public static EmailMessage VerificationEmail(string recipient, string verificationUrl)
    {
        var encodedRecipient = WebUtility.HtmlEncode(recipient);
        var encodedUrl = WebUtility.HtmlEncode(verificationUrl);

        var html = $$"""
            <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
              <h2>Welcome to Tamma</h2>
              <p>Hi {{encodedRecipient}},</p>
              <p>Please verify your email address to activate your account:</p>
              <p>
                <a href="{{encodedUrl}}"
                   style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">
                  Verify Email
                </a>
              </p>
              <p style="color: #666;">Or paste this URL into your browser:</p>
              <p style="word-break: break-all; color: #666;">{{encodedUrl}}</p>
              <p style="color: #999; font-size: 14px;">This link expires in 24 hours. If you did not create a Tamma account, you can safely ignore this email.</p>
            </div>
            """;

        var text = $"""
            Welcome to Tamma!

            Please verify your email address by visiting:
            {verificationUrl}

            This link expires in 24 hours. If you did not create a Tamma account, you can safely ignore this email.
            """;

        return new EmailMessage(
            To: recipient,
            Subject: "Verify your Tamma email address",
            Html: html,
            Text: text);
    }

    /// <summary>
    /// Password-reset email sent by Story 18-6 <c>PasswordResetRequest</c>.
    /// </summary>
    public static EmailMessage PasswordResetEmail(string recipient, string resetUrl)
    {
        var encodedRecipient = WebUtility.HtmlEncode(recipient);
        var encodedUrl = WebUtility.HtmlEncode(resetUrl);

        var html = $$"""
            <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
              <h2>Password reset request</h2>
              <p>Hi {{encodedRecipient}},</p>
              <p>We received a request to reset your Tamma password. Click the link below to choose a new one:</p>
              <p>
                <a href="{{encodedUrl}}"
                   style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">
                  Reset password
                </a>
              </p>
              <p style="color: #666;">Or paste this URL into your browser:</p>
              <p style="word-break: break-all; color: #666;">{{encodedUrl}}</p>
              <p style="color: #999; font-size: 14px;">This link expires in 1 hour. If you did not request a password reset, you can safely ignore this email.</p>
            </div>
            """;

        var text = $"""
            Hi,

            We received a request to reset your Tamma password. Visit the link below to choose a new password:
            {resetUrl}

            This link expires in 1 hour. If you did not request a password reset, you can safely ignore this email.
            """;

        return new EmailMessage(
            To: recipient,
            Subject: "Reset your Tamma password",
            Html: html,
            Text: text);
    }

    /// <summary>
    /// Welcome email sent once a user completes email verification and is
    /// bound to their first tenant.
    /// </summary>
    public static EmailMessage WelcomeEmail(string recipient, string tenantName)
    {
        var encodedRecipient = WebUtility.HtmlEncode(recipient);
        var encodedTenant = WebUtility.HtmlEncode(tenantName);

        var html = $$"""
            <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
              <h2>Welcome to {{encodedTenant}}</h2>
              <p>Hi {{encodedRecipient}},</p>
              <p>Your Tamma account is ready. You're signed in to <strong>{{encodedTenant}}</strong> — head to the dashboard to invite your team and kick off your first workflow.</p>
              <p style="color: #999; font-size: 14px;">Need help? Reply to this email and we'll get back to you.</p>
            </div>
            """;

        var text = $"""
            Hi,

            Welcome to Tamma! Your account is ready and you're signed in to {tenantName}.

            Head to the dashboard to invite your team and kick off your first workflow.
            """;

        return new EmailMessage(
            To: recipient,
            Subject: $"Welcome to Tamma - {tenantName}",
            Html: html,
            Text: text);
    }
}
