using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.AcceptanceRules;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.AcceptanceRules;

/// <summary>
/// Unit tests for <see cref="AcceptanceRulesService"/> with a Moq'd repository
/// (Story 39-5 AC4, AC6): three-tier resolution ordering per mode, source +
/// version provenance, mode isolation (tenant path never consults user rows),
/// fail-loud unknown-key rejection before any write, defensive read validation,
/// and best-effort event emission.
/// </summary>
[TestFixture]
public class AcceptanceRulesServiceTests
{
    private Mock<IAcceptanceRulesRepository> _repo = null!;
    private AcceptanceRulesService _service = null!;

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IAcceptanceRulesRepository>(MockBehavior.Strict);
        _service = new AcceptanceRulesService(_repo.Object);
    }

    private static AcceptanceRulesOverride Row(string? key, Tamma.Core.Documents.Policy.AcceptanceRules rules, int version = 3) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        DocumentTypeKey = key,
        RulesJson = AcceptanceRulesJson.Serialize(rules),
        Version = version,
    };

    // ── Resolution ordering (single-user) ──

    [Test]
    public async Task ResolveAsync_type_override_wins()
    {
        var custom = AcceptanceDefaults.Rules with { AutonomyLevel = 95 };
        _repo.Setup(r => r.GetAsync(UserId, "plan")).ReturnsAsync(Row("plan", custom, 7));

        var resolved = await _service.ResolveAsync(UserId, DocumentTypeKey.Plan);

        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Version.Should().Be(7);
        resolved.Rules.AutonomyLevel.Should().Be(95);
        resolved.DocumentTypeKey.Should().Be("plan");
    }

    [Test]
    public async Task ResolveAsync_falls_back_to_base_override()
    {
        var baseRules = AcceptanceDefaults.Rules with { AutonomyLevel = 88 };
        _repo.Setup(r => r.GetAsync(UserId, "design")).ReturnsAsync((AcceptanceRulesOverride?)null);
        _repo.Setup(r => r.GetAsync(UserId, null)).ReturnsAsync(Row(null, baseRules, 4));

        var resolved = await _service.ResolveAsync(UserId, DocumentTypeKey.Design);

        resolved.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault);
        resolved.Version.Should().Be(4);
        resolved.Rules.AutonomyLevel.Should().Be(88);
    }

    [Test]
    public async Task ResolveAsync_falls_back_to_static_default()
    {
        _repo.Setup(r => r.GetAsync(UserId, "design")).ReturnsAsync((AcceptanceRulesOverride?)null);
        _repo.Setup(r => r.GetAsync(UserId, null)).ReturnsAsync((AcceptanceRulesOverride?)null);

        var resolved = await _service.ResolveAsync(UserId, DocumentTypeKey.Design);

        resolved.Source.Should().Be(AcceptanceRulesSource.SystemDefault);
        resolved.Version.Should().Be(1);
        resolved.Rules.Should().Be(AcceptanceDefaults.For(DocumentTypeKey.Design));
    }

    // ── Mode isolation ──

    [Test]
    public async Task ResolveForTenantAsync_never_consults_user_rows()
    {
        _repo.Setup(r => r.GetByTenantAsync(TenantId, "plan")).ReturnsAsync((AcceptanceRulesOverride?)null);
        _repo.Setup(r => r.GetByTenantAsync(TenantId, null)).ReturnsAsync((AcceptanceRulesOverride?)null);

        var resolved = await _service.ResolveForTenantAsync(TenantId, DocumentTypeKey.Plan);

        resolved.Source.Should().Be(AcceptanceRulesSource.SystemDefault);
        _repo.Verify(r => r.GetAsync(It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    // ── Corrupt row defensive validation ──

    [Test]
    public void ResolveAsync_throws_on_corrupt_rules_json()
    {
        var bad = new AcceptanceRulesOverride
        {
            DocumentTypeKey = "plan",
            // Story 43-11 AC14: 5 is now a LEGAL dial position (the range widened to
            // [1,100]), so the corrupt-row vector must be something Validate() still
            // rejects on read — well above Max.
            RulesJson = AcceptanceRulesJson.Serialize(AcceptanceDefaults.Rules with { AutonomyLevel = AutonomyDial.Max + 1000 }),
            Version = 1,
        };
        _repo.Setup(r => r.GetAsync(UserId, "plan")).ReturnsAsync(bad);

        _service.Invoking(s => s.ResolveAsync(UserId, DocumentTypeKey.Plan))
            .Should().ThrowAsync<TammaError>();
    }

    // ── Upsert rejects unknown key before touching the repo ──

    [Test]
    public async Task UpsertAsync_unknown_type_key_throws_before_repository_touch()
    {
        await _service.Invoking(s => s.UpsertAsync(UserId, "not-a-type", AcceptanceDefaults.Rules))
            .Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "DOCUMENT.TYPE.UNKNOWN");

        _repo.Verify(r => r.UpsertAsync(It.IsAny<AcceptanceRulesOverride>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Test]
    public async Task UpsertAsync_invalid_rules_throws_before_repository_touch()
    {
        var invalid = AcceptanceDefaults.Rules with { AutonomyLevel = 200 };
        await _service.Invoking(s => s.UpsertAsync(UserId, "plan", invalid))
            .Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "ACCEPTANCE_RULES.INVALID");

        _repo.Verify(r => r.UpsertAsync(It.IsAny<AcceptanceRulesOverride>(), It.IsAny<Guid?>()), Times.Never);
    }

    // ── Event emission ──

    [Test]
    public async Task UpsertAsync_emits_created_event()
    {
        var events = new Mock<IEventRepository>();
        events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent d) => d);
        var eventsService = new AcceptanceRulesEventsService(events.Object);
        var service = new AcceptanceRulesService(_repo.Object, eventsService);

        var saved = Row("plan", AcceptanceDefaults.Rules, 1);
        _repo.Setup(r => r.UpsertAsync(It.IsAny<AcceptanceRulesOverride>(), UserId))
            .ReturnsAsync((saved, true));

        await service.UpsertAsync(UserId, "plan", AcceptanceDefaults.Rules);

        events.Verify(e => e.AppendAsync(It.Is<DomainEvent>(
            d => d.Type == AcceptanceRulesEventsService.CreatedType)), Times.Once);
    }
}
