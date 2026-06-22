using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 — shared test doubles for the SaaS provider gate suites.
/// Deliberately minimal: the gate touches mode + auth-lookup + entitlement +
/// events + metrics only — there is NO credential/secret seam anywhere in the
/// fixture (the credential-safety guard).
/// </summary>
internal static class GateTestHelpers
{
    /// <summary>Build a gate wired with the supplied doubles (logger is a no-op).</summary>
    public static SaaSProviderGate Build(
        ITammaModeProvider mode,
        IProviderAuthLookup lookup,
        ITenantProviderEntitlement entitlement,
        IEventRepository events,
        ProviderGatingMetrics metrics) =>
        new(mode, lookup, entitlement, events, metrics,
            NullLogger<SaaSProviderGate>.Instance);
}

/// <summary>A process-stable mode stub.</summary>
internal sealed class StubMode(TammaMode mode) : ITammaModeProvider
{
    public TammaMode Mode { get; } = mode;
}

/// <summary>
/// An <see cref="IProviderAuthLookup"/> double driven by an explicit map. Records
/// every call so single-user tests can assert the lookup is NEVER consulted.
/// </summary>
internal sealed class FakeAuthLookup : IProviderAuthLookup
{
    private readonly IReadOnlyDictionary<string, ProviderAuthModel?> _map;
    private readonly Func<Exception>? _throwFactory;
    public List<string?> Calls { get; } = new();

    public FakeAuthLookup(
        IReadOnlyDictionary<string, ProviderAuthModel?> map,
        Func<Exception>? throwFactory = null)
    {
        _map = map;
        _throwFactory = throwFactory;
    }

    /// <summary>Convenience: anthropic=api-key, claude-code=cli-token, everything else unknown.</summary>
    public static FakeAuthLookup Default() => new(new Dictionary<string, ProviderAuthModel?>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = ProviderAuthModel.ApiKey,
        ["openai"] = ProviderAuthModel.ApiKey,
        ["openrouter"] = ProviderAuthModel.ApiKey,
        ["gemini"] = ProviderAuthModel.ApiKey,
        ["claude-code"] = ProviderAuthModel.CliToken,
        ["opencode"] = ProviderAuthModel.CliToken,
        ["zen-mcp"] = ProviderAuthModel.CliToken,
    });

    /// <summary>A lookup that throws the supplied exception on every call (simulates a transient DB/seam failure).</summary>
    public static FakeAuthLookup Throwing(Func<Exception> throwFactory) =>
        new(new Dictionary<string, ProviderAuthModel?>(StringComparer.OrdinalIgnoreCase), throwFactory);

    public Task<ProviderAuthModel?> AuthModelAsync(string? providerName, CancellationToken ct = default)
    {
        Calls.Add(providerName);
        if (_throwFactory is not null)
            throw _throwFactory();
        if (providerName is not null && _map.TryGetValue(providerName.Trim(), out var model))
            return Task.FromResult(model);
        return Task.FromResult<ProviderAuthModel?>(null);
    }
}

/// <summary>An entitlement double returning a fixed verdict; records calls. Can be configured to throw.</summary>
internal sealed class FakeEntitlement : ITenantProviderEntitlement
{
    private readonly bool _entitled;
    private readonly Func<Exception>? _throwFactory;
    public List<(Guid? TenantId, string Provider)> Calls { get; } = new();

    public FakeEntitlement(bool entitled, Func<Exception>? throwFactory = null)
    {
        _entitled = entitled;
        _throwFactory = throwFactory;
    }

    /// <summary>An entitlement check that throws the supplied exception (simulates an Epic-34 engine failure).</summary>
    public static FakeEntitlement Throwing(Func<Exception> throwFactory) => new(entitled: true, throwFactory);

    public Task<bool> IsTenantEntitledAsync(Guid? tenantId, string providerName, CancellationToken ct = default)
    {
        Calls.Add((tenantId, providerName));
        if (_throwFactory is not null)
            throw _throwFactory();
        return Task.FromResult(_entitled);
    }
}

/// <summary>A recording <see cref="IEventRepository"/>; optionally throws on append.</summary>
internal sealed class RecordingGateEventRepository : IEventRepository
{
    private readonly bool _throwOnAppend;
    public List<DomainEvent> Appended { get; } = new();

    public RecordingGateEventRepository(bool throwOnAppend = false) => _throwOnAppend = throwOnAppend;

    public Task<DomainEvent> AppendAsync(DomainEvent evt)
    {
        if (_throwOnAppend)
            throw new InvalidOperationException("simulated event-store failure");
        Appended.Add(evt);
        return Task.FromResult(evt);
    }

    public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
    public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
        Task.FromResult(new List<DomainEvent>());
    public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
        Task.FromResult<DomainEvent?>(null);
    public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
    public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
        Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
        Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
        Guid tenantId, string? typePrefix, int limit, int offset) =>
        Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
}
