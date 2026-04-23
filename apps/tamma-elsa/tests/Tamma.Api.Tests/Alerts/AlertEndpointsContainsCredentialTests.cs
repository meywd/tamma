using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Endpoints;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 1.5-37 (Wave C.1) — unit tests for the secret-isolation
/// guard in <see cref="AlertEndpoints.ContainsPlaintextCredential"/>.
/// This is the choke point that enforces "credentials never touch
/// <c>alert_channels.config</c>" — we verify it catches the common
/// field names case-insensitively and does not false-positive on
/// benign fields.
/// </summary>
[TestFixture]
public class AlertEndpointsContainsCredentialTests
{
    [TestCase("""{"webhookUrl":"https://..."}""")]
    [TestCase("""{"webhook_url":"..."}""")]
    [TestCase("""{"routingKey":"..."}""")]
    [TestCase("""{"routing_key":"..."}""")]
    [TestCase("""{"password":"..."}""")]
    [TestCase("""{"apiKey":"..."}""")]
    [TestCase("""{"api_key":"..."}""")]
    [TestCase("""{"secret":"..."}""")]
    [TestCase("""{"sharedSecret":"..."}""")]
    [TestCase("""{"shared_secret":"..."}""")]
    [TestCase("""{"token":"..."}""")]
    [TestCase("""{"authToken":"..."}""")]
    [TestCase("""{"auth_token":"..."}""")]
    [TestCase("""{"WEBHOOKURL":"..."}""")]
    [TestCase("""{"subjectPrefix":"[x]","password":"p"}""")]
    public void ContainsPlaintextCredential_ReservedFieldName_ReturnsTrue(string json)
    {
        AlertEndpoints.ContainsPlaintextCredential(json)
            .Should().BeTrue();
    }

    [TestCase("{}")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("""{"toAddress":"ops@acme.io"}""")]
    [TestCase("""{"subjectPrefix":"[Tamma Alert] "}""")]
    [TestCase("""{"url":"https://hooks.example.com/alert","severityFilter":["critical"]}""")]
    [TestCase("""{"severityFilter":["critical","warning"]}""")]
    public void ContainsPlaintextCredential_BenignConfig_ReturnsFalse(string? json)
    {
        AlertEndpoints.ContainsPlaintextCredential(json)
            .Should().BeFalse();
    }

    [Test]
    public void ContainsPlaintextCredential_NonObjectJson_ReturnsFalse()
    {
        // Arrays, strings, numbers etc. aren't objects — no fields to
        // inspect; not a credential leak per this contract.
        AlertEndpoints.ContainsPlaintextCredential("[]")
            .Should().BeFalse();
        AlertEndpoints.ContainsPlaintextCredential("42")
            .Should().BeFalse();
    }
}
