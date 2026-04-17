using System.Collections.Concurrent;
using System.Diagnostics;
using Tamma.Api.Services.Diagnostics;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IProviderSessionService"/>.
///
/// <para>
/// Sessions live in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// handle. The service holds no per-session provider state beyond identity —
/// actual provider traffic is dispatched via <see cref="IProviderClient"/> at
/// <see cref="ExecuteAsync"/> time.
/// </para>
/// <para>
/// Registered as a singleton so sessions survive request scopes.
/// <see cref="IDiagnosticsService"/> is injected directly (it is itself a
/// singleton that resolves EF repositories via <c>IServiceScopeFactory</c>).
/// </para>
/// </summary>
public sealed class ProviderSessionService : IProviderSessionService
{
    private readonly ConcurrentDictionary<string, ProviderSession> _sessions = new();
    private readonly IProviderClient _client;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISystemClock _clock;
    private readonly ILogger<ProviderSessionService> _logger;

    public ProviderSessionService(
        IProviderClient client,
        IDiagnosticsService diagnostics,
        ISystemClock clock,
        ILogger<ProviderSessionService> logger)
    {
        _client = client;
        _diagnostics = diagnostics;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ProviderSession> CreateAsync(string provider, string model, Guid? tenantId)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("provider is required", nameof(provider));
        if (string.IsNullOrWhiteSpace(model))
            model = "default";

        var now = _clock.UtcNow.UtcDateTime;
        var session = new ProviderSession(
            Handle: Guid.NewGuid().ToString(),
            Provider: provider,
            Model: model,
            CreatedAt: now,
            LastUsed: now,
            TenantId: tenantId);

        _sessions[session.Handle] = session;
        _logger.LogInformation(
            "Provider session created: {Handle} provider={Provider} model={Model} tenant={Tenant}",
            session.Handle, provider, model, tenantId);
        return Task.FromResult(session);
    }

    /// <inheritdoc />
    public Task<ProviderSession?> GetAsync(string handle)
    {
        if (!_sessions.TryGetValue(handle, out var current))
        {
            return Task.FromResult<ProviderSession?>(null);
        }

        var touched = current with { LastUsed = _clock.UtcNow.UtcDateTime };
        // Compare-and-swap so a concurrent eviction can't resurrect the session.
        if (_sessions.TryUpdate(handle, touched, current))
        {
            return Task.FromResult<ProviderSession?>(touched);
        }

        // Lost the race to another update/delete — re-fetch.
        return Task.FromResult(_sessions.TryGetValue(handle, out var latest) ? latest : null);
    }

    /// <inheritdoc />
    public async Task<ProviderSession?> GetTenantScopedAsync(Guid? callerTenantId, string handle)
    {
        var session = await GetAsync(handle);
        if (session is null) return null;
        return session.TenantId == callerTenantId ? session : null;
    }

    /// <inheritdoc />
    public async Task<ExecuteResult> ExecuteAsync(
        string handle, ExecuteRequest req, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle, out var session))
        {
            throw new ProviderSessionNotFoundException(handle);
        }

        // Refresh lastUsed up-front so cleanup cannot evict an in-flight
        // session between here and the network call returning.
        var touched = session with { LastUsed = _clock.UtcNow.UtcDateTime };
        _sessions.TryUpdate(handle, touched, session);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var invocation = await _client.InvokeAsync(session.Provider, session.Model, req, ct);
            stopwatch.Stop();

            var durationMs = invocation.DurationMs > 0 ? invocation.DurationMs : stopwatch.ElapsedMilliseconds;

            await _diagnostics.RecordEventAsync(new ProviderDiagnostic
            {
                ProviderKey = session.Provider,
                Model = session.Model,
                RequestDurationMs = durationMs,
                TokensUsed = invocation.TokensUsed,
                Cost = invocation.CostUsd,
                TenantId = session.TenantId,
                Success = true,
                CreatedAt = _clock.UtcNow.UtcDateTime,
                RequestType = "provider-session-execute",
            }, ct);

            return new ExecuteResult(
                Content: invocation.Content,
                TokenUsage: invocation.TokensUsed,
                CostUsd: invocation.CostUsd,
                DurationMs: durationMs);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Record the failure before propagating so aggregate reporting
            // captures the attempt. Never throw from the diagnostics call —
            // swallow + log so the caller sees the original exception.
            try
            {
                await _diagnostics.RecordEventAsync(new ProviderDiagnostic
                {
                    ProviderKey = session.Provider,
                    Model = session.Model,
                    RequestDurationMs = stopwatch.ElapsedMilliseconds,
                    TokensUsed = 0,
                    Cost = 0m,
                    TenantId = session.TenantId,
                    Success = false,
                    ErrorMessage = ex.Message,
                    CreatedAt = _clock.UtcNow.UtcDateTime,
                    RequestType = "provider-session-execute",
                }, ct);
            }
            catch (Exception diagEx)
            {
                _logger.LogError(diagEx,
                    "Failed to record diagnostic for failed provider execution {Handle}", handle);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ExecuteResult> ExecuteTenantScopedAsync(
        Guid? callerTenantId, string handle, ExecuteRequest req, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(handle, out var session) || session.TenantId != callerTenantId)
        {
            throw new ProviderSessionNotFoundException(handle);
        }
        return await ExecuteAsync(handle, req, ct);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string handle)
    {
        var removed = _sessions.TryRemove(handle, out _);
        if (removed)
        {
            _logger.LogInformation("Provider session deleted: {Handle}", handle);
        }
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> DeleteTenantScopedAsync(Guid? callerTenantId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session) || session.TenantId != callerTenantId)
        {
            return Task.FromResult(false);
        }
        return DeleteAsync(handle);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderSession>> ListAsync(Guid? tenantId)
    {
        IReadOnlyList<ProviderSession> filtered = tenantId is null
            ? _sessions.Values.ToList()
            : _sessions.Values.Where(s => s.TenantId == tenantId).ToList();
        return Task.FromResult(filtered);
    }

    /// <inheritdoc />
    public Task<int> EvictInactiveAsync(TimeSpan olderThan)
    {
        var cutoff = _clock.UtcNow.UtcDateTime - olderThan;
        var evicted = 0;
        foreach (var (handle, session) in _sessions)
        {
            if (session.LastUsed < cutoff)
            {
                if (_sessions.TryRemove(handle, out _))
                {
                    evicted++;
                    _logger.LogInformation(
                        "Evicted idle provider session {Handle} (last-used {LastUsed:o})",
                        handle, session.LastUsed);
                }
            }
        }
        return Task.FromResult(evicted);
    }
}
