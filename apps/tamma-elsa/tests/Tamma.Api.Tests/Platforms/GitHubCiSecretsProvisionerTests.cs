using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Sodium;
using Tamma.Platforms.GitHub;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-8 — <see cref="GitHubCiSecretsProvisioner"/> tests.
/// Covers the libsodium round-trip + per-target error isolation +
/// capability gating + log-sanitization.
/// </summary>
[TestFixture]
public sealed class GitHubCiSecretsProvisionerTests
{
    // Epic 31 P4 M4 — the provisioner moved into the driver project and now
    // takes (baseUrl, authorize) from the factory; tests authorize with a
    // static bearer.
    private static Task<bool> TestAuth(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
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
            BaseAddress = new Uri("https://api.github.com"),
        };
        return (http, handler, keypair, publicKeyB64);
    }

    private static string PublicKeyJson(string keyB64) =>
        JsonSerializer.Serialize(new { key = keyB64, key_id = KeyId });

    // ── Libsodium round-trip ─────────────────────────────────────────

    [Test]
    public void EncryptSealedBox_RoundTripsWithKnownKeypair()
    {
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);
        const string plaintext = "tamma_super_secret_42";

        var ciphertextB64 = GitHubCiSecretsProvisioner
            .EncryptSealedBox(publicKeyB64, plaintext);

        var ciphertext = Convert.FromBase64String(ciphertextB64);
        var decrypted = SealedPublicKeyBox
            .Open(ciphertext, keypair.PrivateKey, keypair.PublicKey);
        var recovered = System.Text.Encoding.UTF8.GetString(decrypted);
        recovered.Should().Be(plaintext);
    }

    [Test]
    public void EncryptSealedBox_ProducesDifferentCiphertextEachCall()
    {
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);
        var a = GitHubCiSecretsProvisioner.EncryptSealedBox(publicKeyB64, "hello");
        var b = GitHubCiSecretsProvisioner.EncryptSealedBox(publicKeyB64, "hello");
        a.Should().NotBe(b);
    }

    // ── Repo-scope happy path ────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_RepoScope_HappyPath()
    {
        var (http, handler, keypair, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth, NullLogger<GitHubCiSecretsProvisioner>.Instance);

        handler.EnqueueJson("GET", "/repos/acme/app/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/repos/acme/app/actions/secrets/MY_TOKEN",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("acme", "app") },
            "MY_TOKEN",
            new RedactedSecret("plaintext_secret_value_xyz"));

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        results[0].Error.Should().BeNull();
        results[0].TargetDescriptor.Should().Be("repo:acme/app");

        // Verify the PUT body decrypts back to the plaintext.
        var putReq = handler.Requests
            .Single(r => r.Method == "PUT");
        using var doc = JsonDocument.Parse(putReq.Body!);
        var encryptedB64 = doc.RootElement.GetProperty("encrypted_value").GetString();
        var keyId = doc.RootElement.GetProperty("key_id").GetString();
        keyId.Should().Be(KeyId);

        var decrypted = SealedPublicKeyBox.Open(
            Convert.FromBase64String(encryptedB64!),
            keypair.PrivateKey, keypair.PublicKey);
        System.Text.Encoding.UTF8.GetString(decrypted).Should()
            .Be("plaintext_secret_value_xyz");
    }

    // ── Org-scope happy path ─────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OrgScope_HappyPath()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueJson("GET", "/orgs/acme/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/orgs/acme/actions/secrets/ORG_TOKEN",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Org,
            new[] { (CiSecretTarget)new CiSecretTarget.Org("acme") },
            "ORG_TOKEN",
            new RedactedSecret("orgsecret"));

        results[0].Success.Should().BeTrue();
        results[0].TargetDescriptor.Should().Be("org:acme");
    }

    // ── Environment-scope happy path ─────────────────────────────────

    [Test]
    public async Task ProvisionSecret_EnvironmentScope_HappyPath()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueJson("GET",
            "/repos/acme/app/environments/production/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT",
            "/repos/acme/app/environments/production/secrets/ENV_TOKEN",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Environment,
            new[] { (CiSecretTarget)new CiSecretTarget.Environment("acme", "app", "production") },
            "ENV_TOKEN",
            new RedactedSecret("envsecret"));

        results[0].Success.Should().BeTrue();
        results[0].TargetDescriptor.Should().Be("env:acme/app/production");
    }

    // ── Capability gating: User + Global rejected ────────────────────

    [Test]
    public async Task ProvisionSecret_UserScope_ReturnsNotSupported()
    {
        var (http, _, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.User,
            new[] { (CiSecretTarget)new CiSecretTarget.User("alice") },
            "USER_TOKEN",
            new RedactedSecret("v"));

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Be("scope_not_supported_on_platform");
    }

    [Test]
    public async Task ProvisionSecret_GlobalScope_ReturnsNotSupported()
    {
        var (http, _, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Global,
            new[] { (CiSecretTarget)new CiSecretTarget.Global() },
            "G",
            new RedactedSecret("v"));

        results[0].Error.Should().Be("scope_not_supported_on_platform");
    }

    // ── Per-target error isolation ───────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OneTargetFails_OtherTargetsSucceed()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        // First target: 200 + 204
        handler.EnqueueJson("GET", "/repos/acme/app1/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/repos/acme/app1/actions/secrets/T",
            HttpStatusCode.NoContent);

        // Second target: 500 on public-key fetch.
        handler.EnqueueStatus("GET", "/repos/acme/app2/actions/secrets/public-key",
            HttpStatusCode.InternalServerError);

        // Third target: 200 + 204
        handler.EnqueueJson("GET", "/repos/acme/app3/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/repos/acme/app3/actions/secrets/T",
            HttpStatusCode.NoContent);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new CiSecretTarget[]
            {
                new CiSecretTarget.Repo("acme", "app1"),
                new CiSecretTarget.Repo("acme", "app2"),
                new CiSecretTarget.Repo("acme", "app3"),
            },
            "T",
            new RedactedSecret("v"));

        results.Should().HaveCount(3);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeFalse();
        results[1].Error.Should().StartWith("unknown:http_500");
        results[2].Success.Should().BeTrue();
    }

    // ── Auth-expired mapping ─────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_401_MapsToAuthExpired()
    {
        var (http, handler, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueStatus("GET", "/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.Unauthorized);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T",
            new RedactedSecret("v"));

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Be("auth_expired");
    }

    [Test]
    public async Task ProvisionSecret_403_MapsToPermissionDenied()
    {
        var (http, handler, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueStatus("GET", "/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.Forbidden);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T",
            new RedactedSecret("v"));

        results[0].Error.Should().Be("permission_denied");
    }

    // ── Idempotent delete ────────────────────────────────────────────

    [Test]
    public async Task DeleteSecret_404_TreatedAsSuccess()
    {
        var (http, handler, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueStatus("DELETE", "/repos/o/r/actions/secrets/T",
            HttpStatusCode.NotFound);

        var results = await prov.DeleteSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T");

        results[0].Success.Should().BeTrue("delete is idempotent — 404 == already gone");
    }

    [Test]
    public async Task DeleteSecret_204_Success()
    {
        var (http, handler, _, _) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueStatus("DELETE", "/repos/o/r/actions/secrets/T",
            HttpStatusCode.NoContent);

        var results = await prov.DeleteSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T");
        results[0].Success.Should().BeTrue();
    }

    // ── Rotate signature parity ──────────────────────────────────────

    [Test]
    public async Task RotateSecret_UsesSameWireShapeAsProvision()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueJson("GET", "/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/repos/o/r/actions/secrets/T",
            HttpStatusCode.NoContent);

        var results = await prov.RotateSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T",
            new RedactedSecret("newvalue"));

        results[0].Success.Should().BeTrue();
        // GET + PUT — same as provision.
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be("GET");
        handler.Requests[1].Method.Should().Be("PUT");
    }

    // ── No plaintext in HTTP body ────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_PlaintextNeverAppearsInRequestBody()
    {
        var (http, handler, _, publicKeyB64) = BuildClient();
        var prov = new GitHubCiSecretsProvisioner(http, "https://api.github.com", TestAuth);

        handler.EnqueueJson("GET", "/repos/o/r/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64));
        handler.EnqueueStatus("PUT", "/repos/o/r/actions/secrets/T",
            HttpStatusCode.NoContent);

        const string secret = "the_quick_brown_fox_secret_42";
        await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "T",
            new RedactedSecret(secret));

        // PUT body MUST not contain the plaintext (sealed-box encryption
        // is the whole point — if this assertion fires we shipped
        // plaintext to GitHub, which is the security bug 31-8 was
        // designed to prevent).
        var putReq = handler.Requests.Single(r => r.Method == "PUT");
        putReq.Body.Should().NotContain(secret);
    }

    // ── Cross-tenant isolation: a provisioner only sees its own HttpClient ──

    [Test]
    public async Task CrossTenantIsolation_ProvisionerPerInstance()
    {
        // Two separate provisioners with separate HttpClients (representing
        // tenant A's and tenant B's drivers). A request issued through
        // provA's HttpClient never reaches provB's handler — that's the
        // cross-tenant safety property the resolver gives us.
        var (httpA, handlerA, _, publicKeyB64A) = BuildClient();
        var (httpB, handlerB, _, _) = BuildClient();

        var provA = new GitHubCiSecretsProvisioner(httpA, "https://api.github.com", TestAuth);
        var provB = new GitHubCiSecretsProvisioner(httpB, "https://api.github.com", TestAuth);

        handlerA.EnqueueJson("GET", "/repos/tenantA/app/actions/secrets/public-key",
            HttpStatusCode.OK, PublicKeyJson(publicKeyB64A));
        handlerA.EnqueueStatus("PUT", "/repos/tenantA/app/actions/secrets/T",
            HttpStatusCode.NoContent);

        await provA.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("tenantA", "app") },
            "T",
            new RedactedSecret("tenantA_secret"));

        // provB never saw a request — isolation property confirmed.
        handlerB.Requests.Should().BeEmpty();
        handlerA.Requests.Should().HaveCount(2);
    }
}
