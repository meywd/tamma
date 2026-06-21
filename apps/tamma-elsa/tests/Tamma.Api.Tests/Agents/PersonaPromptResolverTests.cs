using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-15 — unit tests for <see cref="PersonaPromptResolver"/>, the
/// persona/public prompt seam. Reads the Epic 27 <see cref="PromptStoreService"/>
/// keyed (principal, role, action) and FAILS LOUD (<c>PROMPT_UNRESOLVED</c>) on a
/// miss — never an empty/plain fallback. Principal routes the single-user
/// (user-keyed) vs SaaS (tenant-keyed) prompt-store surface.
/// </summary>
[TestFixture]
public class PersonaPromptResolverTests
{
    private Mock<IPromptRepository> _repo = null!;
    private PromptStoreService _store = null!;
    private PersonaPromptResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IPromptRepository>(MockBehavior.Loose);
        _store = new PromptStoreService(_repo.Object);
        _resolver = new PersonaPromptResolver(_store, NullLogger<PersonaPromptResolver>.Instance);
    }

    [Test]
    public async Task SingleUser_RoleSystem_ReturnsUserOverride()
    {
        var userId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(userId, "role-system", "developer", null))
            .ReturnsAsync(new PromptOverride { Template = "USER PERSONA PROMPT" });

        var text = await _resolver.ResolveAsync(
            Principal.ForUser(userId), "developer", action: null);

        text.Should().Be("USER PERSONA PROMPT");
    }

    [Test]
    public async Task SingleUser_RoleSystem_FallsToShippedSystemDefault()
    {
        var userId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(userId, "role-system", "developer", null))
            .ReturnsAsync((PromptOverride?)null);

        // No override → shipped SystemPrompts.RoleSystemPrompts["developer"].
        var text = await _resolver.ResolveAsync(
            Principal.ForUser(userId), "developer", action: null);

        text.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Saas_RoleSystem_RoutesTenantPath()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.GetByTenantAsync(tenantId, "role-system", "architect", null))
            .ReturnsAsync(new PromptOverride { Template = "TENANT PERSONA PROMPT" });

        var text = await _resolver.ResolveAsync(
            Principal.ForTenant(tenantId), "architect", action: null);

        text.Should().Be("TENANT PERSONA PROMPT");
        // The single-user path must NOT be consulted in SaaS mode.
        _repo.Verify(r => r.GetAsync(It.IsAny<Guid?>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task RoleSystem_Absent_FailsLoud_PromptUnresolved()
    {
        var userId = Guid.NewGuid();
        // An unknown role has neither an override NOR a shipped role-system
        // default → the seam fails loud (never empty/plain).
        _repo.Setup(r => r.GetAsync(userId, "role-system", "no-such-role", null))
            .ReturnsAsync((PromptOverride?)null);

        Func<Task> act = async () => await _resolver.ResolveAsync(
            Principal.ForUser(userId), "no-such-role", action: null);

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PROMPT_UNRESOLVED");
    }

    [Test]
    public async Task RoleAction_ReturnsSystemHalf_OfResolvedPrompt()
    {
        var userId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(userId, "role-action", "developer", "implement_feature"))
            .ReturnsAsync(new PromptOverride
            {
                Template = "user template",
                SystemPrompt = "SYSTEM HALF",
            });

        var text = await _resolver.ResolveAsync(
            Principal.ForUser(userId), "developer", action: "implement_feature");

        text.Should().Be("SYSTEM HALF");
    }
}
