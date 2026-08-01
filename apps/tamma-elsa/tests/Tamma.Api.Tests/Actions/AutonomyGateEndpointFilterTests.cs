using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Infrastructure;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-9 <b>Seam C</b> (AC7, AC8) — the enforcement filter, driven end to end
/// through its PUBLIC surface (build an endpoint, run the filter, execute the
/// result) rather than through a re-implementation of its rule.
///
/// <para>Both authoring planes are exercised: the minimal-API
/// <see cref="AutonomyGateEndpointFilter"/> and the controller-action
/// <see cref="EnforcesGovernanceAttribute"/>. They must render the SAME denial —
/// an <c>IEndpointFilter</c> does not run for an MVC endpoint, so the two shapes
/// are genuinely different code, and the whole point of D15's reasoning #4 is
/// that a design covering only one of them reads as covering both.</para>
/// </summary>
[TestFixture]
public class AutonomyGateEndpointFilterTests
{
    // ── Doubles ─────────────────────────────────────────────────────────────

    private sealed class ScriptedGate(AutonomyDecision? decision, bool throws = false) : IAutonomyGate
    {
        public List<AutonomyQuery> Queries { get; } = new();
        public Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            if (throws) throw new InvalidOperationException("control plane unreachable");
            return Task.FromResult(decision!);
        }
    }

    private sealed class FixedPrincipal : IGovernancePrincipalResolver
    {
        public Task<GovernancePrincipal> ResolveAsync(
            ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(GovernancePrincipal.ForUser(Guid.NewGuid()));
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

    private static readonly ActionKey BranchCreate =
        new(ActionNamespace.Effect, ExternalEffect.GitBranchCreate.ToWire());

    private static AutonomyDecision Blocked(
        AutonomyOutcome outcome = AutonomyOutcome.RequiresHuman, bool enforced = true) =>
        new(outcome, BranchCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating,
            AutonomyLevel: AutonomyDial.Min, EffectiveMinAutonomy: AutonomyDial.AlwaysHuman,
            ActionAssignmentSource.PlatformCeiling,
            Enforced: enforced, Enabled: true, AllowedRoles: null, Reason: "always-human");

    private static AutonomyDecision Allowed() =>
        new(AutonomyOutcome.Automated, BranchCreate, ActionGroup.SourceControlWrite,
            ActionRisk.Mutating, AutonomyDial.Min, AutonomyDial.Min,
            ActionAssignmentSource.SystemDefault,
            Enforced: true, Enabled: true, AllowedRoles: null, Reason: "at-or-above-min-autonomy");

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Build an <see cref="HttpContext"/> that looks like a request to a governed,
    /// enforcement-opted-in route.
    /// </summary>
    private static (DefaultHttpContext Http, RecordingEvents Events) Context(
        IAutonomyGate? gate,
        bool withBinding = true,
        ClaimsPrincipal? user = null,
        string? correlationId = null,
        IActionAuthorizationRequests? authorizations = null)
    {
        var events = new RecordingEvents();
        var services = new ServiceCollection();
        services.AddLogging();
        if (gate is not null) services.AddScoped(_ => gate);
        services.AddScoped<IGovernancePrincipalResolver, FixedPrincipal>();
        services.AddScoped(_ => new ActionGateEventsService(events));
        if (authorizations is not null) services.AddSingleton(authorizations);

        var metadata = new List<object> { new GovernanceEnforcementMetadata() };
        if (withBinding) metadata.Add(new ActionGateMetadata(BranchCreate));

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        http.Request.Method = "POST";
        http.Request.Path = "/api/v1/git/acme/widget/branches";
        if (correlationId is not null)
        {
            http.Request.Headers["X-Tamma-Correlation-Id"] = correlationId;
        }
        http.Response.Body = new MemoryStream();
        http.Features.Set<IEndpointFeature>(new EndpointFeature
        {
            Endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"),
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

    /// <summary>Run the minimal-API filter and materialise the HTTP outcome.</summary>
    private static async Task<(int Status, string Body, bool HandlerRan)> RunFilter(DefaultHttpContext http)
    {
        var handlerRan = false;
        var filter = new AutonomyGateEndpointFilter();
        var result = await filter.InvokeAsync(
            new TestInvocationContext(http),
            _ => { handlerRan = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        if (result is IResult r) await r.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, body, handlerRan);
    }

    // ====================================================================
    // AC8 — the denial shape
    // ====================================================================

    [Test]
    public async Task Denial_returns_409()
    {
        var (http, _) = Context(new ScriptedGate(Blocked()));

        var (status, body, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict,
            "409 rather than 403 because the CALLER is authorized — the SYSTEM is not yet "
            + "permitted to act autonomously; and never 202, because 202 is already a success "
            + "code on TammaApiClient and the engine would proceed as if the effect had happened");
        handlerRan.Should().BeFalse("a denied request must not reach the handler");
        body.Should().Contain("ACTION.GATE.REQUIRES_HUMAN");
    }

    [Test]
    public async Task Denial_body_carries_the_documented_code_and_fields()
    {
        var (http, _) = Context(new ScriptedGate(Blocked()));

        var (_, body, _) = await RunFilter(http);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("code").GetString().Should().Be("ACTION.GATE.REQUIRES_HUMAN");
        root.GetProperty("action").GetString().Should().Be(BranchCreate.ToWire());
        root.GetProperty("group").GetString().Should().Be(ActionGroup.SourceControlWrite.ToWire());
        root.GetProperty("effectiveMinAutonomy").GetInt32().Should().Be(AutonomyDial.AlwaysHuman);
        root.GetProperty("autonomyLevel").GetInt32().Should().Be(AutonomyDial.Min);
        root.TryGetProperty("authorizationId", out _).Should().BeTrue(
            "the field is always present; it is null only when there is no correlation id to key "
            + "a grant to, and a caller must be able to tell those apart");
    }

    [Test]
    public async Task ADenialWithACorrelationId_mintsThePendingRowTheCallerCanAct()
    {
        // AC8's authorizationId only means something if a row exists to decide.
        var authorizationId = Guid.NewGuid();
        var requests = new StubRequests(authorizationId);
        var (http, _) = Context(
            new ScriptedGate(Blocked()), correlationId: "run-42", authorizations: requests);

        var (_, body, _) = await RunFilter(http);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authorizationId").GetGuid().Should().Be(authorizationId);
        doc.RootElement.GetProperty("correlationId").GetString().Should().Be("run-42");
        requests.Calls.Should().ContainSingle();
    }

    [Test]
    public async Task ADenialWithoutACorrelationId_stillMintsARowUnderARouteDerivedCorrelation()
    {
        // PIN MOVED by adversarial review F5 (2026-08-01). It previously asserted
        // that a caller sending no correlation id gets a NULL authorizationId and
        // no row — which was true, and was the bug: NOT ONE opted-in route sends
        // the header or the query value (they are all engine mediation routes
        // called by TammaApiClient, which sets neither), so that was EVERY real
        // request. The 409 nonetheless told the caller to "Grant the pending
        // authorization (POST …/{id}/decide)" with no id and no row in existence:
        // an unactionable block, clearable only by editing policy.
        //
        // The old comment's reasoning still holds and is why the fix is a
        // DETERMINISTIC route-derived correlation rather than a per-request id:
        // the ledger is keyed by (principal, correlation, target), so the value
        // must be re-derivable by the retry that follows the human's grant.
        var requests = new StubRequests(Guid.NewGuid());
        var (http, _) = Context(new ScriptedGate(Blocked()), authorizations: requests);

        var (_, body, _) = await RunFilter(http);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authorizationId").ValueKind.Should().Be(JsonValueKind.String,
            "a 409 that names a decide endpoint must name a row that exists");
        requests.Calls.Should().ContainSingle().Which.Should().Be(
            "route:POST /api/v1/git/acme/widget/branches",
            "the derived correlation is the METHOD and the CONCRETE path — deterministic, so "
            + "the retry re-finds the grant, and narrow, so a grant for one repo does not cover "
            + "another");
        doc.RootElement.GetProperty("correlationId").GetString().Should()
            .StartWith("route:", "a derived correlation is marked as such for the auditor");
    }

    private sealed class StubRequests(Guid id) : IActionAuthorizationRequests
    {
        public List<string> Calls { get; } = new();
        public TimeSpan Ttl => TimeSpan.FromHours(24);
        public Task<Guid?> RequestAsync(
            GovernancePrincipal principal, AutonomyDecision decision, string correlationId,
            CancellationToken ct = default)
        {
            Calls.Add(correlationId);
            return Task.FromResult<Guid?>(id);
        }
    }

    // ====================================================================
    // AC2 for Seam C — the anti-no-op PAIR
    // ====================================================================

    [Test]
    public async Task ShippedDefaults_DoNotAlterControlFlow_atSeamC()
    {
        // Half one. Every route that opts in ships at AutonomyDial.Min, so with no
        // policy rows the filter must be invisible.
        var (http, _) = Context(new ScriptedGate(Allowed()));

        var (status, _, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeTrue();
        status.Should().NotBe(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task TighteningOneAction_DoesAlterControlFlow_atSeamC()
    {
        // Half two, and without it half one is satisfiable by a filter that never
        // fires. Same wiring, one different resolved decision.
        var (http, _) = Context(new ScriptedGate(Blocked()));

        var (status, _, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeFalse();
        status.Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task AnObserveOnlyResolution_proceeds()
    {
        // `Enforce = false` is the admin's explicit "report but do not block". A
        // seam that ignored it would make an observe-mode rollout an outage.
        var (http, _) = Context(new ScriptedGate(Blocked(enforced: false)));

        var (_, _, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeTrue();
    }

    [Test]
    public async Task ADeniedOutcome_alsoBlocks_notOnlyRequiresHuman()
    {
        var (http, _) = Context(new ScriptedGate(Blocked(AutonomyOutcome.Denied)));
        var (status, _, handlerRan) = await RunFilter(http);
        status.Should().Be(StatusCodes.Status409Conflict);
        handlerRan.Should().BeFalse();
    }

    // ====================================================================
    // AC7 — the two bypasses the filter does NOT inherit
    // ====================================================================

    [Test]
    public async Task PlatformAdmin_cannot_bypass_a_governed_effect()
    {
        // `platformRole == "platform_admin"` satisfies EVERY PermissionRequirement
        // in this codebase (PermissionHandler, duplicated in
        // SelfOrPermissionRequirement). The gate is NOT authorization and does not
        // consult it: a platform admin can edit assignments but cannot bypass a
        // governed effect.
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("platformRole", "platform_admin"), new Claim("role", "tenant_owner")],
            "test"));
        var (http, _) = Context(new ScriptedGate(Blocked()), user: admin);

        var (status, _, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        handlerRan.Should().BeFalse();
    }

    [Test]
    public async Task WildcardApiKey_cannot_bypass_a_governed_effect()
    {
        // An api-key `permission` claim of "*" is the second unconditional
        // superuser bypass. Same reasoning, same answer.
        var wildcard = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("permission", "*")], "ApiKey"));
        var (http, _) = Context(new ScriptedGate(Blocked()), user: wildcard);

        var (status, _, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        handlerRan.Should().BeFalse();
    }

    [Test]
    public async Task Gate_still_evaluates_when_the_caller_is_anonymous()
    {
        // The Development-without-JWT blanket re-registers every named policy with
        // AllowAnonymousRequirement, so in that configuration a caller reaches the
        // handler with an unauthenticated principal. That blanket rewrites
        // AUTHORIZATION; the gate is not authorization, runs after it, and is
        // unaffected — which is exactly why Seam C is an endpoint filter and not an
        // IAuthorizationHandler (the middleware order also leaves
        // ITenantContext.TenantId unset during policy evaluation).
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        anonymous.Identity!.IsAuthenticated.Should().BeFalse();

        var gate = new ScriptedGate(Blocked());
        var (http, _) = Context(gate, user: anonymous);

        var (status, _, handlerRan) = await RunFilter(http);

        gate.Queries.Should().ContainSingle("the gate must be consulted regardless of the "
            + "authorization outcome");
        status.Should().Be(StatusCodes.Status409Conflict);
        handlerRan.Should().BeFalse();
    }

    // ====================================================================
    // Failure posture: static wiring fails CLOSED, transient fails OPEN
    // ====================================================================

    [Test]
    public async Task AnEnforcedRouteWithNoBinding_failsCLOSED()
    {
        // A deterministic wiring fault — same on every request, caught by
        // GovernedEndpointEnforcementSweepTests before release. Answering
        // "proceed" to "enforce this route, but I cannot tell what it does" would
        // be a silent ungoverning.
        var (http, _) = Context(new ScriptedGate(Allowed()), withBinding: false);

        var (status, body, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("ACTION.GATE.MISCONFIGURED");
        handlerRan.Should().BeFalse();
    }

    [Test]
    public async Task AHostWithNoGateRegistered_failsCLOSED()
    {
        var (http, _) = Context(gate: null);

        var (status, body, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("ACTION.GATE.MISCONFIGURED");
        handlerRan.Should().BeFalse();
    }

    [Test]
    public async Task ATransientEvaluationFailure_failsOPEN_andIsAudited()
    {
        // Deny on a DECISION, never on an ERROR — the posture the epic already
        // took at Seam D. Note this does NOT re-open the gate's own fail-closed
        // handling of an unreadable POLICY input: that is a decision
        // (Unavailable provenance, Enforced forced true) and still blocks above.
        var (http, events) = Context(new ScriptedGate(decision: null, throws: true));

        var (status, _, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeTrue("a control-plane blip must degrade to today's behaviour, not "
            + "stop the platform");
        status.Should().NotBe(StatusCodes.Status409Conflict);
        events.Appended.Select(e => e.Type).Should().Contain(
            ActionGateEventsService.EvaluationFailedType);
    }

    // ====================================================================
    // The CONTROLLER plane renders the same denial (D15 reasoning #4)
    // ====================================================================

    [Test]
    public async Task TheControllerAttribute_producesTheSame409()
    {
        // An IEndpointFilter does not run for an MVC endpoint. If the controller
        // plane's opt-in ever stopped denying, every [Governs]-bound controller
        // action that opted in would silently become ungoverned while the
        // minimal-API tests above stayed green.
        var (http, _) = Context(new ScriptedGate(Blocked()));

        var actionContext = new ActionContext(
            http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(), new ModelStateDictionary());
        var executing = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);

        var nextRan = false;
        await new EnforcesGovernanceAttribute().OnActionExecutionAsync(executing, () =>
        {
            nextRan = true;
            return Task.FromResult(new ActionExecutedContext(
                actionContext, new List<IFilterMetadata>(), controller: null!));
        });

        nextRan.Should().BeFalse("a denied controller action must not reach its body");
        executing.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task TheControllerAttribute_proceedsOnAnAllow()
    {
        var (http, _) = Context(new ScriptedGate(Allowed()));

        var actionContext = new ActionContext(
            http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(), new ModelStateDictionary());
        var executing = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);

        var nextRan = false;
        await new EnforcesGovernanceAttribute().OnActionExecutionAsync(executing, () =>
        {
            nextRan = true;
            return Task.FromResult(new ActionExecutedContext(
                actionContext, new List<IFilterMetadata>(), controller: null!));
        });

        nextRan.Should().BeTrue();
        executing.Result.Should().BeNull();
    }

    // ====================================================================
    // What the filter asks the gate
    // ====================================================================

    [Test]
    public async Task TheQuery_carriesTheBoundAction_andTheCorrelationFromTheWire()
    {
        var gate = new ScriptedGate(Allowed());
        var (http, _) = Context(gate, correlationId: "run-9");

        await RunFilter(http);

        gate.Queries.Should().ContainSingle();
        gate.Queries[0].Action.Should().Be(BranchCreate,
            "the filter evaluates the action the ROUTE is bound to — it has no other way to know "
            + "what the handler does");
        gate.Queries[0].CorrelationId.Should().Be("run-9");
        gate.Queries[0].Operation.Should().Be("POST /api/v1/git/acme/widget/branches");
    }

    [Test]
    public async Task TheCorrelationId_isAlsoReadFromTheQueryString()
    {
        // Several mediation routes already carry ?correlationId=; honouring it
        // means those routes get ledger coverage without a wire change.
        var gate = new ScriptedGate(Allowed());
        var (http, _) = Context(gate);
        http.Request.QueryString = new QueryString("?correlationId=run-11");

        await RunFilter(http);

        gate.Queries[0].CorrelationId.Should().Be("run-11");
    }
}
