using System.Net;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-8 — <see cref="GitLabCiSecretsProvisioner"/> tests.
/// GitLab's wire format is plaintext-over-HTTPS (the platform encrypts
/// at rest); we cover masked-value validation, protected-flag
/// roundtrip, environment-scope wiring, and capability gating for
/// User/Global scopes.
/// </summary>
[TestFixture]
public sealed class GitLabCiSecretsProvisionerTests
{
    private static Task<bool> TestAuth(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", "test-token");
        return Task.FromResult(true);
    }

    private static (HttpClient http, CiSecretsProvisionerTestHandler handler) BuildClient()
    {
        var handler = new CiSecretsProvisionerTestHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gitlab.example.com"),
        };
        return (http, handler);
    }

    // ── Repo-scope happy path ─────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_RepoScope_SendsExpectedPayload()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("POST", "/api/v4/projects/acme%2Fapp/variables",
            HttpStatusCode.Created);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("acme", "app") },
            "DATABASE_URL",
            new RedactedSecret("postgres://host/db"));

        results[0].Success.Should().BeTrue();

        // Verify the POST payload shape.
        var post = handler.Requests.Single();
        using var doc = JsonDocument.Parse(post.Body!);
        doc.RootElement.GetProperty("key").GetString().Should().Be("DATABASE_URL");
        doc.RootElement.GetProperty("value").GetString().Should().Be("postgres://host/db");
        doc.RootElement.GetProperty("protected").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("masked").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("variable_type").GetString().Should().Be("env_var");
    }

    // ── Protected + masked flags round-trip ───────────────────────────

    [Test]
    public async Task ProvisionSecret_ProtectedAndMasked_FlagsAppearInPayload()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("POST", "/api/v4/projects/acme%2Fapp/variables",
            HttpStatusCode.Created);

        // Use a masked-rules-compliant value (>=8 chars, allowed charset).
        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("acme", "app") },
            "API_KEY",
            new RedactedSecret("ABCD1234EFGH"),
            new CiSecretMetadata(Protected: true, Masked: true));

        results[0].Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(handler.Requests.Single().Body!);
        doc.RootElement.GetProperty("protected").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("masked").GetBoolean().Should().BeTrue();
    }

    // ── Environment scope wires environment_scope ─────────────────────

    [Test]
    public async Task ProvisionSecret_EnvironmentScope_PopulatesEnvironmentScope()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("POST", "/api/v4/projects/acme%2Fapp/variables",
            HttpStatusCode.Created);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Environment,
            new[] { (CiSecretTarget)new CiSecretTarget.Environment("acme", "app", "production") },
            "DATABASE_URL",
            new RedactedSecret("conn"));

        results[0].Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(handler.Requests.Single().Body!);
        doc.RootElement.GetProperty("environment_scope").GetString()
            .Should().Be("production");
    }

    // ── Org scope hits group endpoint ─────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OrgScope_HitsGroupEndpoint()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("POST", "/api/v4/groups/myteam/variables",
            HttpStatusCode.Created);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Org,
            new[] { (CiSecretTarget)new CiSecretTarget.Org("myteam") },
            "K", new RedactedSecret("v"));

        results[0].Success.Should().BeTrue();
    }

    // ── User + Global = unsupported ───────────────────────────────────

    [Test]
    public async Task ProvisionSecret_UserScope_ReturnsNotSupported()
    {
        var (http, _) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.User,
            new[] { (CiSecretTarget)new CiSecretTarget.User("alice") },
            "K", new RedactedSecret("v"));

        results[0].Error.Should().Be("scope_not_supported_on_platform");
    }

    [Test]
    public async Task ProvisionSecret_GlobalScope_ReturnsNotSupported()
    {
        var (http, _) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Global,
            new[] { (CiSecretTarget)new CiSecretTarget.Global() },
            "K", new RedactedSecret("v"));

        results[0].Error.Should().Be("scope_not_supported_on_platform");
    }

    // ── Masked-value pre-validation ───────────────────────────────────

    [Test]
    public async Task ProvisionSecret_MaskedShortValue_FailsBeforeNetwork()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("a", "b") },
            "K",
            new RedactedSecret("short"),  // < 8 chars
            new CiSecretMetadata(Masked: true));

        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Be("masked_value_invalid:length");
        handler.Requests.Should().BeEmpty(
            "masked-value validation must short-circuit BEFORE the network call");
    }

    [Test]
    public async Task ProvisionSecret_MaskedNewlineValue_FailsValidation()
    {
        var (http, _) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("a", "b") },
            "K",
            new RedactedSecret("ABCD\nEFGH"),
            new CiSecretMetadata(Masked: true));

        results[0].Error.Should().Be("masked_value_invalid:newlines");
    }

    [Test]
    public async Task ProvisionSecret_MaskedDisallowedChars_FailsValidation()
    {
        var (http, _) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("a", "b") },
            "K",
            new RedactedSecret("ABCD$$$$"),  // $ not in allowed charset
            new CiSecretMetadata(Masked: true));

        results[0].Error.Should().Be("masked_value_invalid:charset");
    }

    // ── MaskedVariableValidator unit tests ────────────────────────────

    [Test]
    public void MaskedVariableValidator_TooShort_ReturnsLength()
    {
        MaskedVariableValidator.Validate("abc").Should().Be("masked_value_invalid:length");
        MaskedVariableValidator.Validate("").Should().Be("masked_value_invalid:length");
    }

    [Test]
    public void MaskedVariableValidator_Newline_ReturnsNewlines()
    {
        MaskedVariableValidator.Validate("abc\ndef12345")
            .Should().Be("masked_value_invalid:newlines");
        MaskedVariableValidator.Validate("abc\rdef12345")
            .Should().Be("masked_value_invalid:newlines");
    }

    [Test]
    public void MaskedVariableValidator_DisallowedChars_ReturnsCharset()
    {
        MaskedVariableValidator.Validate("ABCDEFGH$$$").Should().Be("masked_value_invalid:charset");
        MaskedVariableValidator.Validate("ABCDEFGH ").Should().Be("masked_value_invalid:charset");
    }

    [Test]
    public void MaskedVariableValidator_AllowedCharset_ReturnsNull()
    {
        MaskedVariableValidator.Validate("ABCDefgh").Should().BeNull();
        MaskedVariableValidator.Validate("base64+/=cdef").Should().BeNull();
        MaskedVariableValidator.Validate("alpha-beta_gamma.delta").Should().BeNull();
    }

    // ── Idempotent delete ─────────────────────────────────────────────

    [Test]
    public async Task DeleteSecret_404_TreatedAsSuccess()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("DELETE", "/api/v4/projects/o%2Fr/variables/K",
            HttpStatusCode.NotFound);

        var results = await prov.DeleteSecretAsync(
            CiSecretScope.Repo,
            new[] { (CiSecretTarget)new CiSecretTarget.Repo("o", "r") },
            "K");

        results[0].Success.Should().BeTrue();
    }

    // ── Per-target isolation ──────────────────────────────────────────

    [Test]
    public async Task ProvisionSecret_OneTargetFails_OtherSucceeds()
    {
        var (http, handler) = BuildClient();
        var prov = new GitLabCiSecretsProvisioner(http, "https://gitlab.example.com", TestAuth);

        handler.EnqueueStatus("POST", "/api/v4/projects/o%2Fa/variables",
            HttpStatusCode.Created);
        handler.EnqueueStatus("POST", "/api/v4/projects/o%2Fb/variables",
            HttpStatusCode.InternalServerError);

        var results = await prov.ProvisionSecretAsync(
            CiSecretScope.Repo,
            new CiSecretTarget[]
            {
                new CiSecretTarget.Repo("o", "a"),
                new CiSecretTarget.Repo("o", "b"),
            },
            "K", new RedactedSecret("v"));

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeFalse();
    }
}
