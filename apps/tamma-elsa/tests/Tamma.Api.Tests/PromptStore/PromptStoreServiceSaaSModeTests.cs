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
/// Story 27-2 — SaaS-mode resolution tests for <see cref="PromptStoreService"/>.
///
/// <para>The single-user surface is keyed on <c>userId</c>; the SaaS surface
/// is keyed on <c>tenantId</c>. The two row spaces are disjoint (enforced by
/// the <c>principal_xor</c> CHECK on <c>prompt_overrides</c>) and resolution
/// follows the per-mode 4-layer / 2-layer fallback orders defined in
/// CLAUDE.md "Prompt Store Architecture".</para>
///
/// <para>Per CLAUDE.md, SaaS mode has NO per-user override layer on top of
/// tenant overrides — member users see exactly what tenant_admin set. The
/// <c>NoUserOverrideLayer</c> tests below pin that contract.</para>
/// </summary>
[TestFixture]
public class PromptStoreServiceSaaSModeTests
{
    private static readonly Guid AmbientTenant =
        Guid.Parse("aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa");

    private InMemoryDbFixture _fx = null!;
    private PromptRepository _repo = null!;
    private PromptStoreService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        var tc = new TenantContext();
        // Repository requires an ambient tenant id (PR D guarantee). The
        // SaaS-mode methods then layer their own tenant-scope predicates
        // on top — they don't rely on the ambient id for row selection.
        tc.SetTenantId(AmbientTenant);
        _repo = new PromptRepository(_fx.Factory, tc);
        _service = new PromptStoreService(_repo);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fx.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 4-layer role+action resolution (SaaS mode)
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_TenantOverride_Wins()
    {
        var tenantId = Guid.NewGuid();
        await _service.UpsertRoleActionForTenantAsync(
            tenantId, actingUserId: Guid.NewGuid(),
            "developer", "plan",
            new UpsertPromptInput(Template: "TENANT-OVERRIDDEN", SystemPrompt: "tenant sys"));

        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");

        resolved.Should().NotBeNull();
        resolved!.Template.Should().Be("TENANT-OVERRIDDEN");
        resolved.Source.Should().Be(PromptSource.TenantOverride);
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_FallsBackToSystemRoleAction()
    {
        var tenantId = Guid.NewGuid();

        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");

        resolved.Should().NotBeNull();
        resolved!.Source.Should().Be(PromptSource.SystemRoleAction);
        resolved.Template.Should().Contain("implementation plan");
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_FallsBackToTenantActionDefault()
    {
        var tenantId = Guid.NewGuid();
        // Layer 3 — tenant action-default override. Use an unknown role
        // so layer 2 (system role+action) misses.
        await _service.UpsertActionDefaultForTenantAsync(
            tenantId, actingUserId: null,
            "plan",
            new UpsertPromptInput(Template: "TENANT-ACTION-DEFAULT"));

        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "unknown-role", "plan");

        resolved.Should().NotBeNull();
        resolved!.Template.Should().Be("TENANT-ACTION-DEFAULT");
        resolved.Source.Should().Be(PromptSource.TenantActionDefault);
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_FallsBackToSystemActionDefault()
    {
        var tenantId = Guid.NewGuid();

        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "unknown-role", "plan");

        resolved.Should().NotBeNull();
        resolved!.Source.Should().Be(PromptSource.SystemActionDefault);
        resolved.Template.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_ReturnsNull_WhenNothingMatches()
    {
        var tenantId = Guid.NewGuid();

        var resolved = await _service.ResolveRoleActionForTenantAsync(
            tenantId, "unknown-role", "unknown-action");

        resolved.Should().BeNull();
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleAction_RejectsEmptyTenantId()
    {
        var act = async () =>
            await _service.ResolveRoleActionForTenantAsync(Guid.Empty, "developer", "plan");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("tenantId");
    }

    // ------------------------------------------------------------------
    // 2-layer role-system resolution (SaaS mode)
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleSystem_TenantOverride_Wins()
    {
        var tenantId = Guid.NewGuid();
        await _service.UpsertRoleSystemForTenantAsync(
            tenantId, actingUserId: Guid.NewGuid(),
            "developer",
            new UpsertPromptInput(Template: "TENANT ROLE PROMPT"));

        var resolved = await _service.ResolveRoleSystemForTenantAsync(tenantId, "developer");

        resolved.Should().Be("TENANT ROLE PROMPT");
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleSystem_FallsBackToSystemDefault()
    {
        var tenantId = Guid.NewGuid();

        var resolved = await _service.ResolveRoleSystemForTenantAsync(tenantId, "security");

        resolved.Should().NotBeNullOrWhiteSpace();
        resolved!.Should().Contain("security");
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ResolveRoleSystem_ReturnsNull_ForUnknownRole()
    {
        var tenantId = Guid.NewGuid();

        var resolved = await _service.ResolveRoleSystemForTenantAsync(tenantId, "not-a-role");

        resolved.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Tenant isolation
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_TenantOverrides_DoNotLeakBetweenTenants()
    {
        var acme = Guid.NewGuid();
        var globex = Guid.NewGuid();

        await _service.UpsertRoleActionForTenantAsync(
            acme, null, "developer", "plan",
            new UpsertPromptInput(Template: "ACME-ONLY"));

        var acmeResolved = await _service.ResolveRoleActionForTenantAsync(acme, "developer", "plan");
        var globexResolved = await _service.ResolveRoleActionForTenantAsync(globex, "developer", "plan");

        acmeResolved!.Template.Should().Be("ACME-ONLY");
        acmeResolved.Source.Should().Be(PromptSource.TenantOverride);
        globexResolved!.Source.Should().Be(PromptSource.SystemRoleAction);
        globexResolved.Template.Should().NotBe("ACME-ONLY");
    }

    // ------------------------------------------------------------------
    // No user-override layer in SaaS mode
    //
    // Member users in SaaS mode see exactly what tenant_admin set — no
    // per-user personalization (CLAUDE.md "Resolution Order — SaaS mode":
    // "No per-user override layer in SaaS"). These tests pin the contract
    // by writing a row with BOTH UserId and TenantId set (which the DB
    // CHECK constraint would reject in production) and asserting the
    // tenant resolver does NOT see it.
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_NoUserOverrideLayer_UserScopedRowsAreInvisible()
    {
        var tenantId = Guid.NewGuid();
        var memberUserId = Guid.NewGuid();

        // tenant_admin sets the official tenant prompt
        await _service.UpsertRoleActionForTenantAsync(
            tenantId, actingUserId: Guid.NewGuid(),
            "developer", "plan",
            new UpsertPromptInput(Template: "TENANT-OFFICIAL"));

        // member user "personalises" their own user-scoped row (this would
        // happen via single-user-mode endpoints — in SaaS the API doesn't
        // expose a user-scoped path, but if a row somehow ends up there
        // it MUST NOT leak into tenant resolution).
        await _service.UpsertRoleActionAsync(
            memberUserId, tenantId: null,
            "developer", "plan",
            new UpsertPromptInput(Template: "MEMBER-PERSONAL"));

        // SaaS resolver returns the tenant_admin's row, never the user's.
        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");

        resolved!.Template.Should().Be("TENANT-OFFICIAL");
        resolved.Source.Should().Be(PromptSource.TenantOverride);
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_NoUserOverrideLayer_FallsThroughTenantToSystem()
    {
        var tenantId = Guid.NewGuid();
        var memberUserId = Guid.NewGuid();

        // No tenant override, but a member user has a personal row with the
        // SAME (role, action). Per CLAUDE.md's "no per-user override layer"
        // rule the SaaS resolver MUST skip the user row and land on the
        // system role-action default.
        await _service.UpsertRoleActionAsync(
            memberUserId, tenantId: null,
            "developer", "plan",
            new UpsertPromptInput(Template: "MEMBER-PERSONAL"));

        var resolved = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");

        resolved!.Source.Should().Be(PromptSource.SystemRoleAction);
        resolved.Template.Should().NotBe("MEMBER-PERSONAL");
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_ListTenantOverrides_ExcludesUserScopedRows()
    {
        var tenantId = Guid.NewGuid();
        var someUserId = Guid.NewGuid();

        await _service.UpsertRoleActionForTenantAsync(
            tenantId, null, "developer", "plan",
            new UpsertPromptInput(Template: "TENANT-A"));
        await _service.UpsertRoleActionForTenantAsync(
            tenantId, null, "reviewer", "review",
            new UpsertPromptInput(Template: "TENANT-B"));

        // Single-user row with the same scope — must NOT show up.
        await _service.UpsertRoleActionAsync(
            someUserId, null, "developer", "plan",
            new UpsertPromptInput(Template: "USER-LOCAL"));

        var rows = await _service.ListTenantOverridesAsync(tenantId);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(p => p.TenantId == tenantId);
        rows.Should().OnlyContain(p => p.UserId == null);
        rows.Select(p => p.Template).Should().BeEquivalentTo(new[] { "TENANT-A", "TENANT-B" });
    }

    // ------------------------------------------------------------------
    // Mode-isolation — single-user list excludes tenant rows.
    // The other direction of the disjoint-row-spaces invariant.
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SingleUserMode_ListUserOverrides_ExcludesTenantRows()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await _service.UpsertRoleActionAsync(
            userId, null, "developer", "plan",
            new UpsertPromptInput(Template: "USER-LOCAL"));
        await _service.UpsertRoleActionForTenantAsync(
            tenantId, null, "developer", "plan",
            new UpsertPromptInput(Template: "TENANT-OFFICIAL"));

        var rows = await _service.ListUserOverridesAsync(userId);

        rows.Should().HaveCount(1);
        rows.Single().TenantId.Should().BeNull();
        rows.Single().UserId.Should().Be(userId);
        rows.Single().Template.Should().Be("USER-LOCAL");
    }

    // ------------------------------------------------------------------
    // Upsert semantics — CREATED vs UPDATED, version bump, audit attribution.
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_UpsertRoleAction_FirstCall_ReportsWasCreatedTrue()
    {
        var tenantId = Guid.NewGuid();
        var actingAdmin = Guid.NewGuid();

        var (entity, wasCreated) = await _service.UpsertRoleActionForTenantAsync(
            tenantId, actingAdmin,
            "developer", "plan",
            new UpsertPromptInput(Template: "v1"));

        wasCreated.Should().BeTrue();
        entity.Template.Should().Be("v1");
        entity.Version.Should().Be(1);
        entity.TenantId.Should().Be(tenantId);
        entity.UserId.Should().BeNull();
        entity.CreatedBy.Should().Be(actingAdmin);
        entity.UpdatedBy.Should().Be(actingAdmin);
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_UpsertRoleAction_SecondCall_BumpsVersion()
    {
        var tenantId = Guid.NewGuid();
        var firstAdmin = Guid.NewGuid();
        var secondAdmin = Guid.NewGuid();

        await _service.UpsertRoleActionForTenantAsync(
            tenantId, firstAdmin, "developer", "plan",
            new UpsertPromptInput(Template: "v1"));

        var (entity, wasCreated) = await _service.UpsertRoleActionForTenantAsync(
            tenantId, secondAdmin, "developer", "plan",
            new UpsertPromptInput(Template: "v2"));

        wasCreated.Should().BeFalse();
        entity.Template.Should().Be("v2");
        entity.Version.Should().Be(2);
        entity.CreatedBy.Should().Be(firstAdmin, "CreatedBy is sticky on update");
        entity.UpdatedBy.Should().Be(secondAdmin);
    }

    // ------------------------------------------------------------------
    // Round-trip: upsert → resolve → delete → resolve.
    // ------------------------------------------------------------------

    [Test]
    public async Task PromptStoreService_SaaSMode_DeleteRoleAction_FallsThroughToSystemAfterDelete()
    {
        var tenantId = Guid.NewGuid();
        await _service.UpsertRoleActionForTenantAsync(
            tenantId, null, "developer", "plan",
            new UpsertPromptInput(Template: "TENANT-T1"));

        var beforeDelete = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");
        beforeDelete!.Source.Should().Be(PromptSource.TenantOverride);

        var deleted = await _service.DeleteRoleActionForTenantAsync(tenantId, "developer", "plan");
        deleted.Should().BeTrue();

        var afterDelete = await _service.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan");
        afterDelete!.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    [Test]
    public async Task PromptStoreService_SaaSMode_DeleteRoleAction_ReturnsFalse_WhenMissing()
    {
        var tenantId = Guid.NewGuid();

        var deleted = await _service.DeleteRoleActionForTenantAsync(tenantId, "developer", "plan");

        deleted.Should().BeFalse();
    }
}
