using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// Adversarial review of the 43-9 enforcement wave (2026-08-01) — the seam
/// COMPOSITION tests. Every shipped Seam C/D test drives a <c>ScriptedGate</c>,
/// so the interaction between the REAL <see cref="AutonomyGateService"/> (which
/// rethrows a failed audit append for an enforced denial, deliberately, since
/// 43-5 AC13) and the seams' catch-alls (which fail OPEN on any exception) was
/// never exercised. These tests compose the two.
/// </summary>
[TestFixture]
public class AutonomyGateSeamPostureTests
{
    // ── Doubles ─────────────────────────────────────────────────────────────

    /// <summary>An event store that is DOWN — the F2 scenario: the same Postgres
    /// that holds <c>action_assignments</c> also holds <c>domain_events</c>, so a
    /// single blip produces the fail-closed decision AND the failing append.</summary>
    private sealed class ThrowingEvents : IEventRepository
    {
        public int Attempts { get; private set; }
        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Attempts++;
            throw new InvalidOperationException("domain_events is unreachable");
        }
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

    /// <summary>The 43-5 F6 degraded snapshot: no successful read has landed, so
    /// every catalogued action fails CLOSED with <c>Unavailable</c> provenance
    /// and <c>Enforced</c> forced true.</summary>
    private sealed class DegradedSnapshots : IGovernancePolicySnapshotProvider
    {
        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal)
            => GovernancePolicySnapshot.Unavailable;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId)
            => GovernancePolicySnapshot.Unavailable;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HealthySnapshots : IGovernancePolicySnapshotProvider
    {
        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal)
            => GovernancePolicySnapshot.Empty;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId)
            => GovernancePolicySnapshot.Empty;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ShippedRules : IAcceptanceRulesResolver
    {
        private static ResolvedAcceptanceRules Base() => new(
            AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "base",
            DateTimeOffset.UtcNow);
        public Task<ResolvedAcceptanceRules> ResolveAsync(
            Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default)
            => Task.FromResult(Base());
        public Task<ResolvedAcceptanceRules> ResolveForTenantAsync(
            Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default)
            => Task.FromResult(Base());
        public Task<ResolvedAcceptanceRules> ResolveBaseAsync(
            Guid? userId, CancellationToken ct = default) => Task.FromResult(Base());
        public Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(
            Guid tenantId, CancellationToken ct = default) => Task.FromResult(Base());
    }

    private sealed class FixedPrincipal(Guid? userId = null) : IGovernancePrincipalResolver
    {
        private readonly Guid _userId = userId ?? Guid.NewGuid();
        public Task<GovernancePrincipal> ResolveAsync(
            ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(GovernancePrincipal.ForUser(_userId));
    }

    /// <summary>A ledger that records what was asked of it. F4: Seam A must never
    /// reach <see cref="TryConsumeAsync"/>.</summary>
    private sealed class RecordingLedger : IActionAuthorizationLedger
    {
        public List<string> Consults { get; } = new();
        public ActionAuthorization? Grant { get; set; }

        public Task<ActionAuthorization> RequestAsync(
            Guid? tenantId, Guid? userId, string correlationId, string targetKind,
            string targetKey, string? reason, int? autonomyLevelAtRequest,
            TimeSpan? ttl = null, string scope = "single-use", CancellationToken ct = default)
            => Task.FromResult(new ActionAuthorization
            {
                Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId,
                CorrelationId = correlationId, TargetKind = targetKind,
                TargetKey = targetKey, State = "pending",
            });

        public Task<ActionAuthorization?> TryConsumeAsync(
            Guid? tenantId, Guid? userId, string correlationId, string actionKeyWire,
            CancellationToken ct = default)
        {
            Consults.Add($"{correlationId}|{actionKeyWire}");
            var grant = Grant;
            Grant = null; // single-use, like the real CAS
            return Task.FromResult(grant);
        }

        public Task<ActionAuthorization?> DecideAsync(
            Guid? tenantId, Guid? userId, Guid id, bool granted, Guid decidedByUserId,
            string? reason, CancellationToken ct = default)
            => Task.FromResult<ActionAuthorization?>(null);
    }

    /// <summary>A gate that never returns — F9's hang, which the fail-open posture
    /// (a catch) does not cover.</summary>
    private sealed class HangingGate : IAutonomyGate
    {
        public async Task<AutonomyDecision> EvaluateAsync(
            AutonomyQuery query, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private static readonly ActionKey BranchCreate =
        new(ActionNamespace.Effect, ExternalEffect.GitBranchCreate.ToWire());

    // ── Composition helpers ─────────────────────────────────────────────────

    private static AutonomyGateService Gate(
        IEventRepository events,
        IGovernancePolicySnapshotProvider? snapshots = null,
        IActionAuthorizationLedger? ledger = null)
        => new(
            new FixedPrincipal(),
            snapshots ?? new DegradedSnapshots(),
            new ShippedRules(),
            new ActionGateEventsService(events),
            breakGlass: null,
            logger: null,
            timeProvider: null,
            ledger: ledger);

    private static (DefaultHttpContext Http, ServiceProvider Services) SeamC(
        IAutonomyGate? gate,
        bool withBinding = true,
        bool withMarker = true,
        bool withPrincipalResolver = true,
        IActionAuthorizationRequests? authorizations = null,
        IEventRepository? events = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (gate is not null) services.AddScoped(_ => gate);
        if (withPrincipalResolver)
            services.AddScoped<IGovernancePrincipalResolver>(_ => new FixedPrincipal());
        services.AddScoped(_ => new ActionGateEventsService(events ?? new RecordingEvents()));
        if (authorizations is not null) services.AddSingleton(authorizations);

        var metadata = new List<object>();
        if (withMarker) metadata.Add(new GovernanceEnforcementMetadata());
        if (withBinding) metadata.Add(new ActionGateMetadata(BranchCreate));

        var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        http.Request.Method = "POST";
        http.Request.Path = "/api/v1/git/acme/widget/branches";
        http.Response.Body = new MemoryStream();
        http.Features.Set<IEndpointFeature>(new EndpointFeature
        {
            Endpoint = new Endpoint(
                _ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"),
        });
        return (http, provider);
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

    // ====================================================================
    // F2 — a genuine enforced DENIAL whose audit append fails must still BLOCK
    // ====================================================================

    [Test]
    public async Task TheDegradedDecisionIsMade_evenWhenTheAuditAppendFails()
    {
        // Establishes the premise the two seam tests below rest on: with a
        // WORKING event store the same composition really does produce an
        // enforced RequiresHuman. The append failure must not change that fact.
        var gate = Gate(new RecordingEvents());
        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(BranchCreate, GovernancePrincipal.Platform));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.Enforced.Should().BeTrue();
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
    }

    [Test]
    public async Task SeamC_stillBLOCKS_whenTheAuditAppendForAnEnforcedDenialFails()
    {
        // F2. The gate DECIDED to block; only the record of it failed. Reading
        // that as a transient evaluation fault turns a denial into a pass — and
        // 43-5's fail-closed degradation fires exactly when the control plane is
        // unreadable, which is exactly when the append fails too.
        var events = new ThrowingEvents();
        var (http, _) = SeamC(Gate(events), events: events);

        var (status, body, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeFalse(
            "a decision that was MADE must not be downgraded to a transient fault by an audit "
            + "failure — the block stands");
        status.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("ACTION.GATE.REQUIRES_HUMAN");
    }

    [Test]
    public async Task SeamD_stillSKIPS_whenTheAuditAppendForAnEnforcedDenialFails()
    {
        var events = new ThrowingEvents();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAutonomyGate>(_ => Gate(events));
        services.AddScoped(_ => new ActionGateEventsService(events));
        var provider = services.BuildServiceProvider();

        var seam = new BackgroundActionGate(provider.GetRequiredService<IServiceScopeFactory>());
        var mayRun = await seam.MayRunAsync(BackgroundActor.TaskQueueProcessor);

        mayRun.Should().BeFalse(
            "an actor gated OFF by a decision that was actually made must not run because the "
            + "audit row could not be written");
    }

    [Test]
    public async Task AnAuditFailureOnADecisionThatDoesNotBlock_stillProceeds()
    {
        // The other half: the fix must not turn every event-store blip into a
        // platform outage. A healthy snapshot resolves the shipped default
        // (Automated), the .ALLOWED emission is volume-gated away, and the
        // request proceeds.
        var events = new ThrowingEvents();
        var (http, _) = SeamC(
            Gate(events, snapshots: new HealthySnapshots()), events: events);

        var (status, _, handlerRan) = await RunFilter(http);

        handlerRan.Should().BeTrue();
        status.Should().NotBe(StatusCodes.Status409Conflict);
    }

    // ====================================================================
    // F4 — only a seam that can BLOCK may consume a single-use grant
    // ====================================================================

    [Test]
    public async Task AnObserveOnlySeam_neverConsumesAGrant()
    {
        // Seam A's whole design property is "never blocks in any version".
        // Burning a person's single-use grant on it means the real, blocking ask
        // that follows finds nothing.
        var ledger = new RecordingLedger
        {
            Grant = new ActionAuthorization
            {
                Id = Guid.NewGuid(), CorrelationId = "run-1",
                TargetKind = "group", TargetKey = "deploy-control", State = "granted",
            },
        };
        var gate = Gate(new RecordingEvents(), ledger: ledger);

        // Seam A's shape: no SeamCanBlock, a correlation id, an enforced
        // requires-human decision.
        var observed = await gate.EvaluateAsync(new AutonomyQuery(
            BranchCreate, GovernancePrincipal.Platform, CorrelationId: "run-1"));

        ledger.Consults.Should().BeEmpty(
            "a seam that was never going to block must not burn the person's decision");
        observed.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        observed.CoveredBy.Should().BeNull();

        // …and the real, blocking ask that follows still finds the grant.
        var blocked = await gate.EvaluateAsync(new AutonomyQuery(
            BranchCreate, GovernancePrincipal.Platform, CorrelationId: "run-1",
            SeamCanBlock: true));

        ledger.Consults.Should().ContainSingle();
        blocked.Outcome.Should().Be(AutonomyOutcome.Automated);
        blocked.CoveredBy.Should().Be("group:deploy-control");
    }

    // ====================================================================
    // F5 — Seam C's 409 must not promise an id it never mints
    // ====================================================================

    private sealed class StubRequests : IActionAuthorizationRequests
    {
        public List<(GovernancePrincipal Principal, string CorrelationId)> Calls { get; } = new();
        public TimeSpan Ttl => TimeSpan.FromHours(24);
        public Guid Id { get; } = Guid.NewGuid();
        public Task<Guid?> RequestAsync(
            GovernancePrincipal principal, AutonomyDecision decision, string correlationId,
            CancellationToken ct = default)
        {
            Calls.Add((principal, correlationId));
            return Task.FromResult<Guid?>(Id);
        }
    }

    [Test]
    public async Task SeamC_mintsAPendingRow_evenWhenTheCallerSendsNoCorrelationId()
    {
        // No opted-in route sends X-Tamma-Correlation-Id or ?correlationId=, so
        // before the fix authorizationId was ALWAYS null while the 409 body told
        // the caller to "Grant the pending authorization (POST …/{id}/decide)".
        var requests = new StubRequests();
        var (http, _) = SeamC(Gate(new RecordingEvents()), authorizations: requests);

        var (status, body, _) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authorizationId").GetGuid().Should().Be(requests.Id,
            "the 409 names an id a person can actually decide");
        requests.Calls.Should().ContainSingle();
    }

    [Test]
    public async Task TheDerivedCorrelation_isStableAcrossRetriesOfTheSameRequest()
    {
        // The ledger is keyed by (principal, correlation, target): a correlation
        // that changed per request could never be found again, so the grant a
        // person makes would cover nothing.
        var first = new StubRequests();
        var (http1, _) = SeamC(Gate(new RecordingEvents()), authorizations: first);
        await RunFilter(http1);

        var second = new StubRequests();
        var (http2, _) = SeamC(Gate(new RecordingEvents()), authorizations: second);
        await RunFilter(http2);

        second.Calls[0].CorrelationId.Should().Be(first.Calls[0].CorrelationId);
        first.Calls[0].CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task AnExplicitCorrelationId_alwaysWinsOverTheDerivedOne()
    {
        var requests = new StubRequests();
        var (http, _) = SeamC(Gate(new RecordingEvents()), authorizations: requests);
        http.Request.Headers["X-Tamma-Correlation-Id"] = "run-42";

        var (_, body, _) = await RunFilter(http);

        requests.Calls[0].CorrelationId.Should().Be("run-42");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("correlationId").GetString().Should().Be("run-42");
    }

    // ====================================================================
    // F9 — a HANG is neither open nor closed
    // ====================================================================

    [Test]
    public async Task SeamC_boundsAHangingGate()
    {
        var (http, _) = SeamC(new HangingGate());

        var sw = Stopwatch.StartNew();
        var (_, _, handlerRan) = await RunFilter(http);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(AutonomyGateDeadline.Default * 4,
            "a gate that hangs would otherwise hang the HTTP request until the client "
            + "disconnects — neither open nor closed");
        handlerRan.Should().BeTrue("a timed-out evaluation is a transient fault (fail open)");
    }

    [Test]
    public async Task SeamD_boundsAHangingGate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAutonomyGate>(_ => new HangingGate());
        services.AddScoped(_ => new ActionGateEventsService(new RecordingEvents()));
        var provider = services.BuildServiceProvider();
        var seam = new BackgroundActionGate(provider.GetRequiredService<IServiceScopeFactory>());

        var sw = Stopwatch.StartNew();
        var mayRun = await seam.MayRunAsync(BackgroundActor.TaskQueueProcessor);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(AutonomyGateDeadline.Default * 4,
            "a hanging gate would otherwise stall the sweeper's loop indefinitely");
        mayRun.Should().BeTrue();
    }

    // ====================================================================
    // F10 — a STATIC WIRING fault fails CLOSED, including a missing resolver
    // ====================================================================

    [Test]
    public async Task AHostWithNoPrincipalResolver_failsCLOSED()
    {
        // GetRequiredService<IGovernancePrincipalResolver>() used to sit inside
        // the try, so this deterministic misconfiguration threw into the
        // transient catch-all and the request PROCEEDED — against the stated
        // "a static wiring fault fails CLOSED".
        var (http, _) = SeamC(Gate(new RecordingEvents()), withPrincipalResolver: false);

        var (status, body, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("ACTION.GATE.MISCONFIGURED");
        handlerRan.Should().BeFalse();
    }

    // ====================================================================
    // F8 — the enforcement pin pins the ANNOTATION; the FILTER must agree
    // ====================================================================

    [Test]
    public async Task TheFilterRefusesToEnforce_onARouteCarryingNoOptInMarker()
    {
        // The harness computes enforcement purely from IGovernanceEnforcementMetadata.
        // A route with .Governs(...) + .AddEndpointFilter<AutonomyGateEndpointFilter>()
        // and NO marker used to enforce (409) while the sweep reported it
        // unenforced — the pin bypassed. It is a wiring fault, so it fails CLOSED
        // and LOUD rather than silently gating.
        var (http, _) = SeamC(Gate(new RecordingEvents()), withMarker: false);

        var (status, body, handlerRan) = await RunFilter(http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("ACTION.GATE.MISCONFIGURED");
        handlerRan.Should().BeFalse();
    }
}
