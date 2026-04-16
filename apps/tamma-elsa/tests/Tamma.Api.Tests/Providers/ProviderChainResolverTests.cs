using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Providers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Unit tests for <see cref="ProviderChainResolver"/>. Uses a mocked
/// <see cref="IAgentConfigRepository"/> and a stubbed
/// <see cref="ICircuitBreakerService"/> so we can dictate per-provider states.
/// </summary>
[TestFixture]
public class ProviderChainResolverTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private Mock<IAgentConfigRepository> _configRepo = null!;
    private FakeCircuitBreaker _breaker = null!;
    private ProviderChainResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _configRepo = new Mock<IAgentConfigRepository>();
        _breaker = new FakeCircuitBreaker();
        _sut = new ProviderChainResolver(_configRepo.Object, _breaker);
    }

    private void SetupConfig(string json)
    {
        _configRepo
            .Setup(r => r.ResolveAsync(TenantId))
            .ReturnsAsync((new AgentConfig { TenantId = TenantId, Config = json }, "tenant"));
    }

    [Test]
    public async Task ResolveAsync_EmptyConfig_ReturnsEmptyChainErrorCode()
    {
        SetupConfig("{}");

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().BeEmpty();
        result.ErrorCode.Should().Be("EMPTY_PROVIDER_CHAIN");
        result.HasCandidates.Should().BeFalse();
    }

    [Test]
    public async Task ResolveAsync_MissingChainsKey_ReturnsEmptyChainErrorCode()
    {
        SetupConfig("""{"other": "value"}""");

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.ErrorCode.Should().Be("EMPTY_PROVIDER_CHAIN");
    }

    [Test]
    public async Task ResolveAsync_RoleActionChain_PreservesOrder()
    {
        SetupConfig("""
        {
          "chains": {
            "developer": {
              "code_generation": [
                {"provider": "anthropic", "model": "claude-sonnet-4"},
                {"provider": "openai",    "model": "gpt-4o"},
                {"provider": "openrouter","model": "gpt-4-turbo"}
              ]
            }
          }
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().HaveCount(3);
        result.Ordered[0].Provider.Provider.Should().Be("anthropic");
        result.Ordered[0].Provider.Model.Should().Be("claude-sonnet-4");
        result.Ordered[1].Provider.Provider.Should().Be("openai");
        result.Ordered[2].Provider.Provider.Should().Be("openrouter");
        result.Ordered.Should().OnlyContain(e => e.Reason == ChainReason.Unknown);
    }

    [Test]
    public async Task ResolveAsync_OpenProviderSkipped()
    {
        SetupConfig("""
        {
          "chains": {
            "developer": {
              "code_generation": [
                {"provider": "anthropic"},
                {"provider": "openai"}
              ]
            }
          }
        }
        """);

        _breaker.Set("anthropic", CircuitBreakerState.Open);
        _breaker.Set("openai", CircuitBreakerState.Closed);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().ContainSingle();
        result.Ordered[0].Provider.Provider.Should().Be("openai");
        result.Skipped.Should().ContainSingle(e =>
            e.Provider.Provider == "anthropic" && e.Reason == ChainReason.CircuitOpen);
    }

    [Test]
    public async Task ResolveAsync_HalfOpenAppendedAfterClosed()
    {
        SetupConfig("""
        {
          "chains": {
            "developer": {
              "code_generation": [
                {"provider": "anthropic"},
                {"provider": "openai"},
                {"provider": "openrouter"}
              ]
            }
          }
        }
        """);

        _breaker.Set("anthropic", CircuitBreakerState.HalfOpen);
        _breaker.Set("openai", CircuitBreakerState.Closed);
        _breaker.Set("openrouter", CircuitBreakerState.Closed);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().HaveCount(3);
        result.Ordered[0].Provider.Provider.Should().Be("openai");
        result.Ordered[1].Provider.Provider.Should().Be("openrouter");
        result.Ordered[2].Provider.Provider.Should().Be("anthropic");
        result.Ordered[2].Reason.Should().Be(ChainReason.HalfOpenProbeCandidate);
    }

    [Test]
    public async Task ResolveAsync_AllOpen_ReturnsNoAvailableProviderError()
    {
        SetupConfig("""
        {
          "chains": {
            "developer": {
              "code_generation": [
                {"provider": "anthropic"},
                {"provider": "openai"}
              ]
            }
          }
        }
        """);

        _breaker.Set("anthropic", CircuitBreakerState.Open);
        _breaker.Set("openai", CircuitBreakerState.Open);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().BeEmpty();
        result.Skipped.Should().HaveCount(2);
        result.ErrorCode.Should().Be("NO_AVAILABLE_PROVIDER");
    }

    [Test]
    public async Task ResolveAsync_FallsBackToRoleDefault()
    {
        SetupConfig("""
        {
          "chains": {
            "developer": {
              "default": [{"provider": "anthropic"}]
            }
          }
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().ContainSingle(e => e.Provider.Provider == "anthropic");
    }

    [Test]
    public async Task ResolveAsync_FallsBackToGlobalDefault()
    {
        SetupConfig("""
        {
          "chains": {
            "default": [{"provider": "anthropic"}, {"provider": "openai"}]
          }
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered.Should().HaveCount(2);
    }

    [Test]
    public async Task ResolveAsync_EmptyRole_Throws()
    {
        SetupConfig("{}");
        var act = async () => await _sut.ResolveAsync(TenantId, " ", "code_generation");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ResolveAsync_EmptyAction_Throws()
    {
        SetupConfig("{}");
        var act = async () => await _sut.ResolveAsync(TenantId, "developer", "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ResolveAsync_UnknownTenant_UsesSystemConfig()
    {
        _configRepo
            .Setup(r => r.GetAsync(null))
            .ReturnsAsync(new AgentConfig
            {
                TenantId = null,
                Config = """
                {
                  "chains": {
                    "default": [{"provider": "anthropic"}]
                  }
                }
                """,
            });

        var result = await _sut.ResolveAsync(null, "developer", "code_generation");

        result.Ordered.Should().ContainSingle(e => e.Provider.Provider == "anthropic");
    }

    [Test]
    public async Task ResolveAsync_HandleKeyIncludesModel()
    {
        SetupConfig("""
        {
          "chains": {
            "default": [{"provider": "anthropic", "model": "claude-sonnet-4"}]
          }
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered[0].Provider.Key.Should().Be("anthropic:claude-sonnet-4");
    }

    [Test]
    public async Task ResolveAsync_HandleKeyOmitsModelWhenNull()
    {
        SetupConfig("""
        {
          "chains": {
            "default": [{"provider": "anthropic"}]
          }
        }
        """);

        var result = await _sut.ResolveAsync(TenantId, "developer", "code_generation");

        result.Ordered[0].Provider.Key.Should().Be("anthropic");
    }

    // ── fake circuit breaker ─────────────────────────────────────────────────

    private sealed class FakeCircuitBreaker : ICircuitBreakerService
    {
        private readonly Dictionary<string, CircuitBreakerState> _states = new();

        public void Set(string key, CircuitBreakerState state) => _states[key] = state;

        public Task<CircuitBreakerStatus> GetStateAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default)
        {
            var state = _states.TryGetValue(providerKey, out var s) ? s : CircuitBreakerState.Closed;
            return Task.FromResult(new CircuitBreakerStatus(
                providerKey, state, 0, null, null, null, false));
        }

        public Task<CircuitBreakerStatus> RecordFailureAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CircuitBreakerStatus> RecordSuccessAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryProbeAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CircuitBreakerStatus> ResetAsync(
            string providerKey, Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CircuitBreakerStatus>> ListAsync(
            Guid? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
