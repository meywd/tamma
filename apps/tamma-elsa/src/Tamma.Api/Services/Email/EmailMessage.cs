namespace Tamma.Api.Services.Email;

/// <summary>
/// A single transactional email to be delivered by <see cref="IEmailService"/>.
/// </summary>
/// <param name="To">RFC-5322 recipient address.</param>
/// <param name="Subject">Subject line — must be non-empty.</param>
/// <param name="Html">HTML variant of the body (for rendering-capable clients).</param>
/// <param name="Text">Plain-text fallback (required, never null).</param>
/// <param name="From">Optional override of the system "from" address. When null
/// the SMTP implementation uses the <c>Email:From</c> configuration value.</param>
public record EmailMessage(
    string To,
    string Subject,
    string Html,
    string Text,
    string? From = null);
