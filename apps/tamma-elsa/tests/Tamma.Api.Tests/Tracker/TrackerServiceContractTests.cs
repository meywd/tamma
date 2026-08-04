using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Tracker;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-2 AC5 / plan D4 / epic Decision D6 — the shape of
/// <see cref="ITrackerService"/>, pinned by reflection so a later
/// "let's make it symmetric" refactor fails the build instead of quietly
/// introducing a second ownership plane.
///
/// <para>Pure reflection: no Docker, no database, no host.</para>
/// </summary>
[TestFixture]
public class TrackerServiceContractTests
{
    /// <summary>
    /// Parameter names that would encode a PRINCIPAL SCOPE. <c>createdByUserId</c>
    /// / <c>actingUserId</c> / <c>viewerUserId</c> are deliberately NOT in this
    /// set: they stamp authorship or name a visibility subject, which is a
    /// different thing from scoping a query to an owner.
    /// </summary>
    private static readonly string[] ScopeParameterNames = ["userId", "tenantId"];

    private static MethodInfo[] Methods() => typeof(ITrackerService).GetMethods();

    private static bool IsPreferenceMethod(MethodInfo m) =>
        m.Name.Contains("Preferences", StringComparison.Ordinal);

    [Test]
    public void No_work_item_service_method_takes_a_user_scope()
    {
        var offenders = Methods()
            .Where(m => !IsPreferenceMethod(m))
            .SelectMany(m => m.GetParameters().Select(p => (Method: m.Name, Param: p.Name)))
            .Where(x => ScopeParameterNames.Contains(x.Param, StringComparer.Ordinal))
            .ToArray();

        offenders.Should().BeEmpty(
            "work items, projects and iterations are tenant-schema CONTENT — the tenant is "
            + "already resolved by the connection, so a userId/tenantId scoping parameter would "
            + "be a second ownership plane with no reader (epic D6 / 44-1 AC7). Offenders: "
            + string.Join(", ", offenders.Select(o => $"{o.Method}({o.Param})")));
    }

    [Test]
    public void Only_the_preference_methods_carry_a_mode_split()
    {
        var forTenant = Methods()
            .Where(m => m.Name.EndsWith("ForTenantAsync", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToArray();

        forTenant.Should().OnlyContain(n => n.Contains("Preferences", StringComparison.Ordinal),
            "the ForTenant surface is the SaaS half of the per-principal configuration pair; "
            + "nothing else on this service has a mode split");
        forTenant.Should().BeEquivalentTo(
        [
            nameof(ITrackerService.GetPreferencesForTenantAsync),
            nameof(ITrackerService.UpsertPreferencesForTenantAsync),
            nameof(ITrackerService.DeletePreferencesForTenantAsync),
        ]);
    }

    [Test]
    public void The_preference_surfaces_are_parallel_pairs()
    {
        // Each ...ForTenantAsync(Guid tenantId) has a sibling ...Async(Guid? userId).
        foreach (var tenantMethod in Methods()
            .Where(m => m.Name.EndsWith("ForTenantAsync", StringComparison.Ordinal)))
        {
            var userMethodName = tenantMethod.Name.Replace("ForTenantAsync", "Async", StringComparison.Ordinal);
            var userMethod = typeof(ITrackerService).GetMethod(userMethodName);
            userMethod.Should().NotBeNull(
                $"{tenantMethod.Name} must have a parallel user-plane sibling {userMethodName} "
                + "(IAcceptanceRulesRepository's documented contract: the two surfaces are "
                + "PARALLEL — no method silently joins both planes)");

            userMethod!.GetParameters()[0].ParameterType.Should().Be(typeof(Guid?),
                "the user plane is keyed on a nullable user id");
            tenantMethod.GetParameters()[0].ParameterType.Should().Be(typeof(Guid),
                "the tenant plane is keyed on a non-null tenant id");
        }
    }

    [Test]
    public void No_work_item_or_project_write_is_a_full_body_put()
    {
        // AC3 / plan D2, stated at the type level: the only request type this
        // service accepts for a work-item or project UPDATE is a Patch*Request
        // whose fields are Optional<T>. A plain nullable-field DTO reaching
        // these seams IS the 43-0 bug.
        foreach (var (method, requestType) in new (string, Type)[]
        {
            (nameof(ITrackerService.PatchProjectAsync), typeof(Tamma.Api.Dtos.Tracker.PatchProjectRequest)),
            (nameof(ITrackerService.PatchWorkItemAsync), typeof(Tamma.Api.Dtos.Tracker.PatchWorkItemRequest)),
        })
        {
            typeof(ITrackerService).GetMethod(method)!.GetParameters()
                .Should().Contain(p => p.ParameterType == requestType);

            requestType.GetProperties().Should().OnlyContain(
                p => p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(Tamma.Api.Dtos.Tracker.Optional<>),
                $"every field on {requestType.Name} must be tri-state — a plain nullable field "
                + "cannot distinguish 'absent' from 'clear', which is exactly how the "
                + "acceptance-rules dialog silently reset acceptorRequirement on every save");
        }
    }

    [Test]
    public void Every_mutation_accepts_an_if_match_version()
    {
        // AC9 — an ifMatchVersion parameter on every write seam, so a handler
        // physically cannot forget to plumb the precondition through.
        foreach (var name in new[]
        {
            nameof(ITrackerService.PatchProjectAsync),
            nameof(ITrackerService.DeleteProjectAsync),
            nameof(ITrackerService.PatchWorkItemAsync),
            nameof(ITrackerService.SetWorkItemStatusAsync),
            nameof(ITrackerService.AssignWorkItemAsync),
            nameof(ITrackerService.DeleteWorkItemAsync),
            // Added by the 44-2 conformance round (2026-07-29). Both were
            // silently absent from this list while the AC claimed "every
            // mutation" — the enumeration was the reason the gap survived
            // review, so the gap and the enumeration are fixed together.
            nameof(ITrackerService.DeletePreferencesAsync),
            nameof(ITrackerService.DeletePreferencesForTenantAsync),
        })
        {
            typeof(ITrackerService).GetMethod(name)!.GetParameters()
                .Should().Contain(
                    p => p.Name == "ifMatchVersion" && p.ParameterType == typeof(int?),
                    $"{name} must take the If-Match precondition (AC9)");
        }
    }
}
