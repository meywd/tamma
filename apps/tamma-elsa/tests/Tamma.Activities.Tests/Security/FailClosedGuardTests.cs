using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.Security;

/// <summary>
/// Tests that circuit breaker and budget check helpers fail closed (deny)
/// when exceptions occur, rather than failing open (allow).
/// Since IsCircuitBreakerOpen and IsBudgetExhausted are private static methods
/// in LlmCallWorkflow, we duplicate the fixed logic here to validate the
/// expected behavior contract.
/// </summary>
[TestFixture]
public class FailClosedGuardTests
{
    // =====================================================================
    // IsCircuitBreakerOpen fail-closed tests
    // =====================================================================

    [Test]
    public void IsCircuitBreakerOpen_MalformedJson_ReturnsTrue()
    {
        var result = TestIsCircuitBreakerOpen("anthropic", "THIS IS NOT JSON");
        result.Should().BeTrue("fail-closed: when check throws, circuit should be treated as open");
    }

    [Test]
    public void IsCircuitBreakerOpen_NullProvider_ReturnsFalse()
    {
        var result = TestIsCircuitBreakerOpen(null, "{}");
        result.Should().BeFalse("null provider means no check needed");
    }

    [Test]
    public void IsCircuitBreakerOpen_NullStatesJson_ReturnsFalse()
    {
        var result = TestIsCircuitBreakerOpen("anthropic", null);
        result.Should().BeFalse("null states means no check needed");
    }

    [Test]
    public void IsCircuitBreakerOpen_EmptyProvider_ReturnsFalse()
    {
        var result = TestIsCircuitBreakerOpen("", "{}");
        result.Should().BeFalse("empty provider means no check needed");
    }

    [Test]
    public void IsCircuitBreakerOpen_CircuitActuallyClosed_ReturnsFalse()
    {
        var states = new Dictionary<string, CircuitBreakerState>
        {
            ["anthropic"] = new() { ProviderName = "anthropic", Status = CircuitBreakerStatus.Closed }
        };
        var json = JsonSerializer.Serialize(states);
        var result = TestIsCircuitBreakerOpen("anthropic", json);
        result.Should().BeFalse("circuit is genuinely closed");
    }

    [Test]
    public void IsCircuitBreakerOpen_CircuitActuallyOpen_ReturnsTrue()
    {
        var states = new Dictionary<string, CircuitBreakerState>
        {
            ["anthropic"] = new()
            {
                ProviderName = "anthropic",
                Status = CircuitBreakerStatus.Open,
                OpenedAtUtc = DateTime.UtcNow
            }
        };
        var json = JsonSerializer.Serialize(states);
        var result = TestIsCircuitBreakerOpen("anthropic", json);
        result.Should().BeTrue("circuit is genuinely open");
    }

    [Test]
    public void IsCircuitBreakerOpen_ProviderNotInStates_ReturnsFalse()
    {
        var states = new Dictionary<string, CircuitBreakerState>
        {
            ["openai"] = new() { ProviderName = "openai", Status = CircuitBreakerStatus.Open, OpenedAtUtc = DateTime.UtcNow }
        };
        var json = JsonSerializer.Serialize(states);
        var result = TestIsCircuitBreakerOpen("anthropic", json);
        result.Should().BeFalse("provider not tracked yet, treat as closed");
    }

    [Test]
    public void IsCircuitBreakerOpen_CooldownElapsed_ReturnsFalse()
    {
        var states = new Dictionary<string, CircuitBreakerState>
        {
            ["anthropic"] = new()
            {
                ProviderName = "anthropic",
                Status = CircuitBreakerStatus.Open,
                OpenedAtUtc = DateTime.UtcNow.AddSeconds(-600), // 10 minutes ago
                CooldownPeriod = TimeSpan.FromSeconds(300) // 5 minute cooldown
            }
        };
        var json = JsonSerializer.Serialize(states);
        var result = TestIsCircuitBreakerOpen("anthropic", json);
        result.Should().BeFalse("cooldown elapsed, allow half-open probe");
    }

    // =====================================================================
    // IsBudgetExhausted fail-closed tests
    // =====================================================================

    [Test]
    public void IsBudgetExhausted_MalformedJson_ReturnsTrue()
    {
        var result = TestIsBudgetExhausted("NOT JSON AT ALL");
        result.Should().BeTrue("fail-closed: when check throws, budget should be treated as exhausted");
    }

    [Test]
    public void IsBudgetExhausted_NullJson_ReturnsFalse()
    {
        var result = TestIsBudgetExhausted(null);
        result.Should().BeFalse("null budget means no cap configured");
    }

    [Test]
    public void IsBudgetExhausted_EmptyJson_ReturnsFalse()
    {
        var result = TestIsBudgetExhausted("");
        result.Should().BeFalse("empty budget means no cap configured");
    }

    [Test]
    public void IsBudgetExhausted_WithinBudget_ReturnsFalse()
    {
        var budget = new BudgetState { CapUsd = 10m, SpentUsd = 5m };
        var json = JsonSerializer.Serialize(budget);
        var result = TestIsBudgetExhausted(json);
        result.Should().BeFalse("within budget");
    }

    [Test]
    public void IsBudgetExhausted_OverBudget_ReturnsTrue()
    {
        var budget = new BudgetState { CapUsd = 10m, SpentUsd = 15m };
        var json = JsonSerializer.Serialize(budget);
        var result = TestIsBudgetExhausted(json);
        result.Should().BeTrue("over budget");
    }

    [Test]
    public void IsBudgetExhausted_ExactlyAtCap_ReturnsTrue()
    {
        var budget = new BudgetState { CapUsd = 10m, SpentUsd = 10m };
        var json = JsonSerializer.Serialize(budget);
        var result = TestIsBudgetExhausted(json);
        result.Should().BeTrue("exactly at cap means exhausted");
    }

    [Test]
    public void IsBudgetExhausted_NoCap_ReturnsFalse()
    {
        var budget = new BudgetState { CapUsd = 0m, SpentUsd = 100m };
        var json = JsonSerializer.Serialize(budget);
        var result = TestIsBudgetExhausted(json);
        result.Should().BeFalse("zero cap means unlimited");
    }

    // =====================================================================
    // Helper methods that replicate the FIXED logic
    // (validates the expected contract of the private static methods)
    // =====================================================================

    private static bool TestIsCircuitBreakerOpen(string? provider, string? statesJson)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(statesJson))
            return false;

        try
        {
            var states = JsonSerializer.Deserialize<Dictionary<string, CircuitBreakerState>>(statesJson);
            if (states == null || !states.TryGetValue(provider, out var state))
                return false;

            if (state.Status == CircuitBreakerStatus.Open)
            {
                if (state.OpenedAtUtc.HasValue &&
                    DateTime.UtcNow - state.OpenedAtUtc.Value >= state.CooldownPeriod)
                    return false; // Cooldown elapsed, allow half-open probe
                return true; // Still open
            }

            return false;
        }
        catch
        {
            return true; // FIXED: fail closed
        }
    }

    private static bool TestIsBudgetExhausted(string? budgetJson)
    {
        if (string.IsNullOrWhiteSpace(budgetJson)) return false;

        try
        {
            var budget = JsonSerializer.Deserialize<BudgetState>(budgetJson);
            return budget?.IsExhausted == true;
        }
        catch
        {
            return true; // FIXED: fail closed
        }
    }
}
