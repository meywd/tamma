using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Onboarding;

/// <summary>
/// Direct-handler tests for <see cref="OnboardingEndpoints"/> (story 18-4).
/// Covers the wizard polling status endpoint across the four states the
/// dashboard surfaces, plus the install-github redirect.
///
/// Status state-table (rows = test case):
///
/// <code>
/// | EmailVerified | HasOrg | HasInstallation | Description                  |
/// |---------------|--------|------------------|------------------------------|
/// | false         | false  | false            | brand-new password user      |
/// | true (github) | true   | false            | github-OAuth, has personal   |
/// | true          | true   | false            | password user, verified, org |
/// | true          | true   | true             | onboarding complete          |
/// | true          | true   | true (suspended) | install suspended on GitHub  |
/// </code>
/// </summary>
[TestFixture]
public class OnboardingEndpointsTests
{
    private IServiceScope _scope = null!;
    private IUserRepository _users = null!;
    private ITenantRepository _tenants = null!;
    private ITenantMembershipRepository _memberships = null!;
    private IInstallationRepository _installations = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _users = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _tenants = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _memberships = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _installations = _scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["GitHubApp:InstallUrl"] = "https://github.com/apps/tamma-test/installations/new",
            })
            .Build();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ─── GetStatus ─────────────────────────────────────────────────────────

    [Test]
    public async Task GetStatus_Returns401_WhenPrincipalLacksUserClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // no claims
        var result = await OnboardingEndpoints.GetStatus(
            principal, _users, _memberships, _installations);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task GetStatus_Returns401_WhenUserMissingFromDatabase()
    {
        var principal = Principal(Guid.NewGuid()); // unknown user id
        var result = await OnboardingEndpoints.GetStatus(
            principal, _users, _memberships, _installations);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task GetStatus_NewPasswordUser_AllFlagsFalse()
    {
        var user = await _users.CreateAsync(new User
        {
            Email = "new@example.com",
            AuthMethod = "email",
            EmailVerified = false,
        });

        var payload = await GetStatusPayload(user);

        payload.EmailVerified.Should().BeFalse();
        payload.HasOrg.Should().BeFalse();
        payload.TenantId.Should().BeNull();
        payload.HasInstallation.Should().BeFalse();
        payload.InstallationCount.Should().Be(0);
        payload.Installations.Should().BeEmpty();
    }

    [Test]
    public async Task GetStatus_GitHubOAuthUser_EmailVerifiedEvenWithoutFlag()
    {
        // GitHub-OAuth users bypass the verify-email click; the AuthMethod
        // is the trustworthy signal.
        var user = await _users.CreateAsync(new User
        {
            Email = "gh@example.com",
            AuthMethod = "github",
            EmailVerified = false,
        });

        var payload = await GetStatusPayload(user);

        payload.EmailVerified.Should().BeTrue();
    }

    [Test]
    public async Task GetStatus_VerifiedUserWithTenant_HasOrgTrue_NoInstall()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();

        var payload = await GetStatusPayload(user);

        payload.EmailVerified.Should().BeTrue();
        payload.HasOrg.Should().BeTrue();
        payload.TenantId.Should().Be(tenantId);
        payload.HasInstallation.Should().BeFalse();
        payload.InstallationCount.Should().Be(0);
    }

    [Test]
    public async Task GetStatus_WithLinkedInstallation_AllFlagsTrue_AndIncludesRepos()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();

        var install = await _installations.CreateAsync(new GitHubInstallation
        {
            InstallationId = 11111,
            AccountLogin = "acme-corp",
            AccountType = "Organization",
            AppId = 42,
            TenantId = tenantId,
            Permissions = "{}",
        });
        await _installations.AddRepoAsync(install.Id, 9001, "acme-corp/api");
        await _installations.AddRepoAsync(install.Id, 9002, "acme-corp/web");

        var payload = await GetStatusPayload(user);

        payload.HasInstallation.Should().BeTrue();
        payload.InstallationCount.Should().Be(1);
        payload.Installations.Should().HaveCount(1);
        var inst = payload.Installations[0];
        inst.InstallationId.Should().Be(11111);
        inst.AccountLogin.Should().Be("acme-corp");
        inst.AccountType.Should().Be("Organization");
        inst.Suspended.Should().BeFalse();
        inst.RepoCount.Should().Be(2);
        inst.Repos.Select(r => r.FullName).Should().BeEquivalentTo(
            new[] { "acme-corp/api", "acme-corp/web" });
    }

    [Test]
    public async Task GetStatus_SuspendedInstallation_HasInstallFalse_ButRowIncluded()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();

        var install = await _installations.CreateAsync(new GitHubInstallation
        {
            InstallationId = 22222,
            AccountLogin = "paused-org",
            AccountType = "Organization",
            AppId = 42,
            TenantId = tenantId,
            Permissions = "{}",
        });
        await _installations.SetSuspendedAsync(install.InstallationId, true);

        var payload = await GetStatusPayload(user);

        // Suspended installs do NOT count as a usable installation — the
        // wizard surfaces them so the user can re-enable on GitHub, but
        // the "install" step does not advance.
        payload.HasInstallation.Should().BeFalse();
        payload.InstallationCount.Should().Be(1);
        payload.Installations[0].Suspended.Should().BeTrue();
    }

    [Test]
    public async Task GetStatus_OrphanInstallationOnDifferentTenant_NotIncluded()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();

        // Install bound to a *different* tenant (owned by a different real user
        // — Tenant.OwnerId is FK-constrained to users.id) — must not leak across.
        var otherOwner = await _users.CreateAsync(new User
        {
            Email = "other-owner@example.com", AuthMethod = "email",
        });
        var otherTenant = await _tenants.CreateAsync(new Tenant
        {
            Name = "Other", Slug = "other-co", Type = "org", OwnerId = otherOwner.Id,
        });
        await _installations.CreateAsync(new GitHubInstallation
        {
            InstallationId = 33333,
            AccountLogin = "other",
            AccountType = "User",
            AppId = 42,
            TenantId = otherTenant.Id,
            Permissions = "{}",
        });

        // Plus an orphan (TenantId null) — also excluded.
        await _installations.CreateAsync(new GitHubInstallation
        {
            InstallationId = 44444,
            AccountLogin = "orphan",
            AccountType = "User",
            AppId = 42,
            TenantId = null,
            Permissions = "{}",
        });

        var payload = await GetStatusPayload(user);

        payload.HasInstallation.Should().BeFalse();
        payload.InstallationCount.Should().Be(0);
        payload.Installations.Should().BeEmpty();
    }

    [Test]
    public async Task GetStatus_FallsBackToMembership_WhenActiveTenantNotSet()
    {
        // Edge case: invite-accept path mutates `User.TenantId` on first
        // accept, but a user could land here mid-flow with memberships
        // populated and TenantId still null.
        var user = await _users.CreateAsync(new User
        {
            Email = "membership-only@example.com",
            AuthMethod = "github",
        });
        // Tenant.OwnerId FKs to users.id; seed a real owner separately.
        var owner = await _users.CreateAsync(new User
        {
            Email = "tenant-owner@example.com", AuthMethod = "email",
        });
        var tenant = await _tenants.CreateAsync(new Tenant
        {
            Name = "Member Org", Slug = "member-org", Type = "org", OwnerId = owner.Id,
        });
        await _memberships.AddAsync(tenant.Id, user.Id, "member");
        // intentionally do NOT call UpdateActiveTenantAsync here.

        var payload = await GetStatusPayload(user);

        payload.HasOrg.Should().BeTrue();
        payload.TenantId.Should().Be(tenant.Id);
    }

    // ─── InstallGitHub ─────────────────────────────────────────────────────

    [Test]
    public async Task InstallGitHub_Returns401_WithoutPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var result = OnboardingEndpoints.InstallGitHub(principal, _config);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task InstallGitHub_Redirects_ToInstallUrl_WithStateToken()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();
        var principal = PrincipalWithTenant(user.Id, tenantId);

        var result = OnboardingEndpoints.InstallGitHub(principal, _config);

        // Strongly-typed redirect surface.
        result.Should().BeOfType<RedirectHttpResult>();
        var redirect = (RedirectHttpResult)result;
        redirect.Url.Should().StartWith("https://github.com/apps/tamma-test/installations/new?state=");
        // Token must be opaque non-empty after the prefix.
        var stateParam = ExtractStateParam(redirect.Url);
        stateParam.Should().NotBeNullOrEmpty();
        stateParam!.Split('.').Should().HaveCount(3, "state is a 3-segment JWT");
    }

    [Test]
    public async Task InstallGitHub_FallsBackToDefaultInstallUrl_WhenConfigMissing()
    {
        var (user, tenantId) = await SeedVerifiedUserWithTenantAsync();
        var principal = PrincipalWithTenant(user.Id, tenantId);
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                // GitHubApp:InstallUrl intentionally omitted
            })
            .Build();

        var result = OnboardingEndpoints.InstallGitHub(principal, emptyConfig);

        var redirect = (RedirectHttpResult)result;
        redirect.Url.Should().StartWith(
            "https://github.com/apps/tamma-dev/installations/new?state=");
    }

    [Test]
    public void IssueStateToken_RoundTrip_SignatureMatches()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var token = OnboardingEndpoints.IssueStateToken(_config, userId, tenantId);

        // Verify the signature matches via the SAME secret — defends
        // against a future regression where IssueStateToken stops signing
        // with Jwt:Secret.
        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        var signingInput = $"{parts[0]}.{parts[1]}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!));
        var expected = Base64UrlEncode(hmac.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(signingInput)));
        parts[2].Should().Be(expected);

        // Decoded payload carries the right claim shape.
        var payloadJson = System.Text.Encoding.UTF8.GetString(
            Base64UrlDecode(parts[1]));
        payloadJson.Should().Contain($"\"sub\":\"{userId}\"");
        payloadJson.Should().Contain($"\"tid\":\"{tenantId}\"");
        payloadJson.Should().Contain("\"nonce\":\"");
        payloadJson.Should().Contain("\"typ\":\"github-install-state\"");
    }

    [Test]
    public void IssueStateToken_GeneratesUniqueNonces()
    {
        var userId = Guid.NewGuid();
        var t1 = OnboardingEndpoints.IssueStateToken(_config, userId, null);
        var t2 = OnboardingEndpoints.IssueStateToken(_config, userId, null);
        t1.Should().NotBe(t2);
    }

    [Test]
    public void IssueStateToken_Throws_WhenSecretMissingOrTooShort()
    {
        var weakConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "short", // < 32 chars
            })
            .Build();

        Action act = () => OnboardingEndpoints.IssueStateToken(weakConfig, Guid.NewGuid(), null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private async Task<OnboardingStatusResponse> GetStatusPayload(User user)
    {
        var principal = Principal(user.Id);
        var result = await OnboardingEndpoints.GetStatus(
            principal, _users, _memberships, _installations);
        // GetStatus returns Results.Ok which we can introspect via the
        // strongly-typed Ok<T> wrapper.
        result.Should().BeOfType<Ok<OnboardingStatusResponse>>();
        return ((Ok<OnboardingStatusResponse>)result).Value!;
    }

    private async Task<(User user, Guid tenantId)> SeedVerifiedUserWithTenantAsync()
    {
        var user = await _users.CreateAsync(new User
        {
            Email = "verified@example.com",
            AuthMethod = "email",
            EmailVerified = true,
        });
        var tenant = await _tenants.CreateAsync(new Tenant
        {
            Name = "Verified Org",
            Slug = "verified-org",
            Type = "org",
            OwnerId = user.Id,
        });
        await _memberships.AddAsync(tenant.Id, user.Id, "owner");
        await _users.UpdateActiveTenantAsync(user.Id, tenant.Id);
        // Re-fetch so callers see the persisted active tenant id.
        var refreshed = await _users.GetByIdAsync(user.Id);
        return (refreshed!, tenant.Id);
    }

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal PrincipalWithTenant(Guid userId, Guid tenantId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("tenantId", tenantId.ToString()),
            new Claim("tid", tenantId.ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static string? ExtractStateParam(string url)
    {
        var idx = url.IndexOf("state=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var raw = url[(idx + "state=".Length)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0) raw = raw[..amp];
        return Uri.UnescapeDataString(raw);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static async Task<int> ExecuteAndGetStatus(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }
}
