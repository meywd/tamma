using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Unit tests for <see cref="PromptStoreService"/> exercising the 4-layer role+action
/// resolution order and the 2-layer role-system resolution order.
/// Uses EF Core InMemory provider to isolate from external Postgres.
/// </summary>
[TestFixture]
public class PromptStoreServiceTests
{
    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private PromptRepository _repo = null!;
    private PromptStoreService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _db = _fx.Cp;
        // Story 28-1 PR D: prompt_overrides is tenant-scoped. Bind a
        // synthetic tenant so the repo routes through the factory.
        var tc = new TenantContext();
        tc.SetTenantId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        _repo = new PromptRepository(_fx.Factory, tc);
        _service = new PromptStoreService(_repo);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fx.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 4-layer role+action resolution
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_UserOverride_Wins()
    {
        var userId = Guid.NewGuid();
        await _repo.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            Scope = "role-action",
            Role = "developer",
            Action = "plan",
            Template = "USER-OVERRIDDEN",
            SystemPrompt = "custom system",
        });

        var result = await _service.ResolveRoleActionAsync(userId, "developer", "plan");

        result.Should().NotBeNull();
        result!.Template.Should().Be("USER-OVERRIDDEN");
        result.Source.Should().Be(PromptSource.UserOverride);
    }

    [Test]
    public async Task ResolveRoleActionAsync_FallsBackTo_SystemRoleActionDefault()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveRoleActionAsync(userId, "developer", "plan");

        result.Should().NotBeNull();
        result!.Source.Should().Be(PromptSource.SystemRoleAction);
        // System default templates all mention their role in the text
        result.Template.Should().Contain("implementation plan");
    }

    [Test]
    public async Task ResolveRoleActionAsync_FallsBackTo_UserActionDefault_WhenNoRoleActionExists()
    {
        var userId = Guid.NewGuid();
        // Store a user action-default override
        await _repo.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            Scope = "action-default",
            Role = null,
            Action = "plan",
            Template = "USER-ACTION-DEFAULT",
        });

        // Use an unknown role so no system role-action template is available
        var result = await _service.ResolveRoleActionAsync(userId, "unknown-role", "plan");

        result.Should().NotBeNull();
        result!.Template.Should().Be("USER-ACTION-DEFAULT");
        result.Source.Should().Be(PromptSource.UserActionDefault);
    }

    [Test]
    public async Task ResolveRoleActionAsync_FallsBackTo_SystemActionDefault_WhenNothingElse()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveRoleActionAsync(userId, "unknown-role", "plan");

        result.Should().NotBeNull();
        result!.Source.Should().Be(PromptSource.SystemActionDefault);
        result.Template.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ResolveRoleActionAsync_ReturnsNull_WhenNoLayerMatches()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveRoleActionAsync(userId, "unknown-role", "unknown-action");

        result.Should().BeNull();
    }

    [Test]
    public async Task ResolveRoleActionAsync_AnonymousUser_UsesSystemDefaults()
    {
        var result = await _service.ResolveRoleActionAsync(null, "developer", "implement");

        result.Should().NotBeNull();
        result!.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    // ------------------------------------------------------------------
    // 2-layer role-system resolution
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleSystemAsync_UserOverride_Wins()
    {
        var userId = Guid.NewGuid();
        await _repo.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            Scope = "role-system",
            Role = "developer",
            Action = null,
            Template = "CUSTOM ROLE PROMPT",
        });

        var result = await _service.ResolveRoleSystemAsync(userId, "developer");

        result.Should().Be("CUSTOM ROLE PROMPT");
    }

    [Test]
    public async Task ResolveRoleSystemAsync_FallsBackToSystemDefault()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveRoleSystemAsync(userId, "security");

        result.Should().NotBeNullOrWhiteSpace();
        result!.Should().Contain("security");
    }

    [Test]
    public async Task ResolveRoleSystemAsync_ReturnsNull_ForUnknownRole()
    {
        var result = await _service.ResolveRoleSystemAsync(null, "not-a-role");

        result.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // User-isolated resolution
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_UserOverrides_DoNotLeak_BetweenUsers()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await _repo.UpsertAsync(new PromptOverride
        {
            UserId = alice,
            Scope = "role-action",
            Role = "developer",
            Action = "plan",
            Template = "ALICE-ONLY",
        });

        var aliceResult = await _service.ResolveRoleActionAsync(alice, "developer", "plan");
        var bobResult = await _service.ResolveRoleActionAsync(bob, "developer", "plan");

        aliceResult!.Template.Should().Be("ALICE-ONLY");
        aliceResult.Source.Should().Be(PromptSource.UserOverride);
        bobResult!.Source.Should().Be(PromptSource.SystemRoleAction);
        bobResult.Template.Should().NotBe("ALICE-ONLY");
    }

    // ------------------------------------------------------------------
    // Upsert / Delete round-trip
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertAndDelete_RoleAction_WorksThroughService()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, "developer", "plan", new UpsertPromptInput(
            Template: "MY-TEMPLATE",
            SystemPrompt: "MY-SYSTEM",
            Variables: new[] { "foo" },
            EnableTools: true,
            MaxTokens: 2048));

        var resolved = await _service.ResolveRoleActionAsync(userId, "developer", "plan");
        resolved!.Template.Should().Be("MY-TEMPLATE");

        var deleted = await _service.DeleteRoleActionAsync(userId, "developer", "plan");
        deleted.Should().BeTrue();

        // After delete, should fall through to system default
        var afterDelete = await _service.ResolveRoleActionAsync(userId, "developer", "plan");
        afterDelete!.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    [Test]
    public async Task DeleteRoleActionAsync_ReturnsFalse_WhenOverrideMissing()
    {
        var userId = Guid.NewGuid();

        var deleted = await _service.DeleteRoleActionAsync(userId, "developer", "plan");

        deleted.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Audit prompts/007 — wasCreated discriminator drives CREATED vs UPDATED
    // event emission at the endpoint layer.
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertRoleActionAsync_FirstCall_ReportsWasCreatedTrue()
    {
        var userId = Guid.NewGuid();

        var (entity, wasCreated) = await _service.UpsertRoleActionAsync(
            userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "T1"));

        wasCreated.Should().BeTrue();
        entity.Template.Should().Be("T1");
        entity.Version.Should().Be(1);
    }

    [Test]
    public async Task UpsertRoleActionAsync_SecondCall_ReportsWasCreatedFalse()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "T1"));

        var (entity, wasCreated) = await _service.UpsertRoleActionAsync(
            userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "T2"));

        wasCreated.Should().BeFalse();
        entity.Template.Should().Be("T2");
        entity.Version.Should().Be(2, "audit prompts/010: version bumps on update");
    }

    [Test]
    public async Task UpsertRoleActionAsync_SetsCreatedByAndUpdatedBy_ToOwnerByDefault()
    {
        var userId = Guid.NewGuid();
        var (entity, _) = await _service.UpsertRoleActionAsync(
            userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "T1"));

        entity.CreatedBy.Should().Be(userId);
        entity.UpdatedBy.Should().Be(userId);
    }

    // ------------------------------------------------------------------
    // Audit prompts/008 — Layer 4 fallback for arbitrary roles is locked.
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_UnknownRole_KnownAction_ResolvesToActionDefault()
    {
        var result = await _service.ResolveRoleActionAsync(null, "data_engineer", "plan");

        result.Should().NotBeNull();
        result!.Source.Should().Be(PromptSource.SystemActionDefault);
        // The action-default body keeps the {{role}} placeholder for runtime interpolation
        result.Template.Should().Contain("{{role}}");
    }

    [Test]
    public async Task ResolveRoleActionAsync_UnknownRole_UnknownAction_ReturnsNull()
    {
        var result = await _service.ResolveRoleActionAsync(null, "data_engineer", "scaffold");

        result.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Audit prompts/003 — Resolved.Version flows through to the render
    // response. Defaults to 1 for system templates.
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_SystemDefault_HasVersionOne()
    {
        var result = await _service.ResolveRoleActionAsync(null, "developer", "plan");

        result.Should().NotBeNull();
        result!.Version.Should().Be(1);
    }

    [Test]
    public async Task ResolveRoleActionAsync_UserOverride_BumpsVersionOnEachUpdate()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "v1"));
        await _service.UpsertRoleActionAsync(userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "v2"));
        await _service.UpsertRoleActionAsync(userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "v3"));

        var resolved = await _service.ResolveRoleActionAsync(userId, "developer", "plan");

        resolved.Should().NotBeNull();
        resolved!.Version.Should().Be(3);
    }
}
