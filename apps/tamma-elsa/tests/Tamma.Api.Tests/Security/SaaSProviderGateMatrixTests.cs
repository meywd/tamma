using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 — the canonical regression guards: the full
/// mode × auth-model × known/unknown × entitlement matrix; the typed-decision →
/// §2.4 status mapping; the 34-11 entity-swap contract-neutrality; and the
/// credential-safety assertion (the gate has NO secret/credential dependency).
/// </summary>
[TestFixture]
public class SaaSProviderGateMatrixTests
{
    /// <summary>One matrix cell: inputs → expected decision shape.</summary>
    public sealed record Cell(
        TammaMode Mode,
        string Provider,
        bool Entitled,
        bool ExpectAllowed,
        ProviderGateOutcome ExpectOutcome,
        int ExpectStatus,
        int ExpectEvents,
        int ExpectMetric);

    private static IEnumerable<TestCaseData> MatrixCells()
    {
        // Single-user: every provider allowed, zero side effects (entitlement
        // and provider type are irrelevant — mode short-circuits first).
        foreach (var provider in new[] { "anthropic", "claude-code", "unknown-x" })
        foreach (var entitled in new[] { true, false })
        {
            yield return new TestCaseData(new Cell(
                TammaMode.SingleUser, provider, entitled,
                ExpectAllowed: true, ProviderGateOutcome.Allowed, 200,
                ExpectEvents: 0, ExpectMetric: 0))
                .SetName($"SingleUser_{provider}_entitled={entitled}_allows");
        }

        // SaaS api-key (anthropic): allowed iff entitled, else 403.
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "anthropic", true,
            ExpectAllowed: true, ProviderGateOutcome.Allowed, 200, 0, 0))
            .SetName("SaaS_anthropic_entitled_allows");
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "anthropic", false,
            ExpectAllowed: false, ProviderGateOutcome.TenantNotEntitled, 403, 1, 1))
            .SetName("SaaS_anthropic_notEntitled_denies403");

        // SaaS cli-token (claude-code): denied 400 regardless of entitlement.
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "claude-code", true,
            ExpectAllowed: false, ProviderGateOutcome.SaasProviderNotAllowed, 400, 1, 1))
            .SetName("SaaS_claudeCode_entitled_denies400");
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "claude-code", false,
            ExpectAllowed: false, ProviderGateOutcome.SaasProviderNotAllowed, 400, 1, 1))
            .SetName("SaaS_claudeCode_notEntitled_denies400");

        // SaaS unknown: denied 400 fail-closed regardless of entitlement.
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "unknown-x", true,
            ExpectAllowed: false, ProviderGateOutcome.SaasProviderNotAllowed, 400, 1, 1))
            .SetName("SaaS_unknown_entitled_denies400_failclosed");
        yield return new TestCaseData(new Cell(
            TammaMode.SaaS, "unknown-x", false,
            ExpectAllowed: false, ProviderGateOutcome.SaasProviderNotAllowed, 400, 1, 1))
            .SetName("SaaS_unknown_notEntitled_denies400_failclosed");
    }

    [TestCaseSource(nameof(MatrixCells))]
    public async Task Matrix_with_static_lookup(Cell cell) =>
        await RunCell(cell, FakeAuthLookup.Default());

    /// <summary>
    /// 34-11 swap: the SAME matrix with an entity-shaped lookup (anthropic=api-key,
    /// claude-code=cli-token, everything else unknown) — proving the DI swap from
    /// StaticProviderAuthLookup to EntityProviderAuthLookup is contract-neutral.
    /// </summary>
    [TestCaseSource(nameof(MatrixCells))]
    public async Task Matrix_with_entity_shaped_lookup(Cell cell)
    {
        var entityLikeLookup = new FakeAuthLookup(new Dictionary<string, ProviderAuthModel?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = ProviderAuthModel.ApiKey,
            ["claude-code"] = ProviderAuthModel.CliToken,
        });
        await RunCell(cell, entityLikeLookup);
    }

    private static async Task RunCell(Cell cell, IProviderAuthLookup lookup)
    {
        var events = new RecordingGateEventRepository();
        using var metrics = new ProviderGatingMetrics();
        var gate = GateTestHelpers.Build(
            new StubMode(cell.Mode), lookup, new FakeEntitlement(cell.Entitled), events, metrics);

        var decision = await gate.InspectAsync(
            new ProviderGateContext(cell.Provider, "developer", "implement", Guid.NewGuid()));

        decision.Allowed.Should().Be(cell.ExpectAllowed);
        decision.Outcome.Should().Be(cell.ExpectOutcome);
        decision.HttpStatusHint.Should().Be(cell.ExpectStatus);
        events.Appended.Should().HaveCount(cell.ExpectEvents);
        metrics.GatedTotal.Should().Be(cell.ExpectMetric);
    }

    // ── Endpoint-mapping contract: typed decision drives the §2.4 status ────

    [Test]
    public void SaasProviderNotAllowed_maps_to_400_and_TenantNotEntitled_to_403()
    {
        var notAllowed = new ProviderGateDecision(
            false, ProviderGateOutcome.SaasProviderNotAllowed, "x", null, 400);
        var notEntitled = new ProviderGateDecision(
            false, ProviderGateOutcome.TenantNotEntitled, "y", ProviderAuthModel.ApiKey, 403);
        var allow = ProviderGateDecision.Allow(ProviderAuthModel.ApiKey);

        notAllowed.HttpStatusHint.Should().Be(400);
        notEntitled.HttpStatusHint.Should().Be(403);
        allow.HttpStatusHint.Should().Be(200);
        allow.Allowed.Should().BeTrue();
        allow.Reason.Should().BeNull();
    }

    // ── Credential-safety: the gate has NO credential/secret dependency ─────

    [Test]
    public void Gate_constructor_has_no_credential_or_secret_dependency()
    {
        var ctor = typeof(SaaSProviderGate).GetConstructors().Single();

        // Collect the SHORT type name of each ctor parameter plus, for generics,
        // the short names of the generic arguments (so ILogger<SaaSProviderGate>
        // contributes "ILogger`1" + "SaaSProviderGate"). We deliberately avoid the
        // assembly-qualified FullName because it carries "...PublicKeyToken=null".
        var names = new List<string>();
        foreach (var p in ctor.GetParameters())
        {
            names.Add(p.ParameterType.Name);
            if (p.ParameterType.IsGenericType)
                names.AddRange(p.ParameterType.GetGenericArguments().Select(a => a.Name));
        }

        // No dependency type name may hint at a secret / credential / cabinet /
        // key / token / cipher — the gate operates on provider names + mode only.
        string[] forbidden =
            { "Credential", "Secret", "Cabinet", "ApiKey", "Cipher", "Vault", "Kek" };

        foreach (var name in names)
        foreach (var bad in forbidden)
        {
            name.Should().NotContainEquivalentOf(bad,
                $"the gate must have no '{bad}'-shaped dependency (credential safety, AC9)");
        }

        // Sanity: the gate's dependency set is exactly the mode + auth lookup +
        // entitlement + events + metrics + logger — nothing secret-bearing.
        ctor.GetParameters().Select(p => p.ParameterType).Should().BeEquivalentTo(new[]
        {
            typeof(ITammaModeProvider),
            typeof(IProviderAuthLookup),
            typeof(ITenantProviderEntitlement),
            typeof(Tamma.Data.Repositories.IEventRepository),
            typeof(ProviderGatingMetrics),
            typeof(Microsoft.Extensions.Logging.ILogger<SaaSProviderGate>),
        });
    }

    [Test]
    public async Task Gate_decision_carries_no_secret_material_in_reason()
    {
        var events = new RecordingGateEventRepository();
        using var metrics = new ProviderGatingMetrics();
        var gate = GateTestHelpers.Build(
            new StubMode(TammaMode.SaaS), FakeAuthLookup.Default(),
            new FakeEntitlement(true), events, metrics);

        var decision = await gate.InspectAsync(
            new ProviderGateContext("claude-code", "developer", "implement", Guid.NewGuid()));

        // The reason + event payload reference only provider name / mode / reason
        // codes — never key material.
        decision.Reason.Should().NotBeNullOrWhiteSpace();
        decision.Reason!.ToLowerInvariant().Should().NotContain("sk-");
        events.Appended.Should().ContainSingle();
        events.Appended[0].Data.ToLowerInvariant().Should().NotContain("sk-");
        events.Appended[0].Tags.ToLowerInvariant().Should().NotContain("sk-");
    }
}
