using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TDD;

namespace Tamma.Activities.Tests.TDD;

/// <summary>
/// E2E finding 28 — <see cref="CommitChangesActivity"/> must never report a commit
/// that did not happen.
///
/// <para>Two lies composed on the fallback TDD leg. (a) A store-rehydrated activity
/// has a null <c>_configuration</c>, so <c>Engine:CallbackUrl</c> read as absent and
/// the code silently took a <c>SimulateCommit</c> branch that minted a SHA from
/// <c>Guid.NewGuid()</c> and emitted <c>COMMIT.CREATED.SUCCESS</c> for it — a
/// fabricated commit id on the audit stream of a platform whose product claim is a
/// truthful audit trail (run 38: two "successful commits", <c>head == base</c>, an
/// empty PR). (b) The "real" path took HTTP 200 as proof and copied whatever
/// <c>commitSha</c> held, including the empty string when the property was absent —
/// which is precisely the shape of the LLM-proxy response that endpoint returns.</para>
///
/// <para>Elsa's <c>ActivityExecutionContext</c> is not cheaply constructible, so
/// per the convention in <c>CheckBudgetActivityEmissionTests</c> these pin the
/// extracted pure rules plus the structural fact that the fabricating branch is
/// gone from the type.</para>
/// </summary>
[TestFixture]
public class CommitHonestyTests
{
    // ── The fabricating branch is gone, structurally ────────────────────────

    [Test]
    public void CommitChangesActivity_hasNoSimulationBranch()
    {
        typeof(CommitChangesActivity)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(m => m.Name)
            .Should().NotContain(n => n.Contains("Simulate", StringComparison.OrdinalIgnoreCase),
                "a simulated commit emitted COMMIT.CREATED.SUCCESS with a fabricated SHA");
    }

    [Test]
    public void EveryNonCommitPath_hasAStableErrorCode()
    {
        var codes = new[]
        {
            CommitChangesActivity.ErrorNoFiles,
            CommitChangesActivity.ErrorNoSeam,
            CommitChangesActivity.ErrorNoSha,
            CommitChangesActivity.ErrorBridgeFailed,
        };

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => c.StartsWith("TDD.COMMIT.", StringComparison.Ordinal));
    }

    // ── What counts as proof a commit happened ──────────────────────────────

    [Test]
    public void TryReadCommitSha_rejects_theLlmProxyAnswer()
    {
        // The literal response shape of POST /api/engine/execute-task, which is what
        // the "real" commit path actually talks to. HTTP 200, success=true, and no
        // commit anywhere in it.
        var proxyAnswer = Parse(
            """{"success":true,"output":"I have committed the changes.","tokensUsed":42,"costUsd":0.01}""");

        CommitChangesActivity.TryReadCommitSha(proxyAnswer, out var sha).Should().BeFalse();
        sha.Should().BeNull();
    }

    [Test]
    public void TryReadCommitSha_rejects_anEmptyOrMissingSha()
    {
        CommitChangesActivity.TryReadCommitSha(Parse("""{"commitSha":""}"""), out _)
            .Should().BeFalse("the old code copied \"\" straight into CommitResult.CommitSha");
        CommitChangesActivity.TryReadCommitSha(Parse("""{"commitSha":null}"""), out _)
            .Should().BeFalse();
        CommitChangesActivity.TryReadCommitSha(Parse("""{}"""), out _).Should().BeFalse();
    }

    [Test]
    public void TryReadCommitSha_rejects_aReportedFailure_evenWithASha()
    {
        CommitChangesActivity.TryReadCommitSha(
            Parse("""{"success":false,"commitSha":"a1b2c3d4e5f6"}"""), out _)
            .Should().BeFalse();
    }

    [Test]
    public void TryReadCommitSha_accepts_aRealCommitId()
    {
        CommitChangesActivity.TryReadCommitSha(
            Parse("""{"success":true,"commitSha":"9f2a1c4b7e08d5361a0b2c3d4e5f60718293a4b5"}"""),
            out var sha).Should().BeTrue();
        sha.Should().Be("9f2a1c4b7e08d5361a0b2c3d4e5f60718293a4b5");

        // Short form (abbreviated id) is a commit id too.
        CommitChangesActivity.TryReadCommitSha(Parse("""{"commitSha":"9f2a1c4"}"""), out _)
            .Should().BeTrue();
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("abc", false)]                       // too short
    [TestCase("committed", false)]                 // prose, not hex
    [TestCase("zzzzzzz", false)]                   // right length, not hex
    [TestCase("a1b2c3d", true)]
    [TestCase("A1B2C3D4E5F6", true)]
    public void IsCommitSha_acceptsOnlyGitObjectIds(string? candidate, bool expected)
        => CommitChangesActivity.IsCommitSha(candidate).Should().Be(expected);

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
