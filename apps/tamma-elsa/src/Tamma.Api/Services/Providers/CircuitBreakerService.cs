using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Real circuit-breaker state machine persisted to the <c>provider_health</c> table.
///
/// <para>State transitions:</para>
/// <list type="bullet">
///   <item><c>Closed → Open</c>: N failures within sliding window <c>W</c>.</item>
///   <item><c>Open → HalfOpen</c>: cooldown <c>C</c> has elapsed since the circuit opened.</item>
///   <item><c>HalfOpen → Closed</c>: probe succeeded.</item>
///   <item><c>HalfOpen → Open</c>: probe failed — re-open for another cooldown.</item>
/// </list>
///
/// <para>State is scoped per-tenant (<see cref="Guid"/>?). <c>TenantId = null</c>
/// denotes the system-wide row.</para>
///
/// <para>Uses <see cref="ISystemClock"/> so tests can advance wall time.</para>
/// </summary>
public sealed class CircuitBreakerService : ICircuitBreakerService
{
    private readonly IProviderHealthRepository _repo;
    private readonly ISystemClock _clock;
    private readonly CircuitBreakerOptions _options;

    public CircuitBreakerService(
        IProviderHealthRepository repo,
        ISystemClock clock,
        CircuitBreakerOptions? options = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new CircuitBreakerOptions();
    }

    public async Task<CircuitBreakerStatus> RecordSuccessAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        ValidateKey(providerKey);

        var health = await _repo.GetOrCreateAsync(providerKey, tenantId);
        var now = _clock.UtcNow.UtcDateTime;

        // Success resets everything: circuit closes, counters clear.
        health.Status = "healthy";
        health.LastSuccess = now;
        health.FailureCount = 0;
        health.FailureWindowStart = null;
        health.CircuitOpenUntil = null;
        health.HalfOpenInProgress = false;
        health.UpdatedAt = now;

        await _repo.SaveChangesAsync();
        return ToStatus(health, now);
    }

    public async Task<CircuitBreakerStatus> RecordFailureAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        ValidateKey(providerKey);

        var health = await _repo.GetOrCreateAsync(providerKey, tenantId);
        var now = _clock.UtcNow.UtcDateTime;

        var wasHalfOpen = EffectiveStateNoWrite(health, now) == CircuitBreakerState.HalfOpen;

        // Slide the failure window if expired or absent.
        if (health.FailureWindowStart is null ||
            (now - health.FailureWindowStart.Value) > _options.FailureWindow)
        {
            health.FailureWindowStart = now;
            health.FailureCount = 0;
        }

        health.FailureCount++;
        health.LastFailure = now;
        health.UpdatedAt = now;

        // A HalfOpen probe that fails re-opens the circuit immediately, regardless
        // of window failure count.
        if (wasHalfOpen)
        {
            OpenCircuit(health, now);
        }
        else if (health.FailureCount >= _options.FailureThreshold)
        {
            OpenCircuit(health, now);
        }
        else
        {
            // Closed, below threshold.
            health.Status = "degraded";
        }

        await _repo.SaveChangesAsync();
        return ToStatus(health, now);
    }

    public async Task<CircuitBreakerStatus> GetStateAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        ValidateKey(providerKey);

        var existing = await _repo.GetStatusAsync(providerKey, tenantId);
        if (existing is null)
        {
            // No recorded activity → treat as Closed with zero failures.
            return new CircuitBreakerStatus(
                providerKey, CircuitBreakerState.Closed, 0, null, null, null, false);
        }

        var now = _clock.UtcNow.UtcDateTime;

        // Auto-promote Open→HalfOpen if cooldown elapsed. Persist so callers
        // see a stable view.
        if (existing.CircuitOpenUntil is not null && now >= existing.CircuitOpenUntil.Value)
        {
            if (!existing.HalfOpenInProgress)
            {
                existing.Status = "degraded";
                existing.UpdatedAt = now;
                await _repo.SaveChangesAsync();
            }
        }

        return ToStatus(existing, now);
    }

    public async Task<bool> TryProbeAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        ValidateKey(providerKey);

        var existing = await _repo.GetStatusAsync(providerKey, tenantId);
        if (existing is null) return false;

        var now = _clock.UtcNow.UtcDateTime;
        var state = EffectiveStateNoWrite(existing, now);

        if (state != CircuitBreakerState.HalfOpen) return false;
        if (existing.HalfOpenInProgress) return false;

        existing.HalfOpenInProgress = true;
        existing.UpdatedAt = now;
        await _repo.SaveChangesAsync();
        return true;
    }

    public async Task<CircuitBreakerStatus> ResetAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        ValidateKey(providerKey);

        var health = await _repo.GetOrCreateAsync(providerKey, tenantId);
        var now = _clock.UtcNow.UtcDateTime;

        health.Status = "unknown";
        health.FailureCount = 0;
        health.FailureWindowStart = null;
        health.CircuitOpenUntil = null;
        health.HalfOpenInProgress = false;
        health.UpdatedAt = now;

        await _repo.SaveChangesAsync();
        return ToStatus(health, now);
    }

    public async Task<IReadOnlyList<CircuitBreakerStatus>> ListAsync(
        Guid? tenantId, CancellationToken ct = default)
    {
        var all = await _repo.GetAllAsync(tenantId);
        var now = _clock.UtcNow.UtcDateTime;
        return all.Select(h => ToStatus(h, now)).ToList();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void OpenCircuit(ProviderHealth health, DateTime now)
    {
        health.CircuitOpenUntil = now.Add(_options.CooldownDuration);
        health.HalfOpenInProgress = false;
        health.Status = "down";
    }

    private static CircuitBreakerState EffectiveStateNoWrite(ProviderHealth health, DateTime now)
    {
        if (health.CircuitOpenUntil is null) return CircuitBreakerState.Closed;
        if (now < health.CircuitOpenUntil.Value) return CircuitBreakerState.Open;
        return CircuitBreakerState.HalfOpen;
    }

    private CircuitBreakerStatus ToStatus(ProviderHealth h, DateTime now)
    {
        var state = EffectiveStateNoWrite(h, now);
        DateTimeOffset? openUntil = h.CircuitOpenUntil.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(h.CircuitOpenUntil.Value, DateTimeKind.Utc))
            : null;
        DateTimeOffset? lastSuccess = h.LastSuccess.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(h.LastSuccess.Value, DateTimeKind.Utc))
            : null;
        DateTimeOffset? lastFailure = h.LastFailure.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(h.LastFailure.Value, DateTimeKind.Utc))
            : null;

        return new CircuitBreakerStatus(
            h.ProviderKey,
            state,
            h.FailureCount,
            lastSuccess,
            lastFailure,
            openUntil,
            h.HalfOpenInProgress);
    }

    /// <summary>
    /// Charset regex matching the TS validator at
    /// <c>packages/api/src/routes/settings/health-routes.ts:14-22</c>. Defence
    /// in depth — finding 013. The endpoint layer also validates so callers
    /// see a 400; this layer protects activities / non-HTTP callers.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex KeyPattern =
        new("^[a-zA-Z0-9._\\-:/]+$",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void ValidateKey(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new ArgumentException("Provider key must not be empty.", nameof(providerKey));
        if (providerKey.Length > 256)
            throw new ArgumentException("Provider key too long (max 256).", nameof(providerKey));
        if (!KeyPattern.IsMatch(providerKey))
            throw new ArgumentException(
                "Provider key contains invalid characters (allowed: a-zA-Z0-9._-:/).",
                nameof(providerKey));
    }
}
