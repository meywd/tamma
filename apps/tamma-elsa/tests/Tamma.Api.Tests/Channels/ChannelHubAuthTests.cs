using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Hubs;

namespace Tamma.Api.Tests.Channels;

/// <summary>
/// Story 39-18 (AC5 — auth + forged-join halves). The <c>OrchestratorChannel</c>
/// policy admits only a service principal / orchestrator-claim (a member/admin/owner
/// JWT is rejected), and NO hub method has a parameter that could influence group
/// membership (a build-time refusal of forged group-join, D5). Runs locally.
/// </summary>
[TestFixture]
public class ChannelHubAuthTests
{
    private static OrchestratorChannelHandler Handler()
        => new(Mock.Of<IHttpContextAccessor>()); // no HttpContext → no ServiceAuthPrincipal; falls to claim checks.

    private static async Task<bool> Evaluate(ClaimsPrincipal principal)
    {
        var requirement = new OrchestratorChannelRequirement();
        var ctx = new AuthorizationHandlerContext(new[] { requirement }, principal, resource: null);
        await Handler().HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    [Test]
    public async Task OrchestratorClaim_Passes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ApprovalChannels.PrincipalTypeClaim, ApprovalChannels.OrchestratorPrincipalType),
        }, "test"));
        (await Evaluate(principal)).Should().BeTrue();
    }

    [Test]
    public async Task ServicePrincipalStarPermission_Passes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("permission", "*") }, "ApiKey"));
        (await Evaluate(principal)).Should().BeTrue();
    }

    [Test]
    public async Task MemberOwnerAdminJwt_IsRejected()
    {
        foreach (var role in new[] { "member", "admin", "owner" })
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("role", role),
                new Claim("tenantId", Guid.NewGuid().ToString()),
            }, "test"));
            (await Evaluate(principal)).Should().BeFalse($"a tenant {role} JWT is not an orchestrator principal (AC5)");
        }
    }

    // ── forged-join refusal, made a build-time property (D5) ─────────────────

    [TestCase(typeof(OrchestratorChannelHub))]
    [TestCase(typeof(UserChannelHub))]
    public void NoHubMethod_HasAParameterThatCouldInfluenceGroupMembership(Type hubType)
    {
        var declared = hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in declared)
        {
            // No client-invokable join surface: no method is named to join/subscribe...
            method.Name.Should().NotContainEquivalentOf("group");
            method.Name.Should().NotContainEquivalentOf("subscribe");
            method.Name.Should().NotContainEquivalentOf("join");

            // ...and no parameter is a group/tenant/recipient selector a client could
            // pass to steer delivery (groups are derived from claims ONLY).
            foreach (var p in method.GetParameters())
            {
                var pn = (p.Name ?? string.Empty).ToLowerInvariant();
                pn.Should().NotContain("group");
                pn.Should().NotContain("tenant");
                pn.Should().NotContain("recipient");
                pn.Should().NotContain("audience");
            }
        }
    }

    [Test]
    public void UserChannelHub_HasNoTaskActionMethod_D7()
    {
        // Acting on a task travels the REST resume surface, not the hub. The only
        // client→server methods are Ack + SendAgentMessage.
        var methods = typeof(UserChannelHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.Name != nameof(UserChannelHub.OnConnectedAsync))
            .Select(m => m.Name)
            .ToList();

        methods.Should().BeEquivalentTo(new[] { nameof(UserChannelHub.Ack), nameof(UserChannelHub.SendAgentMessage) });
        methods.Should().NotContain(n => n.Contains("Decision") || n.Contains("Resume") || n.Contains("Task"));
    }
}
