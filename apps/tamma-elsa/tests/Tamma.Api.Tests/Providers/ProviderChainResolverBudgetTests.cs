using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.Providers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 9-5 — verifies the budget-aware overload of
/// <see cref="IProviderChainResolver.ResolveAsync(Guid?, string, string, ChainResolveOptions, CancellationToken)"/>.
/// Health behaviour is covered by <see cref="ProviderChainResolverTests"/>;
/// these tests exercise the new fields (<c>BudgetAllowed</c>, <c>BudgetSpent</c>,
/// <c>RecommendedProvider</c>, <c>AllExhausted</c>) and per-account override.
/// </summary>
[TestFixture]
public class ProviderChainResolverBudgetTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherAccount = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime PeriodStart = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private Mock<IAgentConfigRepository> _configRepo = null!;
    private FakeBreaker _breaker = null!;
    private FakeDiagnostics _diagnostics = null!;
    private ProviderChainResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _configRepo = new Mock<IAgentConfigRepository>();
        _breaker = new FakeBreaker();
        _diagnostics = new FakeDiagnostics();
        _sut = new ProviderChainResolver(_configRepo.Object, _breaker, _diagnostics);
    }

    private void SetupConfig(string json) =>
        _configRepo
            .Setup(r => r.ResolveAsync(TenantId))
            .ReturnsAsync((new AgentConfig { TenantId = TenantId, Config = json }, "tenant"));

    // ── budget pass-through ─────────────────────────────────────────────────

    [Test]
    public async Task ResolveAsync_NoBudgetConfigured_AllProvidersBudgetAllowed()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        // No diagnostics entry => GetBudgetAsync returns zero limit / not over budget.
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 0m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().HaveCount(2);
        result.Ordered.Should().OnlyContain(e => e.BudgetAllowed);
        result.RecommendedProvider.Should().Be("anthropic");
        result.AllExhausted.Should().BeFalse();
    }

    [Test]
    public async Task ResolveAsync_OverBudget_AllProvidersBudgetDenied_AndAllExhausted()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 110m, limit: 100m, isOver: true));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().HaveCount(2);
        result.Ordered.Should().OnlyContain(e => !e.BudgetAllowed);
        result.Ordered.Should().OnlyContain(e => e.BudgetSpent == 110m);
        result.RecommendedProvider.Should().BeNull();
        result.AllExhausted.Should().BeTrue();
        // Still returns ordered entries (not an empty error result) so callers
        // can present per-entry status to the user even when nothing is usable.
        result.HasCandidates.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Test]
    public async Task ResolveAsync_BudgetSpentSurfaced_OnEveryEntry()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 42.5m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().OnlyContain(e => e.BudgetSpent == 42.5m);
    }

    // ── recommendation logic ────────────────────────────────────────────────

    [Test]
    public async Task ResolveAsync_RecommendsFirstHealthyEntry()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.RecommendedProvider.Should().Be("anthropic");
        result.Ordered[0].Recommended.Should().BeTrue();
        result.Ordered[1].Recommended.Should().BeFalse();
        result.AllExhausted.Should().BeFalse();
    }

    [Test]
    public async Task ResolveAsync_FirstProviderOpen_RecommendsSecond()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        _breaker.Set("anthropic", CircuitBreakerState.Open);
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.RecommendedProvider.Should().Be("openai");
        result.Ordered.Should().ContainSingle();
        result.Ordered[0].Provider.Provider.Should().Be("openai");
        result.Ordered[0].Recommended.Should().BeTrue();
        // Open provider lives in Skipped, not Ordered, with the proper reason.
        result.Skipped.Should().ContainSingle(e =>
            e.Provider.Provider == "anthropic" &&
            e.Reason == ChainReason.CircuitOpen &&
            e.CircuitOpen);
    }

    [Test]
    public async Task ResolveAsync_HalfOpenProbeIsRecommendedWhenNoClosedAvailable()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"}]}}""");
        _breaker.Set("anthropic", CircuitBreakerState.HalfOpen);
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().ContainSingle();
        result.Ordered[0].Reason.Should().Be(ChainReason.HalfOpenProbeCandidate);
        result.RecommendedProvider.Should().Be("anthropic");
    }

    [Test]
    public async Task ResolveAsync_AllOpen_NoRecommendation()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"},{"provider":"openai"}]}}""");
        _breaker.Set("anthropic", CircuitBreakerState.Open);
        _breaker.Set("openai", CircuitBreakerState.Open);
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.RecommendedProvider.Should().BeNull();
        result.AllExhausted.Should().BeTrue();
        result.ErrorCode.Should().Be("NO_AVAILABLE_PROVIDER");
    }

    // ── per-account override ────────────────────────────────────────────────

    [Test]
    public async Task ResolveAsync_AccountIdOverride_QueriesOtherAccountBudget()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"}]}}""");
        // Tenant is healthy, but the override account is over-budget.
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));
        _diagnostics.SetBudget(OtherAccount, BuildStatus(spent: 200m, limit: 100m, isOver: true));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions(AccountId: OtherAccount));

        result.Ordered.Should().ContainSingle();
        result.Ordered[0].BudgetAllowed.Should().BeFalse();
        result.RecommendedProvider.Should().BeNull();
        result.AllExhausted.Should().BeTrue();

        // The override account's budget was the one queried, not the tenant's.
        _diagnostics.Queries.Should().Contain(OtherAccount);
        _diagnostics.Queries.Should().NotContain(TenantId);
    }

    [Test]
    public async Task ResolveAsync_NullTenantAndNullAccountId_SkipsBudgetCheck()
    {
        // System-scoped (no tenant) chain with no override — budget check
        // is skipped entirely so unauthenticated/system callers still get a
        // recommendation.
        _configRepo
            .Setup(r => r.GetAsync(null))
            .ReturnsAsync(new AgentConfig
            {
                TenantId = null,
                Config = """{"chains":{"default":[{"provider":"anthropic"}]}}""",
            });

        var result = await _sut.ResolveAsync(tenantId: null, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().ContainSingle();
        result.Ordered[0].BudgetAllowed.Should().BeTrue();
        result.RecommendedProvider.Should().Be("anthropic");
        // Diagnostics never consulted.
        _diagnostics.Queries.Should().BeEmpty();
    }

    [Test]
    public async Task ResolveAsync_DiagnosticsThrows_FailsOpen()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"}]}}""");
        _diagnostics.ThrowOnNextQuery();

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        // Fail-open: when the diagnostics service throws we treat as no
        // budget data — every entry stays BudgetAllowed=true.
        result.Ordered.Should().ContainSingle();
        result.Ordered[0].BudgetAllowed.Should().BeTrue();
        result.RecommendedProvider.Should().Be("anthropic");
        result.AllExhausted.Should().BeFalse();
    }

    // ── two-arg ctor stays alive (back-compat) ──────────────────────────────

    [Test]
    public async Task LegacyConstructor_NoDiagnostics_StillResolves()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"}]}}""");
        var legacy = new ProviderChainResolver(_configRepo.Object, _breaker);

        var result = await legacy.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().ContainSingle();
        // No diagnostics => budget defaults to allowed.
        result.Ordered[0].BudgetAllowed.Should().BeTrue();
    }

    // ── circuit-open metadata exposed ───────────────────────────────────────

    [Test]
    public async Task ResolveAsync_OpenProviderCarriesCircuitOpenUntil()
    {
        SetupConfig("""{"chains":{"default":[{"provider":"anthropic"}]}}""");
        var openUntil = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        _breaker.Set("anthropic", CircuitBreakerState.Open, openUntil);
        _diagnostics.SetBudget(TenantId, BuildStatus(spent: 0m, limit: 100m));

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation",
            new ChainResolveOptions());

        result.Ordered.Should().BeEmpty();
        result.Skipped.Should().ContainSingle();
        result.Skipped[0].CircuitOpen.Should().BeTrue();
        result.Skipped[0].CircuitOpenUntil.Should().Be(openUntil);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static BudgetStatus BuildStatus(decimal spent, decimal limit, bool isOver = false) =>
        new(
            AccountId: Guid.Empty,
            PeriodStart: PeriodStart,
            PeriodEnd: PeriodEnd,
            Spent: spent,
            Limit: limit,
            Remaining: Math.Max(0m, limit - spent),
            PercentUsed: limit > 0 ? (double)(spent / limit) * 100.0 : 0.0,
            AlertThreshold: 0.8,
            ShouldAlert: isOver,
            IsOverBudget: isOver);

    private sealed class FakeBreaker : ICircuitBreakerService
    {
        private readonly Dictionary<string, (CircuitBreakerState State, DateTimeOffset? OpenUntil)> _states = new();

        public void Set(string key, CircuitBreakerState state, DateTimeOffset? openUntil = null) =>
            _states[key] = (state, openUntil);

        public Task<CircuitBreakerStatus> GetStateAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default)
        {
            var (state, openUntil) = _states.TryGetValue(providerKey, out var s)
                ? s
                : (CircuitBreakerState.Closed, (DateTimeOffset?)null);
            return Task.FromResult(new CircuitBreakerStatus(
                providerKey, state, 0, null, null, openUntil, false));
        }

        public Task<CircuitBreakerStatus> RecordFailureAsync(string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CircuitBreakerStatus> RecordSuccessAsync(string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TryProbeAsync(string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CircuitBreakerStatus> ResetAsync(string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CircuitBreakerStatus>> ListAsync(Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDiagnostics : IDiagnosticsService
    {
        private readonly Dictionary<Guid, BudgetStatus> _budgets = new();
        private bool _shouldThrow;
        public List<Guid> Queries { get; } = new();

        public void SetBudget(Guid accountId, BudgetStatus status) => _budgets[accountId] = status;
        public void ThrowOnNextQuery() => _shouldThrow = true;

        public Task<BudgetStatus> GetBudgetAsync(Guid accountId, CancellationToken ct = default)
        {
            Queries.Add(accountId);
            if (_shouldThrow)
            {
                _shouldThrow = false;
                throw new InvalidOperationException("simulated diagnostics outage");
            }
            return Task.FromResult(_budgets.TryGetValue(accountId, out var b)
                ? b with { AccountId = accountId }
                : BuildStatus(spent: 0m, limit: 0m) with { AccountId = accountId });
        }

        public Task<Guid> RecordEventAsync(ProviderDiagnostic diag, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
            DiagnosticsFilter filter, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DiagnosticsReport> GetReportAsync(
            Guid? tenantId, DateTime from, DateTime to, BucketSize bucketSize, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DimensionReport> GetDimensionReportAsync(
            Guid? tenantId, DateTime from, DateTime to, DimensionGroup groupBy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ProviderDiagnosticsDeepReport> GetDeepReportAsync(
            Guid? tenantId, DateTime from, DateTime to, string? providerKey, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public IReadOnlyList<ProviderDiagnostic> GetRecentEvents(Guid? tenantId, int limit = 50) =>
            throw new NotSupportedException();
    }
}
