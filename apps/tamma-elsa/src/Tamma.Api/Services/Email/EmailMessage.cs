namespace Tamma.Api.Services.Email;

/// <summary>
/// A single transactional email to be delivered by <see cref="IEmailService"/>.
///
/// <para>
/// The optional <see cref="Template"/>, <see cref="TenantId"/>, and
/// <see cref="UserId"/> properties are propagated onto the domain events
/// (<see cref="EmailEventTypes"/>) that every <see cref="IEmailService"/>
/// implementation emits. They are never included in log lines and never
/// logged alongside the recipient — they are safe, non-PII correlation keys.
/// </para>
/// </summary>
/// <param name="To">RFC-5322 recipient address. Stored but never logged.</param>
/// <param name="Subject">Subject line. Stored but never logged.</param>
/// <param name="Html">HTML variant of the body (for rendering-capable clients).</param>
/// <param name="Text">Plain-text fallback (required, never null).</param>
/// <param name="From">Optional override of the system "from" address. When null
/// the SMTP implementation uses the <c>Email:From</c> configuration value.</param>
/// <param name="Template">Template key used to compose this message
/// (e.g. <c>verification</c>, <c>password-reset</c>). Surfaces on the emitted
/// <c>EMAIL.QUEUED.SUCCESS</c> event tag.</param>
/// <param name="TenantId">Owning tenant; propagated to the event stream.</param>
/// <param name="UserId">User the message concerns; propagated to the event stream.</param>
public record EmailMessage(
    string To,
    string Subject,
    string Html,
    string Text,
    string? From = null,
    string? Template = null,
    Guid? TenantId = null,
    Guid? UserId = null);
