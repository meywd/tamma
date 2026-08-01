using System.Security.Claims;
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

namespace Tamma.Api.Tests.ZzProbe;

/// <summary>TEMPORARY adversarial probe — delete after the review.</summary>
[TestFixture]
public class ZzGateProbeTests
{
    private sealed class ThrowingEvents : IEventRepository
    {
        public int Attempts;
        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Attempts++;
            throw new InvalidOperationException("event store unreachable (transient)");
        }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? t, string? ty, int? i, int l) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid t, string ty) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid t) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? a, string? b, int? c, int d, int e)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid a, string? b, int c, int d)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
    }

    private sealed class DegradedSnapshots : IGovernancePolicySnapshotProvider
    {
        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal p) => GovernancePolicySnapshot.Unavailable;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId) => GovernancePolicySnapshot.Unavailable;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class FixedPrincipal(GovernancePrincipal p) : IGovernancePrincipalResolver
    {
        public Task<GovernancePrincipal> ResolveAsync(ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(p);
    }

    private sealed class DefaultRules : IAcceptanceRulesResolver
    {
        private static ResolvedAcceptanceRules R() =>
            new(AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);
        public Task<ResolvedAcceptanceRules> ResolveAsync(Guid? userId, DocumentTypeKey dt, CancellationToken ct = default) => Task.FromResult(R());
        public Task<ResolvedAcceptanceRules> ResolveForTenantAsync(Guid tenantId, DocumentTypeKey dt, CancellationToken ct = default) => Task.FromResult(R());
        public Task<ResolvedAcceptanceRules> ResolveBaseAsync(Guid? userId, CancellationToken ct = default) => Task.FromResult(R());
        public Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(R());
    }

    private static readonly ActionKey BranchCreate =
        new(ActionNamespace.Effect, ExternalEffect.GitBranchCreate.ToWire());

    [Test]
    public async Task PROBE_enforced_denial_with_failing_audit_append_lets_the_request_through()
    {
        var user = Guid.NewGuid();
        var events = new ThrowingEvents();
        var gateEvents = new ActionGateEventsService(events);
        var gate = new AutonomyGateService(
            new FixedPrincipal(GovernancePrincipal.ForUser(user)),
            new DegradedSnapshots(),
            new DefaultRules(),
            gateEvents);

        // 1. The gate DOES produce an enforced denial when the audit works.
        var withWorkingAudit = new AutonomyGateService(
            new FixedPrincipal(GovernancePrincipal.ForUser(user)),
            new DegradedSnapshots(),
            new DefaultRules(),
            new ActionGateEventsService(new NoOpEvents()));
        var decision = await withWorkingAudit.EvaluateAsync(new AutonomyQuery(
            BranchCreate, GovernancePrincipal.ForUser(user), null, "POST /x", "/x", null));
        TestContext.Out.WriteLine(
            $"DECISION with working audit: outcome={decision.Outcome} enforced={decision.Enforced} reason={decision.Reason}");

        // 2. Same evaluation, audit append fails → what does the SEAM C filter do?
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAutonomyGate>(_ => gate);
        services.AddScoped<IGovernancePrincipalResolver>(_ => new FixedPrincipal(GovernancePrincipal.ForUser(user)));
        services.AddScoped(_ => gateEvents);

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        http.Request.Method = "POST";
        http.Request.Path = "/api/v1/git/acme/widget/branches";
        http.Response.Body = new MemoryStream();
        http.Features.Set<IEndpointFeature>(new Feat
        {
            Endpoint = new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new object[]
                {
                    new GovernanceEnforcementMetadata(),
                    new ActionGateMetadata(BranchCreate),
                }), "probe"),
        });

        var handlerRan = false;
        var filter = new AutonomyGateEndpointFilter();
        var result = await filter.InvokeAsync(
            new Ctx(http),
            _ => { handlerRan = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        TestContext.Out.WriteLine($"SEAM C: handlerRan={handlerRan} result={result?.GetType().Name ?? "null"} status={http.Response.StatusCode}");

        // 3. Seam D, same conditions.
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddScoped<IAutonomyGate>(_ => gate);
        sc.AddScoped(_ => gateEvents);
        var sp = sc.BuildServiceProvider();
        var bg = new BackgroundActionGate(sp.GetRequiredService<IServiceScopeFactory>());
        var mayRun = await bg.MayRunAsync(BackgroundActor.TaskQueueProcessor, null, default);
        TestContext.Out.WriteLine($"SEAM D (automation actor, degraded+failing audit): mayRun={mayRun}");

        Assert.Pass();
    }

    private sealed class NoOpEvents : IEventRepository
    {
        public Task<DomainEvent> AppendAsync(DomainEvent evt) => Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? t, string? ty, int? i, int l) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid t, string ty) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid t) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? a, string? b, int? c, int d, int e)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid a, string? b, int c, int d)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
    }

    private sealed class Feat : IEndpointFeature { public Endpoint? Endpoint { get; set; } }

    private sealed class Ctx(HttpContext http) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }
}
