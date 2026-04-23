using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 / Story 1.5-37 (Wave C.1) — one implementation per
/// <see cref="AlertChannelType"/>. The <see cref="NotificationDispatcher"/>
/// resolves a channel by its <see cref="ChannelType"/> string via
/// <see cref="IAlertChannelRegistry"/> and calls
/// <see cref="SendAsync"/> with the alert + channel config row.
///
/// <para><b>Secret access</b>: implementations that need a credential
/// (Slack webhook URL, PagerDuty routing_key, webhook HMAC secret)
/// resolve it through <c>ISecretStoreBackend</c> keyed by
/// <see cref="AlertChannel.CredentialsSecretId"/>. The
/// <see cref="AlertChannel.Config"/> column MUST NOT carry plaintext
/// credentials — this is enforced by unit tests and is a deployment
/// invariant.</para>
/// </summary>
public interface IAlertChannel
{
    /// <summary>
    /// The channel type this implementation handles. Matches the
    /// <see cref="AlertChannel.ChannelType"/> column value.
    /// </summary>
    string ChannelType { get; }

    /// <summary>
    /// Deliver <paramref name="alert"/> through <paramref name="channel"/>.
    /// Returns <see cref="DeliveryResult.Success"/> on any successful
    /// outcome (including 2xx HTTP or outbox-enqueue success). Must
    /// NOT throw — a throwable failure should be wrapped in a
    /// <see cref="DeliveryResult"/> with a descriptive error.
    /// </summary>
    Task<DeliveryResult> SendAsync(
        Alert alert,
        AlertChannel channel,
        CancellationToken ct);
}

/// <summary>
/// Outcome of a single channel delivery attempt. <c>Success = true</c>
/// means the alert was handed off to its downstream (SMTP outbox /
/// webhook endpoint / PagerDuty / Slack) cleanly; the dispatcher
/// flips the delivery-attempt row to <c>success</c>. <c>Success =
/// false</c> keeps the row in <c>failed</c> state until the retry
/// ceiling is reached; <see cref="Error"/> is persisted for audit.
/// </summary>
public sealed record DeliveryResult(bool Success, string? Error);

/// <summary>
/// Story 5.6 / Story 1.5-37 (Wave C.1) — resolves an
/// <see cref="IAlertChannel"/> implementation by its
/// <see cref="IAlertChannel.ChannelType"/>.
/// </summary>
public interface IAlertChannelRegistry
{
    /// <summary>
    /// Look up the channel implementation for
    /// <paramref name="channelType"/>. Returns null when no
    /// implementation is registered (should only happen during tests
    /// or a partial deployment — production registrations cover every
    /// value in <see cref="AlertChannelType.All"/>).
    /// </summary>
    IAlertChannel? Resolve(string channelType);
}

/// <summary>
/// Default registry: looks up channels from a DI-supplied enumerable
/// of <see cref="IAlertChannel"/>. Registrations are singleton; the
/// registry is a thin facade over a dictionary keyed by
/// <see cref="IAlertChannel.ChannelType"/>.
/// </summary>
public sealed class AlertChannelRegistry : IAlertChannelRegistry
{
    private readonly IReadOnlyDictionary<string, IAlertChannel> _byType;

    public AlertChannelRegistry(IEnumerable<IAlertChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _byType = channels.ToDictionary(
            c => c.ChannelType,
            c => c,
            StringComparer.OrdinalIgnoreCase);
    }

    public IAlertChannel? Resolve(string channelType)
    {
        ArgumentNullException.ThrowIfNull(channelType);
        return _byType.TryGetValue(channelType, out var channel)
            ? channel
            : null;
    }
}
