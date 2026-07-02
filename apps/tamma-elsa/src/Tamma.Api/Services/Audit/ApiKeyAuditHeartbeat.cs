using System.Collections.Concurrent;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-10 (AC8) — throttle for <c>AUTH.APIKEY.USED</c>. API-key auth runs
/// on the hot per-request path; emitting an audit event every request would
/// flood the trail. This returns <c>true</c> at most once per
/// <c>(apiKeyId, coarse time bucket)</c> so a busy key produces a heartbeat, not
/// a flood.
/// </summary>
public interface IApiKeyAuditHeartbeat
{
    /// <summary>True the first time an API key is seen in the current time
    /// bucket; false for every subsequent request by the same key in that
    /// bucket. Thread-safe.</summary>
    bool ShouldEmit(Guid apiKeyId);
}

/// <summary>
/// In-process heartbeat: one <c>AUTH.APIKEY.USED</c> per key per
/// <see cref="Window"/>. Keyed by <c>apiKeyId</c> (bounded by the number of
/// distinct keys), driven off an injected <see cref="TimeProvider"/> so tests
/// can advance time deterministically.
/// </summary>
public sealed class ApiKeyAuditHeartbeat : IApiKeyAuditHeartbeat
{
    /// <summary>Heartbeat window. One audit event per key per window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, long> _lastBucket = new();

    public ApiKeyAuditHeartbeat(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <inheritdoc />
    public bool ShouldEmit(Guid apiKeyId)
    {
        var bucket = _time.GetUtcNow().UtcDateTime.Ticks / Window.Ticks;
        while (true)
        {
            if (_lastBucket.TryGetValue(apiKeyId, out var existing))
            {
                // Already emitted for this (or a later) bucket → suppress.
                if (existing >= bucket) return false;
                if (_lastBucket.TryUpdate(apiKeyId, bucket, existing)) return true;
            }
            else if (_lastBucket.TryAdd(apiKeyId, bucket))
            {
                return true;
            }
            // Lost a race — retry with a fresh read.
        }
    }
}
