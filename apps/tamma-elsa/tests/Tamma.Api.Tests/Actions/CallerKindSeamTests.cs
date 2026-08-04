using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Infrastructure;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-13 AC2 + AC3 — the two-direction pin at Seam C, on the
/// <c>AutonomyGateEndpointFilterTests</c> harness pattern extended with the REAL
/// <see cref="AutonomyGateService"/> (stub snapshot/rules providers), so the
/// dial comparison, the caller-kind resolution
/// (<see cref="CallerKindResolver"/> inside <c>AutonomyGateEnforcement</c>) and
/// the audit emission are all production code — only I/O is stubbed.
///
/// <para><b>Why route SHAPES in a harness and not the production routes AC2
/// names</b> (plan D6 / Blocked #1): <c>PUT /api/admin/scheduled-triggers/{id}</c>
/// is DELIBERATELY unbound (KnownUngovernedEndpoints, decided 2026-08-01) and
/// the tracker bindings are owned by Story 44-2 — binding either here would move
/// the 216/21 pins, which AC9 forbids. The harness carries the real route shape,
/// the real binding metadata, the real filter and the real gate; when 44-2 (or a
/// schedule story) binds the production route, these tests become redundant with
/// the production path, not wrong.</para>
///
/// <para><b>Both directions live in ONE class</b> — AC2's drift-proofing: the
/// human-passes half and the llm-gated half cannot be edited apart.</para>
/// </summary>
[TestFixture]
public class CallerKindSeamTests
{
    // ── Stubs (I/O only — the gate, evaluator, resolver and events are real) ─

    private sealed class FixedPrincipal : IGovernancePrincipalResolver
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public Task<GovernancePrincipal> ResolveAsync(
            ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(GovernancePrincipal.ForUser(UserId));
    }

    /// <summary>A snapshot provider whose platform ceiling pins ONE action to
    /// AlwaysHuman — the level forced above the dial, per D6.</summary>
    private sealed class CeilingSnapshots(string actionWire) : IGovernancePolicySnapshotProvider
    {
        private GovernancePolicySnapshot Snapshot { get; } =
            GovernancePolicySnapshot.FromSuccessfulRead(
                new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                {
                    [actionWire] = new ActionAssignmentValue(
                        AutonomyDial.AlwaysHuman, Enforce: true, Enabled: null, AllowedRoles: null),
                },
                new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
                new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
                new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal) => Snapshot;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId) => Snapshot;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ShippedRules : IAcceptanceRulesResolver
    {
        private static ResolvedAcceptanceRules Resolved => new(
            AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "base",
            DateTimeOffset.UtcNow);

        public Task<ResolvedAcceptanceRules> ResolveAsync(
            Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default)
            => Task.FromResult(Resolved);
        public Task<ResolvedAcceptanceRules> ResolveForTenantAsync(
            Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default)
            => Task.FromResult(Resolved);
        public Task<ResolvedAcceptanceRules> ResolveBaseAsync(
            Guid? userId, CancellationToken ct = default)
            => Task.FromResult(Resolved);
        public Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(
            Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(Resolved);
    }

    private sealed class RecordingEvents : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
    }

    // ── Harness — the real filter over a governed, enforcement-opted-in shape ─

    private static (DefaultHttpContext Http, RecordingEvents Events) Context(
        string method, string path, ActionKey action,
        AuthPrincipal? typedPrincipal, ClaimsPrincipal? user)
    {
        var events = new RecordingEvents();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new ActionGateEventsService(events));
        services.AddScoped<IGovernancePrincipalResolver, FixedPrincipal>();
        // THE REAL GATE over the real evaluator: only snapshot/rules I/O is
        // stubbed, so the dial comparison and the caller-kind short-circuits
        // are the production path.
        services.AddScoped<IAutonomyGate>(sp => new AutonomyGateService(
            sp.GetRequiredService<IGovernancePrincipalResolver>(),
            new CeilingSnapshots(action.ToWire()),
            new ShippedRules(),
            sp.GetRequiredService<ActionGateEventsService>()));

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        if (typedPrincipal is not null) http.SetAuthPrincipal(typedPrincipal);
        http.Request.Method = method;
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        http.Features.Set<IEndpointFeature>(new EndpointFeature
        {
            Endpoint = new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new GovernanceEnforcementMetadata(),
                    new ActionGateMetadata(action)),
                "caller-kind-seam-test"),
        });
        return (http, events);
    }

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    private sealed class TestInvocationContext(HttpContext http) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }

    private static async Task<(int Status, string Body, bool HandlerRan)> RunFilter(
        DefaultHttpContext http)
    {
        var handlerRan = false;
        var result = await new AutonomyGateEndpointFilter().InvokeAsync(
            new TestInvocationContext(http),
            _ => { handlerRan = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        if (result is IResult r) await r.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, body, handlerRan);
    }

    /// <summary>A production-shaped human credential: the dashboard JWT plane
    /// (verbatim <c>sub</c>, MapInboundClaims=false).</summary>
    private static ClaimsPrincipal HumanJwt() => new(new ClaimsIdentity(
        new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim("role", "admin"),
        },
        authenticationType: "Bearer"));

    /// <summary>A production-shaped engine credential: the typed service
    /// principal <c>ApiKeyAuthHandler</c> mints for the service-scope
    /// <c>Tamma:ApiToken</c>, plus its claims shape.</summary>
    private static (AuthPrincipal Typed, ClaimsPrincipal Claims) EngineCredential()
    {
        var typed = new ServiceAuthPrincipal(
            Guid.NewGuid(), "tamma-engine", Array.Empty<string>(), null);
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "tamma-engine"),
                new Claim("scope", "service"),
            },
            authenticationType: "ApiKey"));
        return (typed, claims);
    }

    private static Dictionary<string, string?> Tags(DomainEvent evt) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!)!;

    // ====================================================================
    // AC2 — ONE test, BOTH directions, run for a HUMAN route shape and a
    // DUAL (tracker) route shape.
    // ====================================================================

    [TestCase("PUT", "/api/admin/scheduled-triggers/7f3f2f9e-0000-0000-0000-000000000001",
        "effect:schedule.update",
        TestName = "TheSameRequest_HumanPasses_EngineTokenIsGated(schedule.update)")]
    [TestCase("PATCH", "/api/work-items/7f3f2f9e-0000-0000-0000-000000000002",
        "effect:tracker.work-item.update",
        TestName = "TheSameRequest_HumanPasses_EngineTokenIsGated(tracker.work-item.update)")]
    public async Task TheSameRequest_HumanPasses_EngineTokenIsGated(
        string method, string path, string actionWire)
    {
        var action = ActionKey.Parse(actionWire);

        // Direction one: a person, on a route whose level sits ABOVE the dial
        // (platform ceiling AlwaysHuman), passes at dial Min.
        var (humanHttp, humanEvents) = Context(
            method, path, action, typedPrincipal: null, user: HumanJwt());
        var human = await RunFilter(humanHttp);

        human.HandlerRan.Should().BeTrue(
            $"a HUMAN on {actionWire} must never be gated — the dial is a control on model "
            + "autonomy, not a lock on the product (43-11 Amendment 4)");
        human.Status.Should().NotBe(StatusCodes.Status409Conflict);
        var humanAllow = humanEvents.Appended
            .Should().ContainSingle(e => e.Type == ActionGateEventsService.AllowedType).Which;
        Tags(humanAllow)["callerKind"].Should().Be("human");
        JsonDocument.Parse(humanAllow.Data!).RootElement.GetProperty("reason").GetString()
            .Should().Be(AutonomyGateEvaluator.ReasonCallerHuman);

        // Direction two: the SAME request under the engine token is gated.
        var (typed, claims) = EngineCredential();
        var (engineHttp, engineEvents) = Context(
            method, path, action, typedPrincipal: typed, user: claims);
        var engine = await RunFilter(engineHttp);

        engine.HandlerRan.Should().BeFalse(
            $"the engine token on {actionWire} is the LLM (fail-closed) and the level is above "
            + "the dial");
        engine.Status.Should().Be(StatusCodes.Status409Conflict);
        engine.Body.Should().Contain("ACTION.GATE.REQUIRES_HUMAN");
        var engineBlock = engineEvents.Appended
            .Should().ContainSingle(e => e.Type == ActionGateEventsService.RequiresHumanType).Which;
        Tags(engineBlock)["callerKind"].Should().Be("llm");
    }

    // ====================================================================
    // AC3 — an engine-token call with NO caller-kind declaration anywhere
    // consults the dial; the audit tag proves the default was Llm.
    // ====================================================================

    [Test]
    public async Task AnEngineToken_WithNoDeclaration_ConsultsTheDial()
    {
        var action = ActionKey.Parse("effect:schedule.update");
        var (typed, claims) = EngineCredential();
        var (http, events) = Context(
            "PUT", "/api/admin/scheduled-triggers/7f3f2f9e-0000-0000-0000-000000000003",
            action, typedPrincipal: typed, user: claims);

        var (status, _, handlerRan) = await RunFilter(http);

        // The 409 half alone would pass vacuously — the tag assertion is what
        // proves the DIAL was consulted for an undeclared caller: flip the
        // AutonomyQuery.Caller default (or make the resolver answer Human for
        // service principals) and this goes red.
        status.Should().Be(StatusCodes.Status409Conflict);
        handlerRan.Should().BeFalse();
        var block = events.Appended
            .Should().ContainSingle(e => e.Type == ActionGateEventsService.RequiresHumanType).Which;
        Tags(block)["callerKind"].Should().Be("llm");
        JsonDocument.Parse(block.Data!).RootElement.GetProperty("reason").GetString()
            .Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman,
                "the block must be the DIAL's answer (the ceiling row), not a degradation "
                + "artifact — that is what 'consults the dial' means");
    }
}
