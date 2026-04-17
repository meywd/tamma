using System.Collections.Concurrent;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Non-sending <see cref="IEmailService"/> that captures every message in a
/// thread-safe queue. Used by:
/// <list type="bullet">
///   <item><description>Integration tests — assert mail contents without a
///     real SMTP server.</description></item>
///   <item><description>Local development without SMTP configured — the
///     <see cref="EmailServiceCollectionExtensions"/> falls back to this
///     implementation and logs a warning.</description></item>
/// </list>
/// </summary>
public sealed class InMemoryEmailService : IEmailService
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    /// <summary>Every message seen by <see cref="SendAsync"/>, in send order.</summary>
    public IReadOnlyList<EmailMessage> SentMessages => _messages.ToArray();

    /// <summary>Drop all captured messages. Test helper — not used in prod.</summary>
    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { /* drain */ }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
