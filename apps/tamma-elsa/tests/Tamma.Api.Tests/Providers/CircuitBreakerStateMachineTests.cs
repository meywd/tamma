using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Providers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Unit tests for <see cref="CircuitBreakerService"/> exercising the
/// Closed→Open→HalfOpen→(Closed|Open) transitions with a fake clock and an
/// in-memory <see cref="IProviderHealthRepository"/> double. Persistence
/// round-trips are covered separately by the integration test fixture.
/// </summary>
[TestFixture]
public class CircuitBreakerStateMachineTests
{
    private InMemoryHealthRepo _repo = null!;
    private TestSystemClock _clock = null!;
    private CircuitBreakerService _sut = null!;

    private static readonly CircuitBreakerOptions TestOptions = new()
    {
        FailureThreshold = 3,
        FailureWindow = TimeSpan.FromSeconds(60),
        CooldownDuration = TimeSpan.FromSeconds(300),
    };

    private static readonly DateTimeOffset Start = new(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _repo = new InMemoryHealthRepo();
        _clock = new TestSystemClock(Start);
        _sut = new CircuitBreakerService(_repo, _clock, TestOptions);
    }

    // ── Closed → Open ────────────────────────────────────────────────────────

    [Test]
    public async Task NewProvider_GetState_ReturnsClosedZeroFailures()
    {
        var s = await _sut.GetStateAsync("anthropic", tenantId: null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(0);
        s.CircuitOpenUntil.Should().BeNull();
    }

    [Test]
    public async Task RecordFailure_BelowThreshold_StaysClosed()
    {
        await _sut.RecordFailureAsync("anthropic", null);
        await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.GetStateAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(2);
    }

    [Test]
    public async Task RecordFailure_AtThreshold_OpensCircuit()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.GetStateAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Open);
        s.CircuitOpenUntil.Should().NotBeNull();
        s.CircuitOpenUntil!.Value.Should().Be(Start.AddSeconds(300));
        s.FailureCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task RecordFailure_OutsideWindow_ResetsCount()
    {
        await _sut.RecordFailureAsync("anthropic", null);
        await _sut.RecordFailureAsync("anthropic", null);

        // Advance past the 60-second failure window.
        _clock.Advance(TimeSpan.FromSeconds(61));

        await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.GetStateAsync("anthropic", null);
        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(1); // window restarted
    }

    // ── Open → HalfOpen (cooldown) ───────────────────────────────────────────

    [Test]
    public async Task OpenCircuit_BeforeCooldown_StillOpen()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        _clock.Advance(TimeSpan.FromSeconds(299));

        var s = await _sut.GetStateAsync("anthropic", null);
        s.State.Should().Be(CircuitBreakerState.Open);
    }

    [Test]
    public async Task OpenCircuit_AfterCooldown_PromotesToHalfOpen()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        _clock.Advance(TimeSpan.FromSeconds(300));

        var s = await _sut.GetStateAsync("anthropic", null);
        s.State.Should().Be(CircuitBreakerState.HalfOpen);
    }

    // ── HalfOpen → Closed (probe success) ────────────────────────────────────

    [Test]
    public async Task HalfOpen_ProbeSuccess_ClosesCircuit()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);
        _clock.Advance(TimeSpan.FromSeconds(301));

        var probed = await _sut.TryProbeAsync("anthropic", null);
        probed.Should().BeTrue();

        var s = await _sut.RecordSuccessAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(0);
        s.CircuitOpenUntil.Should().BeNull();
        s.HalfOpenInProgress.Should().BeFalse();
    }

    // ── HalfOpen → Open (probe failure) ──────────────────────────────────────

    [Test]
    public async Task HalfOpen_ProbeFailure_ReopensCircuit()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);
        _clock.Advance(TimeSpan.FromSeconds(301));

        (await _sut.TryProbeAsync("anthropic", null)).Should().BeTrue();

        var s = await _sut.RecordFailureAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Open);
        // Cooldown should be reset from the new failure time.
        s.CircuitOpenUntil!.Value.Should().Be(Start.AddSeconds(301 + 300));
    }

    [Test]
    public async Task TryProbe_WhenCircuitClosed_ReturnsFalse()
    {
        (await _sut.TryProbeAsync("anthropic", null)).Should().BeFalse();
    }

    [Test]
    public async Task TryProbe_WhenCircuitOpen_ReturnsFalse()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        (await _sut.TryProbeAsync("anthropic", null)).Should().BeFalse();
    }

    [Test]
    public async Task TryProbe_OnlyOneCallerSucceeds()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);
        _clock.Advance(TimeSpan.FromSeconds(301));

        var first = await _sut.TryProbeAsync("anthropic", null);
        var second = await _sut.TryProbeAsync("anthropic", null);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Reset_ClearsAllState()
    {
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.ResetAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(0);
        s.CircuitOpenUntil.Should().BeNull();
        s.HalfOpenInProgress.Should().BeFalse();
    }

    // ── Recording success mid-stream ─────────────────────────────────────────

    [Test]
    public async Task RecordSuccess_InClosedState_ResetsFailureCount()
    {
        await _sut.RecordFailureAsync("anthropic", null);
        await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.RecordSuccessAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(0);
        s.LastSuccess.Should().NotBeNull();
    }

    [Test]
    public async Task RecordSuccess_InOpenState_ClosesCircuit()
    {
        // Even without probing, a success recorded directly clears the breaker.
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", null);

        var s = await _sut.RecordSuccessAsync("anthropic", null);

        s.State.Should().Be(CircuitBreakerState.Closed);
        s.FailureCount.Should().Be(0);
    }

    // ── Per-tenant isolation ─────────────────────────────────────────────────

    [Test]
    public async Task TenantsAreIsolated_FailuresInOneTenantDoNotAffectAnother()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        // Trip tenant A.
        for (var i = 0; i < 3; i++)
            await _sut.RecordFailureAsync("anthropic", tenantA);

        var a = await _sut.GetStateAsync("anthropic", tenantA);
        var b = await _sut.GetStateAsync("anthropic", tenantB);
        var system = await _sut.GetStateAsync("anthropic", null);

        a.State.Should().Be(CircuitBreakerState.Open);
        b.State.Should().Be(CircuitBreakerState.Closed);
        b.FailureCount.Should().Be(0);
        system.State.Should().Be(CircuitBreakerState.Closed);
    }

    [Test]
    public async Task TenantsAreIsolated_ListReturnsOnlyTenantOwnedRows()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        await _sut.RecordFailureAsync("anthropic", tenantA);
        await _sut.RecordFailureAsync("openai", tenantB);

        var a = await _sut.ListAsync(tenantA);
        var b = await _sut.ListAsync(tenantB);

        a.Should().ContainSingle(x => x.ProviderKey == "anthropic");
        b.Should().ContainSingle(x => x.ProviderKey == "openai");
        a.Should().NotContain(x => x.ProviderKey == "openai");
    }

    // ── Key validation ───────────────────────────────────────────────────────

    [Test]
    public void RecordFailure_EmptyKey_Throws()
    {
        var act = async () => await _sut.RecordFailureAsync(" ", null);
        act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public void RecordSuccess_EmptyKey_Throws()
    {
        var act = async () => await _sut.RecordSuccessAsync(string.Empty, null);
        act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Status label round-trip ──────────────────────────────────────────────

    [Test]
    public async Task Failure_PopulatesLastFailureTimestamp()
    {
        var s = await _sut.RecordFailureAsync("anthropic", null);
        s.LastFailure.Should().NotBeNull();
        s.LastFailure!.Value.Should().Be(Start);
    }

    [Test]
    public async Task Success_PopulatesLastSuccessTimestamp()
    {
        var s = await _sut.RecordSuccessAsync("anthropic", null);
        s.LastSuccess.Should().NotBeNull();
        s.LastSuccess!.Value.Should().Be(Start);
    }

    // ── In-memory test double for IProviderHealthRepository ──────────────────

    /// <summary>
    /// Simple dictionary-backed fake of <see cref="IProviderHealthRepository"/>
    /// that mirrors the semantics of the real EF implementation (change-tracked
    /// entity returned from <see cref="GetOrCreateAsync"/>, no auto-persist until
    /// <see cref="SaveChangesAsync"/>).
    /// </summary>
    private sealed class InMemoryHealthRepo : IProviderHealthRepository
    {
        private readonly Dictionary<(string, Guid?), ProviderHealth> _rows = new();

        public Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)
        {
            _rows.TryGetValue((providerKey, tenantId), out var row);
            return Task.FromResult<ProviderHealth?>(row);
        }

        public Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)
            => Task.FromResult(_rows.Values.Where(r => r.TenantId == tenantId).ToList());

        public Task<ProviderHealth> GetOrCreateAsync(string providerKey, Guid? tenantId)
        {
            if (!_rows.TryGetValue((providerKey, tenantId), out var row))
            {
                row = new ProviderHealth
                {
                    Id = Guid.NewGuid(),
                    ProviderKey = providerKey,
                    TenantId = tenantId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                _rows[(providerKey, tenantId)] = row;
            }
            return Task.FromResult(row);
        }

        public Task SaveChangesAsync() => Task.CompletedTask;
    }
}
