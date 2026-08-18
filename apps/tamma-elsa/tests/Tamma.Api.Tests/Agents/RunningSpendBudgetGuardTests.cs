using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// The spend ceiling that actually blocks model calls.
///
/// <para><b>Why here.</b> Every model call the autonomous loop makes goes engine →
/// <c>POST /api/v1/llm/call</c> → <c>ManagedAgent.RunAsync</c> step 1b →
/// <see cref="IBudgetGuard"/>. The shipped <see cref="PerCallBudgetGuard"/> returned
/// <c>true</c> unconditionally (its own doc named "consult running tenant spend" as the
/// 32-9 follow-on), and the other two budget checks in the codebase cap nothing: the
/// engine workflow's gate meters a per-call bucket re-seeded on every call, and
/// <c>ProviderChainResolver</c>'s budget read only annotates a chain-inspection endpoint.
/// So nothing capped the loop. These pin the replacement.</para>
/// </summary>
[TestFixture]
public class RunningSpendBudgetGuardTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task Allows_WhenSpendIsUnderTheAccountLimit()
    {
        var guard = Guard(spent: 10m, limit: 100m);

        (await guard.IsWithinBudgetAsync(Tenant, 0m)).Should().BeTrue();
    }

    [Test]
    public async Task Denies_WhenSpendReachesTheAccountLimit()
    {
        var guard = Guard(spent: 100m, limit: 100m);

        (await guard.IsWithinBudgetAsync(Tenant, 0m)).Should().BeFalse(
            "ManagedAgent turns a false into BUDGET_EXCEEDED and never invokes the provider");
    }

    [Test]
    public async Task Denies_WhenSpendReachesTheAdlCeiling_evenWithNoAccountLimit()
    {
        // The single-user default account limit is 0 (= unlimited), which is exactly the
        // deployment the autonomous loop runs in — Adl:MaxSpendUsd is what caps it there.
        var guard = Guard(spent: 30m, limit: 0m, config: new Dictionary<string, string?>
        {
            [AdlSpendCeiling.MaxSpendKey] = "25",
        });

        (await guard.IsWithinBudgetAsync(Tenant, 0m)).Should().BeFalse();
    }

    [Test]
    public async Task UsesTheConfiguredBudgetOwner_WhenTheRequestCarriesNoTenant()
    {
        var owner = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics.Setup(d => d.GetBudgetAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status(spent: 100m, limit: 50m));

        var guard = new RunningSpendBudgetGuard(diagnostics.Object, Config(new Dictionary<string, string?>
        {
            [AdlSpendCeiling.BudgetOwnerKey] = owner.ToString(),
        }));

        (await guard.IsWithinBudgetAsync(tenantId: null, 0m)).Should().BeFalse(
            "single-user mode never attaches a tenant, so without this fallback the budget "
            + "check was skipped entirely and the loop ran uncapped");
    }

    [Test]
    public async Task Allows_AndNeverQueries_WhenThereIsNoBucketToMeter()
    {
        var diagnostics = new Mock<IDiagnosticsService>(MockBehavior.Strict);

        var guard = new RunningSpendBudgetGuard(diagnostics.Object, Config(new Dictionary<string, string?>()));

        (await guard.IsWithinBudgetAsync(tenantId: null, 0m)).Should().BeTrue(
            "denying every model call because no budget owner is configured would brick a "
            + "fresh deployment; it warns instead");
        diagnostics.VerifyNoOtherCalls();
    }

    [Test]
    public async Task FailsClosed_WhenAConfiguredCeilingCannotBeEvaluated()
    {
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics.Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("diagnostics down"));

        var guard = new RunningSpendBudgetGuard(diagnostics.Object, Config(new Dictionary<string, string?>
        {
            [AdlSpendCeiling.MaxSpendKey] = "25",
        }));

        (await guard.IsWithinBudgetAsync(Tenant, 0m)).Should().BeFalse(
            "an operator who set a cap must not be silently uncapped by an outage");
    }

    [Test]
    public async Task FailsOpen_WhenEvaluationBreaksButNoCeilingWasAskedFor()
    {
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics.Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("diagnostics down"));

        var guard = new RunningSpendBudgetGuard(diagnostics.Object, Config(new Dictionary<string, string?>()));

        (await guard.IsWithinBudgetAsync(Tenant, 0m)).Should().BeTrue(
            "with no cap there is nothing to evaluate — blocking every model call on a "
            + "diagnostics blip would be a self-inflicted outage");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static RunningSpendBudgetGuard Guard(
        decimal spent, decimal limit, Dictionary<string, string?>? config = null)
    {
        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics.Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status(spent, limit));
        return new RunningSpendBudgetGuard(diagnostics.Object, Config(config ?? new Dictionary<string, string?>()));
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static BudgetStatus Status(decimal spent, decimal limit) => new(
        AccountId: Tenant,
        PeriodStart: DateTime.UtcNow.AddDays(-30),
        PeriodEnd: DateTime.UtcNow.AddDays(30),
        Spent: spent,
        Limit: limit,
        Remaining: Math.Max(0m, limit - spent),
        PercentUsed: limit > 0 ? (double)(spent / limit) * 100 : 0,
        AlertThreshold: 0.8,
        ShouldAlert: false,
        IsOverBudget: limit > 0 && spent > limit);
}
