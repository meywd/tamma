using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Auth;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Story 28-R2 follow-up B — handler-direct tests for the admin
/// impersonation surface. Covers:
/// <list type="bullet">
///   <item><description>Begin happy path — row is inserted, JWT is minted
///     with <c>imp_id</c>, IMPERSONATION.STARTED event carries actor +
///     target identity in tags + data.</description></item>
///   <item><description>End happy path — row is stamped, IMPERSONATION.ENDED
///     event includes duration + endedReason.</description></item>
///   <item><description>Reason charset gate — out-of-charset rejects 400,
///     no row written, no event emitted.</description></item>
///   <item><description>Active-list query — only rows with
///     <c>EndedAt IS NULL</c>; ended rows are filtered out.</description></item>
///   <item><description>Tenant existence gate — non-existent tenant
///     returns 404.</description></item>
///   <item><description>Target-user membership gate — a target user
///     who isn't a member of the target tenant returns 400.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AdminImpersonationTests
{
    private ControlPlaneDbContext _db = null!;
    private RecordingPlatformEventPublisher _publisher = null!;
    private TimeProvider _timeProvider = null!;
    private IAdminImpersonationService _service = null!;
    private IJwtService _jwt = null!;

    private static readonly Guid OperatorId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _publisher = new RecordingPlatformEventPublisher();
        _timeProvider = TimeProvider.System;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
                ["Tamma:Impersonation:MaxSessionMinutes"] = "60",
            })
            .Build();
        _jwt = new JwtService(config);
        _service = new AdminImpersonationService(_db, _jwt, config, _timeProvider);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static ClaimsPrincipal Operator()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "ops@tamma.dev"),
            new Claim("platformRole", "platform_admin"),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var id = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = id,
            Name = "TargetOrg",
            Slug = "target-" + id.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task SeedOperatorAsync()
    {
        _db.Users.Add(new User
        {
            Id = OperatorId,
            Email = "ops@tamma.dev",
            DisplayName = "Operator",
            AuthMethod = "email",
            Role = "owner",
            PlatformRole = "platform_admin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTenantMemberAsync(Guid tenantId, string role = "member")
    {
        var userId = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = userId,
            Email = $"member-{userId:N}@target.example",
            AuthMethod = "email",
            Role = role,
            PlatformRole = "user",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return userId;
    }

    // ── Begin ──────────────────────────────────────────────────────────────

    [Test]
    public async Task BeginImpersonation_HappyPath_InsertsRowAndEmitsEvent()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        http.Request.Headers["User-Agent"] = "TestUA/1.0";

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null,
                Reason: "Debug failed billing webhook"),
            http,
            _service,
            _publisher,
            Operator());

        // Response shape
        result.Should().BeOfType<Ok<AdminImpersonationsEndpoints.BeginImpersonationResponse>>();
        var body = ((Ok<AdminImpersonationsEndpoints.BeginImpersonationResponse>)result).Value!;
        body.TargetTenantId.Should().Be(tenantId);
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.ImpersonationId.Should().NotBe(Guid.Empty);

        // Audit row
        var row = await _db.AdminImpersonations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == body.ImpersonationId);
        row.Should().NotBeNull();
        row!.ImpersonatorUserId.Should().Be(OperatorId);
        row.ImpersonatorEmail.Should().Be("ops@tamma.dev");
        row.TargetTenantId.Should().Be(tenantId);
        row.TargetUserId.Should().BeNull();
        row.Reason.Should().Be("Debug failed billing webhook");
        row.EndedAt.Should().BeNull();
        row.IpAddress.Should().Be("10.0.0.1");
        row.UserAgent.Should().Be("TestUA/1.0");

        // JWT carries imp_id
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "imp_id"
            && c.Value == body.ImpersonationId.ToString("D"));
        // 15-minute cap
        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));

        // Platform event
        _publisher.Events.Should().ContainSingle(e => e.Type == "IMPERSONATION.STARTED");
        AssertActorAndImpersonationTags(_publisher.Events[0], body.ImpersonationId);
    }

    [Test]
    public async Task BeginImpersonation_WithTargetUser_RecordsUserId()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();
        var memberId = await SeedTenantMemberAsync(tenantId, role: "admin");

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: memberId,
                Reason: "Reproduce member-only bug"),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        result.Should().BeOfType<Ok<AdminImpersonationsEndpoints.BeginImpersonationResponse>>();
        var body = ((Ok<AdminImpersonationsEndpoints.BeginImpersonationResponse>)result).Value!;
        body.TargetUserId.Should().Be(memberId);

        var row = await _db.AdminImpersonations.AsNoTracking()
            .FirstAsync(r => r.Id == body.ImpersonationId);
        row.TargetUserId.Should().Be(memberId);
    }

    [Test]
    public async Task BeginImpersonation_RejectsEmptyReason()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null, Reason: "   "),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _db.AdminImpersonations.Should().BeEmpty();
        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task BeginImpersonation_RejectsNewlineInReason()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null, Reason: "phase1\nphase2"),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _db.AdminImpersonations.Should().BeEmpty();
        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task BeginImpersonation_RejectsHtmlMetacharsInReason()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null, Reason: "<script>alert(1)</script>"),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _db.AdminImpersonations.Should().BeEmpty();
    }

    [Test]
    public async Task BeginImpersonation_RejectsOverLengthReason()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null, Reason: new string('a', 501)),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task BeginImpersonation_NonExistentTenant_Returns404()
    {
        await SeedOperatorAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            Guid.NewGuid(),
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: null, Reason: "Investigate"),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _db.AdminImpersonations.Should().BeEmpty();
    }

    [Test]
    public async Task BeginImpersonation_TargetUserNotMember_Returns400()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();
        // A user that exists but is NOT a member of the target tenant.
        var orphanId = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = orphanId,
            Email = $"orphan-{orphanId:N}@example.com",
            AuthMethod = "email",
            Role = "member",
            PlatformRole = "user",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var result = await AdminImpersonationsEndpoints.BeginImpersonation(
            tenantId,
            new AdminImpersonationsEndpoints.BeginImpersonationRequest(
                TargetUserId: orphanId, Reason: "Bad target"),
            new DefaultHttpContext(),
            _service,
            _publisher,
            Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _db.AdminImpersonations.Should().BeEmpty();
    }

    // ── End ────────────────────────────────────────────────────────────────

    [Test]
    public async Task EndImpersonation_HappyPath_StampsRowAndEmitsEvent()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();
        var begin = await _service.BeginImpersonationAsync(
            Operator(), tenantId, null, "Investigate failed run", null, null);

        // Build a principal that mirrors the minted JWT (carries imp_id).
        var principal = MakePrincipalWithImpId(begin.ImpersonationId);

        var result = await AdminImpersonationsEndpoints.EndImpersonation(
            _service,
            _publisher,
            principal,
            new DefaultHttpContext());

        // EndImpersonation returns Results.Ok(new { ... anonymous ... }),
        // so the runtime type is Ok<TAnon>. Assert via the IStatusCodeHttpResult
        // surface to stay independent of the anonymous-type identity.
        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        var row = await _db.AdminImpersonations.AsNoTracking()
            .FirstAsync(r => r.Id == begin.ImpersonationId);
        row.EndedAt.Should().NotBeNull();
        row.EndedReason.Should().Be("explicit_exit");

        _publisher.Events.Should().ContainSingle(e => e.Type == "IMPERSONATION.ENDED");
        var ended = _publisher.Events.First(e => e.Type == "IMPERSONATION.ENDED");
        AssertActorAndImpersonationTags(ended, begin.ImpersonationId);
        // Duration is recorded
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(ended.Data!);
        data!.Should().ContainKey("durationSeconds");
        data["endedReason"]!.ToString().Should().Be("explicit_exit");
    }

    [Test]
    public async Task EndImpersonation_NoImpIdClaim_Returns400()
    {
        var principal = Operator();  // no imp_id

        var result = await AdminImpersonationsEndpoints.EndImpersonation(
            _service, _publisher, principal, new DefaultHttpContext());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task EndImpersonation_AlreadyEnded_Returns410()
    {
        await SeedOperatorAsync();
        var tenantId = await SeedTenantAsync();
        var begin = await _service.BeginImpersonationAsync(
            Operator(), tenantId, null, "First end", null, null);
        await _service.EndImpersonationAsync(begin.ImpersonationId, "explicit_exit");

        var principal = MakePrincipalWithImpId(begin.ImpersonationId);
        var result = await AdminImpersonationsEndpoints.EndImpersonation(
            _service, _publisher, principal, new DefaultHttpContext());

        StatusCodeOf(result).Should().Be(StatusCodes.Status410Gone);
    }

    // ── Active list ────────────────────────────────────────────────────────

    [Test]
    public async Task ListActive_ReturnsOnlyOpenSessions()
    {
        await SeedOperatorAsync();
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var openA = await _service.BeginImpersonationAsync(
            Operator(), tenantA, null, "Open A", null, null);
        var openB = await _service.BeginImpersonationAsync(
            Operator(), tenantB, null, "Open B", null, null);
        var ended = await _service.BeginImpersonationAsync(
            Operator(), tenantA, null, "Will close", null, null);
        await _service.EndImpersonationAsync(ended.ImpersonationId, "explicit_exit");

        var result = await AdminImpersonationsEndpoints.ListActive(_service);

        result.Should().BeOfType<Ok<AdminImpersonationsEndpoints.ActiveImpersonationListResponse>>();
        var body = ((Ok<AdminImpersonationsEndpoints.ActiveImpersonationListResponse>)result).Value!;
        body.Count.Should().Be(2);
        body.Items.Select(i => i.Id).Should().Contain(new[] { openA.ImpersonationId, openB.ImpersonationId });
        body.Items.Select(i => i.Id).Should().NotContain(ended.ImpersonationId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue)
            return s.StatusCode.Value;
        if (result is Ok<object> or Ok<AdminImpersonationsEndpoints.BeginImpersonationResponse>
            or Ok<AdminImpersonationsEndpoints.ActiveImpersonationListResponse>)
            return StatusCodes.Status200OK;
        throw new InvalidOperationException(
            $"Result {result.GetType().Name} has no status code");
    }

    private static ClaimsPrincipal MakePrincipalWithImpId(Guid impId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "ops@tamma.dev"),
            new Claim("platformRole", "platform_admin"),
            new Claim("imp_id", impId.ToString("D")),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static void AssertActorAndImpersonationTags(PlatformEvent evt, Guid expectedImpId)
    {
        // Tags channel
        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags.Should().NotBeNull();
        tags!["actorUserId"].Should().Be(OperatorId.ToString("D"));
        tags["actorEmail"].Should().Be("ops@tamma.dev");
        tags["actorPlatformRole"].Should().Be("platform_admin");
        tags["impersonationId"].Should().Be(expectedImpId.ToString("D"));

        // Data channel — defence-in-depth (immutable record)
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(evt.Data!);
        data.Should().NotBeNull();
        data!["actorUserId"]!.ToString().Should().Be(OperatorId.ToString("D"));
        data["actorEmail"]!.ToString().Should().Be("ops@tamma.dev");
        data["actorPlatformRole"]!.ToString().Should().Be("platform_admin");
        data["impersonationId"]!.ToString().Should().Be(expectedImpId.ToString("D"));
    }

}
