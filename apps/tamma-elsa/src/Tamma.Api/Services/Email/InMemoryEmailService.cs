using System.Collections.Concurrent;
using System.Text.Json;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

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
///
/// <para>
/// Like the real providers, <see cref="SendAsync"/> returns a transaction id
/// and — when an <see cref="IEventRepository"/> is wired in — emits
/// <c>EMAIL.QUEUED.SUCCESS</c> and <c>EMAIL.SENT.SUCCESS</c> events so
/// consumers get a uniform event-stream signal regardless of which provider
/// is active.
/// </para>
/// </summary>
public sealed class InMemoryEmailService : IEmailService
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();
    private readonly IEventRepository? _events;

    /// <summary>
    /// Construct a stand-alone inbox. No event-store writes are performed.
    /// Kept for tests that construct the service directly
    /// (e.g. <c>new InMemoryEmailService()</c>).
    /// </summary>
    public InMemoryEmailService() { }

    /// <summary>
    /// Construct an inbox that mirrors the real providers' event-emission
    /// behaviour. Called by DI.
    /// </summary>
    public InMemoryEmailService(IEventRepository events)
    {
        _events = events;
    }

    /// <summary>Every message seen by <see cref="SendAsync"/>, in send order.</summary>
    public IReadOnlyList<EmailMessage> SentMessages => _messages.ToArray();

    /// <summary>Drop all captured messages. Test helper — not used in prod.</summary>
    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { /* drain */ }
    }

    public async Task<Guid> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var txnId = Guid.NewGuid();
        _messages.Enqueue(message);

        if (_events is not null)
        {
            await EmitEventAsync(EmailEventTypes.Queued, txnId, message, provider: "in-memory");
            await EmitEventAsync(EmailEventTypes.Sent, txnId, message, provider: "in-memory");
        }

        return txnId;
    }

    private async Task EmitEventAsync(
        string type, Guid txnId, EmailMessage message, string provider)
    {
        // Recipient, subject, and body are DELIBERATELY omitted from both tags
        // and data. The outbox table is the only place they live.
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = txnId.ToString(),
            ["template"] = message.Template,
            ["tenant_id"] = message.TenantId?.ToString(),
            ["user_id"] = message.UserId?.ToString(),
        };

        var data = new Dictionary<string, object?>
        {
            ["provider"] = provider,
        };

        await _events!.AppendAsync(new DomainEvent
        {
            Type = type,
            TenantId = message.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }
}
