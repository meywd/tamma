using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Email;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Verifies the built-in transactional email templates substitute their
/// inputs into both the HTML and plain-text variants and produce a non-empty
/// subject line. These templates are what Story 18-1 (verification) and
/// Story 18-6 (password reset) send to real users, so their output is the
/// user-visible contract for those flows.
/// </summary>
[TestFixture]
public class EmailTemplatesTests
{
    // ─── VerificationEmail ────────────────────────────────────────────────

    [Test]
    public void VerificationEmail_SubstitutesRecipientAndUrl()
    {
        var msg = EmailTemplates.VerificationEmail(
            recipient: "alice@example.com",
            verificationUrl: "https://dash.tamma.dev/verify?token=abc123");

        msg.To.Should().Be("alice@example.com");
        msg.Subject.Should().NotBeNullOrWhiteSpace();
        msg.Html.Should().Contain("https://dash.tamma.dev/verify?token=abc123");
        msg.Text.Should().Contain("https://dash.tamma.dev/verify?token=abc123");
        // Body must reference verification so the user knows what the link does.
        msg.Text.ToLowerInvariant().Should().Contain("verify");
    }

    [Test]
    public void VerificationEmail_EscapesHtmlInRecipient()
    {
        var msg = EmailTemplates.VerificationEmail(
            recipient: "<script>alert(1)</script>@example.com",
            verificationUrl: "https://dash.tamma.dev/verify?token=t");

        msg.Html.Should().NotContain("<script>alert(1)</script>");
    }

    // ─── PasswordResetEmail ───────────────────────────────────────────────

    [Test]
    public void PasswordResetEmail_SubstitutesRecipientAndUrl()
    {
        var msg = EmailTemplates.PasswordResetEmail(
            recipient: "bob@example.com",
            resetUrl: "https://dash.tamma.dev/reset-password?token=xyz");

        msg.To.Should().Be("bob@example.com");
        msg.Subject.Should().NotBeNullOrWhiteSpace();
        msg.Html.Should().Contain("https://dash.tamma.dev/reset-password?token=xyz");
        msg.Text.Should().Contain("https://dash.tamma.dev/reset-password?token=xyz");
        msg.Text.ToLowerInvariant().Should().Contain("reset");
    }

    // ─── WelcomeEmail ─────────────────────────────────────────────────────

    [Test]
    public void WelcomeEmail_SubstitutesRecipientAndTenant()
    {
        var msg = EmailTemplates.WelcomeEmail(
            recipient: "carol@example.com",
            tenantName: "Acme Engineering");

        msg.To.Should().Be("carol@example.com");
        msg.Subject.Should().NotBeNullOrWhiteSpace();
        msg.Html.Should().Contain("Acme Engineering");
        msg.Text.Should().Contain("Acme Engineering");
    }

    [Test]
    public void WelcomeEmail_EscapesHtmlInTenantName()
    {
        var msg = EmailTemplates.WelcomeEmail(
            recipient: "carol@example.com",
            tenantName: "<b>Evil & Co</b>");

        msg.Html.Should().NotContain("<b>Evil & Co</b>");
        msg.Html.Should().Contain("&amp;");
    }
}
