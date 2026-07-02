using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IPlatformEventPublisher"/> that ALWAYS throws on
/// <see cref="AppendAndPublishAsync"/>, simulating a transient event-store /
/// publisher outage. Used to prove that a POST-COMMIT audit emit is best-effort:
/// a publisher failure must NOT fail a catalog change that has already committed
/// (which would otherwise 500 the write and invite a retry that mints a second
/// superseding version).
/// </summary>
internal sealed class ThrowingPlatformEventPublisher : IPlatformEventPublisher
{
    /// <summary>Number of publish attempts observed (each throws).</summary>
    public int Attempts { get; private set; }

    public Task<PlatformEvent?> AppendAndPublishAsync(
        PlatformEvent evt, CancellationToken ct = default)
    {
        Attempts++;
        throw new InvalidOperationException(
            "Simulated platform-event publisher outage (AppendAndPublishAsync).");
    }
}
