using System.Net;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Sodium;
using Tamma.Platforms.Gitea;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-8 — <see cref="GiteaCiSecretsProvisioner"/> tests. Gitea's
/// wire format mirrors GitHub's libsodium sealed-box dance (1.21+);
/// the per-scope endpoint paths differ. Forgejo extends from Gitea.
/// </summary>
[TestFixture]
public sealed class GiteaCiSecretsProvisionerTests
{
    private static Task<bool> TestAuth(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", "test-token");
        return Task.FromResult(true);
    }

    private const string KeyId = "test-key-id-12345";

    private static (HttpClient http, CiSecretsProvisionerTestHandler handler,
        KeyPair keypair, string publicKeyB64) BuildClient()
    {
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);

        var handler = new CiSecretsProvisionerTestHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gitea.example.com"),
        };
        return (http, handler, keypair, publicKeyB64);
    }

    private static string PublicKeyJson(string keyB64) =>
        JsonSerializer.Serialize(new { key = keyB64, key_id = KeyId });

    // ── Repo-scope happy path + libsodium ─────────────────────────────

    [Test]
    public async Task ProvisionSecret_RepoScope_HappyPath_Encrypted()
    {
        var (http, handler, keypair, publicKeyB64) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/repos/owner/repo/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/repos/owner/repo/actions/secrets/MY_SECRET",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("owner", "repo") },
            "MY_SECRET",
            new RedactedSecret("plaintext"));

        results[0].Success.Should().BeTrue();
        results[0].Kind.Should().Be(PlatformKind.Gitea);

        // Round-trip: decrypt the wire payload back to plaintext.
        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var doc = JsonDocument.Parse(put.Body!);
        var encryptedB64 = doc.RootElement.GetProperty("encrypted_value").GetString();
        var decrypted = SealedPublicKeyBox.Open(
            Convert.FromBase64String(encryptedB64!),
            keypair.PrivateKey, keypair.PublicKey);
        System.Text.Encoding.UTF8.GetString(decrypted).Should().Be("plaintext");
    }

    // ── Org scope ─────────────────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OrgScope_HitsOrgEndpoint()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/orgs/myorg/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/orgs/myorg/actions/secrets/SECRET",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Org,
            new[] { (CiSecretTarget)new CiSecretTarget.Org("myorg") },
            "SECRET", new RedactedSecret("v"));

        results[0].Success.Should().BeTrue();
    }

    // ── User scope (Gitea-specific) ───────────────────────────────────

    [Test]
    public async Task ProvisionSecret_UserScope_HitsUserEndpoint()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/user/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/user/actions/secrets/USER_SECRET",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.User,
            new[] { (CiSecretTarget)new CiSecretTarget.User("alice") },
            "USER_SECRET", new RedactedSecret("v"));

        results[0].Success.Should().BeTrue();
    }

    // ── Environment scope = unsupported ───────────────────────────────

    [Test]
    public async Task ProvisionSecret_EnvironmentScope_ReturnsNotSupported()
    {
        var (http, handler, _, _) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Environment,
            new[] { (CiSecretTarget)new CiSecretTarget.Environment("o", "r", "prod") },
            "SECRET", new RedactedSecret("v"));

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Be("scope_not_supported_on_platform");
        handler.Requests.Should().BeEmpty(
            "scope-not-supported short-circuits before any network call");
    }

    // ── Per-target isolation ──────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OneFails_OtherSucceeds()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/repos/o/a/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/repos/o/a/actions/secrets/T",
            HttpStatusCode.NoContent);

        // Second target: 403 on key fetch.
        handler.EnqueueStatus("GET", "/api/v1/repos/o/b/actions/secrets/public-key",
            HttpStatusCode.Forbidden);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new CiSecretTarget[]
            {
                new CiSecretTarget.Repo("o", "a"),
                new CiSecretTarget.Repo("o", "b"),
            },
            "T", new RedactedSecret("v"));

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Error.Should().Be("permission_denied");
    }

    // ── Forgejo wrapper ───────────────────────────────────────────────

    [Test]
    public async Task ForgejoProvisioner_DelegatesToGitea_StampsForgejoKind()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new ForgejoCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/repos/o/r/actions/secrets/T",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T", new RedactedSecret("v"));

        results[0].Success.Should().BeTrue();
        results[0].Kind.Should().Be(PlatformKind.Forgejo,
            "Forgejo wrapper stamps its own kind on results");
        prov.Kind.Should().Be(PlatformKind.Forgejo);
    }

    // ── No plaintext on the wire ──────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_PlaintextNeverInRequestBody()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        handler.EnqueueJson("GET", "/api/v1/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/api/v1/repos/o/r/actions/secrets/T",
            HttpStatusCode.NoContent);

        const string secret = "the_quick_brown_fox_secret_42";
        await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T", new RedactedSecret(secret));

        var put = handler.Requests.Single(r => r.Method == "PUT");
        put.Body.Should().NotContain(secret);
    }

    // ── Construction guards ───────────────────────────────────────────

    [Test]
    public void Constructor_RejectsBadKind()
    {
        var (http, _, _, _) = BuildClient();
        Action act = () => new GiteaCiSecretsProvisioner(http,
            "https://gitea.example.com", TestAuth, kind: PlatformKind.GitHub);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Gitea*Forgejo*");
    }

    // ── ListSecrets returns ServiceUnavailable ────────────────────────

    [Test]
    public async Task ListSecrets_ReturnsServiceUnavailable()
    {
        var (http, _, _, _) = BuildClient();
        var prov = new GiteaCiSecretsProvisioner(http, "https://gitea.example.com", TestAuth);

        var result = await prov.ListSecretsAsync(
            CiSecretScope.Repo, new CiSecretTarget.Repo("o", "r"));

        result.Should().BeOfType<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.ServiceUnavailable>(
            "Gitea ≤ 1.25 has no consistent list-secrets endpoint");
    }
}
