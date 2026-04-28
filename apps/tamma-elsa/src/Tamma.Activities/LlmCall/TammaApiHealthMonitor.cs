using Tamma.Data.Abstractions;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Wave C.4 §4 — per-process health monitor for calls to the central
/// Tamma API. Every response from <see cref="TammaApiClient"/> is
/// reported here; the monitor keeps a bounded rolling-window of the
/// last 5 minutes of observations and fires
/// <c>PLATFORM.API.UNHEALTHY</c> (via <see cref="IAlertEventEmitter"/>)
/// when the sustained failure rate crosses 50% with at least 10 total
/// requests in window.
///
/// <para>Belt-and-suspenders dedup: the rule-engine throttle is 600s
/// per <c>BuiltInAlertRules</c>, and we add a 300s emitter-level dedup
/// because the signal is observer-shaped (threshold-crossing, not
/// event-per-failure). Without emitter dedup, every request after the
/// first crossing would re-emit — and the evaluator throttle would
/// drop them, generating pointless DCB event-store writes.</para>
/// </summary>
public sealed class TammaApiHealthMonitor
{
    /// <summary>Rolling window length (seconds). 5 minutes per brief.</summary>
    public const int WindowSeconds = 300;

    /// <summary>Minimum total requests before we can declare the API unhealthy.</summary>
    public const int MinRequestsForDecision = 10;

    /// <summary>Failure-rate threshold.</summary>
    public const decimal FailureRateThreshold = 0.5m;

    /// <summary>Emitter-level dedup window.</summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(300);

    private readonly IAlertEventEmitter _emitter;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly LinkedList<Record> _window = new();

    private DateTimeOffset _lastFireUtc = DateTimeOffset.MinValue;

    public TammaApiHealthMonitor(IAlertEventEmitter emitter, TimeProvider? time = null)
    {
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Record one TammaApiClient response. <paramref name="statusCode"/>
    /// is 0 or null for connection-level failures (no HTTP response);
    /// <paramref name="exceptionType"/> carries the exception type name
    /// when the call threw. Either a 5xx status OR a non-null exception
    /// counts as a failure; 2xx / 3xx / 4xx count as success (4xx is a
    /// client error, not a platform-health signal).
    /// </summary>
    public async Task RecordAsync(
        bool success,
        int? statusCode,
        string? exceptionType,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var reason = DescribeFailureReason(success, statusCode, exceptionType);
        var isFailure = reason is not null;

        PlatformApiUnhealthyEvent? toEmit = null;

        lock (_lock)
        {
            _window.AddLast(new Record(now, isFailure, reason));
            TrimExpired(now);

            // Short-circuit if we've recently emitted.
            if (now - _lastFireUtc < DedupWindow) return;

            var (total, failures) = CountBuckets();
            if (total < MinRequestsForDecision) return;

            var rate = failures == 0 ? 0m : (decimal)failures / total;
            if (rate < FailureRateThreshold) return;

            toEmit = new PlatformApiUnhealthyEvent(
                WindowSeconds: WindowSeconds,
                TotalRequests: total,
                FailureCount: failures,
                FailureRate: Math.Round(rate, 2),
                TopFailureReasons: TopReasons(limit: 3));

            _lastFireUtc = now;
        }

        await _emitter.EmitPlatformApiUnhealthyAsync(toEmit, ct).ConfigureAwait(false);
    }

    private static string? DescribeFailureReason(
        bool success, int? statusCode, string? exceptionType)
    {
        if (success) return null;
        if (!string.IsNullOrWhiteSpace(exceptionType)) return exceptionType;
        if (statusCode is int sc)
        {
            // 5xx = server-side. 4xx = client-side → not a health signal.
            if (sc >= 500 && sc < 600) return sc.ToString();
            // Anything else with success=false + no exception is
            // ambiguous — bucket as "other" so it still surfaces when
            // the overall rate is high.
            if (sc == 0) return "ConnectionError";
            return null;
        }
        return "Unknown";
    }

    private void TrimExpired(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(WindowSeconds);
        while (_window.First is { } head && head.Value.When < cutoff)
        {
            _window.RemoveFirst();
        }
    }

    private (int Total, int Failures) CountBuckets()
    {
        var total = _window.Count;
        var failures = 0;
        foreach (var r in _window)
        {
            if (r.IsFailure) failures++;
        }
        return (total, failures);
    }

    private IReadOnlyList<FailureReasonCount> TopReasons(int limit)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _window)
        {
            if (r.Reason is null) continue;
            counts[r.Reason] = counts.TryGetValue(r.Reason, out var c) ? c + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => new FailureReasonCount(kv.Key, kv.Value))
            .ToArray();
    }

    private readonly record struct Record(
        DateTimeOffset When, bool IsFailure, string? Reason);
}
