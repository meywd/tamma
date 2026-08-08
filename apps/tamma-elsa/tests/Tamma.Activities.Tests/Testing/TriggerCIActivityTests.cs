using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Tests.Testing;

/// <summary>
/// Epic 31 P3 (seam 4) — contract pins for <see cref="TriggerCIActivity"/>.
///
/// <para>Written with the repoint off the raw
/// <c>POST {Engine:CallbackUrl}/api/engine/trigger-ci</c> HTTP call and onto the
/// governed CI plane (<c>TammaApiClient.TriggerTestsAsync</c> →
/// <c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>). The pins:</para>
/// <list type="bullet">
///   <item>the activity's RESULT contract (<see cref="CITriggerResult"/>:
///     Success / RunId / Error / TriggeredAt) is unchanged — downstream
///     <c>WaitForCIResultsActivity</c> keys its bookmark on <c>RunId</c>;</item>
///   <item>the real path no longer performs raw engine-callback HTTP — no
///     IHttpClientFactory dependency survives on the activity;</item>
///   <item>repository normalization: mediation takes <c>owner/repo</c>, while
///     workflows historically pass browser/clone URLs.</item>
/// </list>
/// </summary>
[TestFixture]
public class TriggerCIActivityTests
{
    // ================================================================
    // Result-contract pins
    // ================================================================

    [Test]
    public void CITriggerResult_DefaultsAreFailureSafe()
    {
        var result = new CITriggerResult();
        result.Success.Should().BeFalse(
            "a freshly-constructed trigger result must never read as a dispatched run");
    }

    [Test]
    public void Activity_KeepsTheCITriggerResultContract()
    {
        typeof(CITriggerResult).GetProperty("Success").Should().NotBeNull();
        typeof(CITriggerResult).GetProperty("RunId").Should().NotBeNull();
        typeof(CITriggerResult).GetProperty("PipelineUrl").Should().NotBeNull();
        typeof(CITriggerResult).GetProperty("Error").Should().NotBeNull();
        typeof(CITriggerResult).GetProperty("TriggeredAt").Should().NotBeNull();
    }

    // ================================================================
    // The repoint — no raw engine-callback HTTP path survives
    // ================================================================

    [Test]
    public void RealPath_DoesNotDependOnRawHttpClientFactory()
    {
        typeof(TriggerCIActivity)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType.FullName)
            .Should().NotContain(n => n!.Contains("IHttpClientFactory"),
                "the activity's real path is the governed CI mediation plane via TammaApiClient, "
                + "not a hand-rolled HTTP call to /api/engine/trigger-ci");
    }

    [Test]
    public void RealPath_GoesThroughTheMediationClient()
    {
        typeof(TriggerCIActivity)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType)
            .Should().Contain(t => t == typeof(Tamma.Activities.LlmCall.TammaApiClient),
                "CI dispatch is a governed external effect — it must ride the mediation client");
    }

    [Test]
    public void MockPath_IsStillAvailableForTesting()
    {
        typeof(TriggerCIActivity)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .Should().Contain("SimulateCITrigger", "Testing:UseMock keeps the mock path");
    }

    // ================================================================
    // Repository normalization (mediation takes owner/repo path segments)
    // ================================================================

    [TestCase("acme/widgets", "acme/widgets")]
    [TestCase("https://github.com/acme/widgets", "acme/widgets")]
    [TestCase("https://github.com/acme/widgets.git", "acme/widgets")]
    [TestCase("https://gitea.example.com/acme/widgets/", "acme/widgets")]
    [TestCase("git@github.com:acme/widgets.git", "acme/widgets")]
    [TestCase("widgets", "widgets")]
    public void NormalizeRepository_YieldsOwnerRepo(string input, string expected)
    {
        TriggerCIActivity.NormalizeRepository(input).Should().Be(expected);
    }

    [Test]
    public void NormalizeRepository_EmptyInput_YieldsEmpty()
    {
        TriggerCIActivity.NormalizeRepository("").Should().BeEmpty();
    }
}
