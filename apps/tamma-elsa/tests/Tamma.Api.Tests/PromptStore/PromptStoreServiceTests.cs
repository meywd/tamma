using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Unit tests for <see cref="PromptStoreService"/> exercising the Story 27-18
/// fail-loud role+action resolution (override → system default → TammaError) and
/// the 2-layer role-system resolution order. Uses EF Core InMemory provider to
/// isolate from external Postgres.
/// </summary>
[TestFixture]
public class PromptStoreServiceTests
{
    // Canonical taxonomy cell used as "the plan cell" in tests: developer owns
    // plan-implementation (Plan body family). Its Plan body contains the phrase
    // "implementation plan".
    private const string Role = "developer";
    private const string Action = "plan-implementation";

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
    // Fail-loud role+action resolution (override → system default → TammaError)
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_UserOverride_Wins()
    {
        var userId = Guid.NewGuid();
        await _repo.UpsertAsync(new PromptOverride
        {
            UserId = userId,
            Scope = "role-action",
            Role = Role,
            Action = Action,
            Template = "USER-OVERRIDDEN",
            SystemPrompt = "custom system",
        });

        var result = await _service.ResolveRoleActionAsync(userId, Role, Action);

        result.Should().NotBeNull();
        result.Template.Should().Be("USER-OVERRIDDEN");
        result.Source.Should().Be(PromptSource.UserOverride);
    }

    [Test]
    public async Task ResolveRoleActionAsync_FallsBackTo_SystemRoleActionDefault()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveRoleActionAsync(userId, Role, Action);

        result.Should().NotBeNull();
        result.Source.Should().Be(PromptSource.SystemRoleAction);
        // The Plan body (mapped from plan-implementation) mentions "implementation plan".
        result.Template.Should().Contain("implementation plan");
    }

    [Test]
    public async Task ResolveRoleActionAsync_AnonymousUser_UsesSystemDefaults()
    {
        var result = await _service.ResolveRoleActionAsync(null, "developer", "implement-feature");

        result.Should().NotBeNull();
        result.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    // ------------------------------------------------------------------
    // Fail-loud terminal: no override + no system default → TammaError.
    // This is the core Story 27-18 mandate — NO null, NO empty/plain fallback.
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_NoOverrideNoDefault_ThrowsTammaError()
    {
        var userId = Guid.NewGuid();

        // A taxonomy-valid action that this role does NOT own (deploy is
        // devops-only) has no developer system default → hard error.
        var act = async () => await _service.ResolveRoleActionAsync(userId, "developer", "deploy");

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("PROMPT.RESOLVE.NO_DEFAULT");
        ex.Which.Context.Should().ContainKey("role");
        ex.Which.Context.Should().ContainKey("action");
    }

    [Test]
    public async Task ResolveRoleActionAsync_UnknownRoleUnknownAction_ThrowsTammaError()
    {
        // Unknown role/action: no override, no system default → no silent
        // empty/plain fallback, a TammaError instead.
        var act = async () => await _service.ResolveRoleActionAsync(null, "unknown-role", "unknown-action");

        await act.Should().ThrowAsync<TammaError>();
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
            Role = Role,
            Action = Action,
            Template = "ALICE-ONLY",
        });

        var aliceResult = await _service.ResolveRoleActionAsync(alice, Role, Action);
        var bobResult = await _service.ResolveRoleActionAsync(bob, Role, Action);

        aliceResult.Template.Should().Be("ALICE-ONLY");
        aliceResult.Source.Should().Be(PromptSource.UserOverride);
        bobResult.Source.Should().Be(PromptSource.SystemRoleAction);
        bobResult.Template.Should().NotBe("ALICE-ONLY");
    }

    // ------------------------------------------------------------------
    // Upsert / Delete round-trip
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertAndDelete_RoleAction_WorksThroughService()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, Role, Action, new UpsertPromptInput(
            Template: "MY-TEMPLATE",
            SystemPrompt: "MY-SYSTEM",
            Variables: new[] { "foo" },
            EnableTools: true,
            MaxTokens: 2048));

        var resolved = await _service.ResolveRoleActionAsync(userId, Role, Action);
        resolved.Template.Should().Be("MY-TEMPLATE");

        var deleted = await _service.DeleteRoleActionAsync(userId, Role, Action);
        deleted.Should().BeTrue();

        // After delete, should fall through to system default
        var afterDelete = await _service.ResolveRoleActionAsync(userId, Role, Action);
        afterDelete.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    [Test]
    public async Task DeleteRoleActionAsync_ReturnsFalse_WhenOverrideMissing()
    {
        var userId = Guid.NewGuid();

        var deleted = await _service.DeleteRoleActionAsync(userId, Role, Action);

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
            userId, null, Role, Action,
            new UpsertPromptInput(Template: "T1"));

        wasCreated.Should().BeTrue();
        entity.Template.Should().Be("T1");
        entity.Version.Should().Be(1);
    }

    [Test]
    public async Task UpsertRoleActionAsync_SecondCall_ReportsWasCreatedFalse()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, Role, Action,
            new UpsertPromptInput(Template: "T1"));

        var (entity, wasCreated) = await _service.UpsertRoleActionAsync(
            userId, null, Role, Action,
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
            userId, null, Role, Action,
            new UpsertPromptInput(Template: "T1"));

        entity.CreatedBy.Should().Be(userId);
        entity.UpdatedBy.Should().Be(userId);
    }

    // ------------------------------------------------------------------
    // Audit prompts/003 — Resolved.Version flows through to the render
    // response. Defaults to 1 for system templates.
    // ------------------------------------------------------------------

    [Test]
    public async Task ResolveRoleActionAsync_SystemDefault_HasVersionOne()
    {
        var result = await _service.ResolveRoleActionAsync(null, Role, Action);

        result.Should().NotBeNull();
        result.Version.Should().Be(1);
    }

    [Test]
    public async Task ResolveRoleActionAsync_UserOverride_BumpsVersionOnEachUpdate()
    {
        var userId = Guid.NewGuid();
        await _service.UpsertRoleActionAsync(userId, null, Role, Action,
            new UpsertPromptInput(Template: "v1"));
        await _service.UpsertRoleActionAsync(userId, null, Role, Action,
            new UpsertPromptInput(Template: "v2"));
        await _service.UpsertRoleActionAsync(userId, null, Role, Action,
            new UpsertPromptInput(Template: "v3"));

        var resolved = await _service.ResolveRoleActionAsync(userId, Role, Action);

        resolved.Should().NotBeNull();
        resolved.Version.Should().Be(3);
    }
}
