using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 28-12 follow-up — pin the JSON contract of the
/// <c>GET /api/admin/kek/rotate/status</c> endpoint so a future refactor
/// cannot silently regress the <c>phase</c> field to an integer. Operators
/// and the runbook UI both key off the string form; a JSON integer would
/// break every poll site without any compiler / reference warning.
///
/// <para>The endpoint
/// (<see cref="Tamma.Api.Endpoints.KekRotationEndpoints.GetStatus"/>)
/// projects the <c>KekRotationPhase</c> enum as
/// <c>status.Phase.ToString().ToLowerInvariant()</c> so the wire shape is
/// always a lowercase string. These tests verify that contract without
/// depending on any specific enum arithmetic.</para>
///
/// <para>If a future change replaces the manual projection with default
/// <c>System.Text.Json</c> enum serialization, this suite fails until
/// <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> (or the
/// equivalent <c>ConfigureHttpJsonOptions</c> default converter) is added
/// — catching the regression at test time rather than at runbook time.</para>
/// </summary>
[TestFixture]
public class KekRotationStatusSerializationTests
{
    [Test]
    public async Task GetStatus_SerializesPhaseAsJsonString_NotInteger()
    {
        using var client = ApiTestFixture.CreateClient();

        var response = await client.GetAsync("/api/admin/kek/rotate/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "permissive-dev auth lets the OwnerAccess policy pass in tests");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var phaseElement = doc.RootElement.GetProperty("phase");

        phaseElement.ValueKind.Should().Be(JsonValueKind.String,
            "runbook + UI key off the string form; a JsonValueKind.Number "
            + "would silently break every poll site. If this fails, add "
            + "[JsonConverter(typeof(JsonStringEnumConverter))] on "
            + "KekRotationStatus.Phase or register the converter globally "
            + "via ConfigureHttpJsonOptions.");
    }

    [Test]
    public async Task GetStatus_DefaultPhase_IsIdleLowercase()
    {
        // With no rotation ever kicked off, the coordinator returns the
        // default snapshot (Phase=Idle). The endpoint projects it as the
        // lowercase string "idle". This is the exact wire shape the
        // runbook dashboard consumes — pin it against accidental casing
        // / whitespace drift.
        using var client = ApiTestFixture.CreateClient();

        var response = await client.GetAsync("/api/admin/kek/rotate/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var phase = doc.RootElement.GetProperty("phase").GetString();

        phase.Should().Be("idle");
    }

    [Test]
    public async Task GetStatus_BodyIsCamelCase_AndIncludesAllFields()
    {
        // Defence-in-depth for the operator-facing payload: the runbook
        // polls JSON keys by name, so camelCase drift (Phase vs phase,
        // TotalTenants vs totalTenants) would break it. Pin every field
        // the current projection emits.
        using var client = ApiTestFixture.CreateClient();

        var response = await client.GetAsync("/api/admin/kek/rotate/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.TryGetProperty("phase", out _).Should().BeTrue();
        root.TryGetProperty("fromVersion", out _).Should().BeTrue();
        root.TryGetProperty("toVersion", out _).Should().BeTrue();
        root.TryGetProperty("totalTenants", out _).Should().BeTrue();
        root.TryGetProperty("reencryptedTenants", out _).Should().BeTrue();
        root.TryGetProperty("failedTenants", out _).Should().BeTrue();
        // startedAt / completedAt / failureReason are nullable — they may
        // appear as JsonValueKind.Null at idle; just assert presence.
        root.TryGetProperty("startedAt", out _).Should().BeTrue();
        root.TryGetProperty("completedAt", out _).Should().BeTrue();
        root.TryGetProperty("failureReason", out _).Should().BeTrue();
    }
}
