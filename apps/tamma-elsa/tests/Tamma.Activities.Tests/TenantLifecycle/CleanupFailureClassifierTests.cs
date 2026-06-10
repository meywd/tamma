using System.Net.Sockets;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Re-port of <c>CleanUpFailedTenantClassifierTests</c> (deleted during
/// the H6 decomposition merge). The classifier maps
/// <c>(stepName, exception)</c> to a stable failure code +
/// length-bounded redacted snippet that's safe to land in long-lived
/// storage. These tests lock in:
///
/// <list type="bullet">
///   <item>Step name + exception type → stable
///     <c>FailureCode</c>.</item>
///   <item>Raw <see cref="System.Exception.Message"/> is wrapped through
///     <see cref="IErrorRedactor"/> and never leaks bearer tokens or
///     internal URLs into the snippet.</item>
///   <item>Snippet is bounded (200 chars) so the long-lived event
///     store can't grow on a chatty exception.</item>
///   <item>Null redactor produces a code but leaves the message
///     unredacted (test-only fallback path).</item>
/// </list>
/// </summary>
[TestFixture]
public class CleanupFailureClassifierTests
{
    private IErrorRedactor _redactor = null!;

    [SetUp]
    public void Setup() => _redactor = new ErrorRedactor();

    // ── Step + exception-type → failure code ─────────────────────────

    [Test]
    public void Classify_DropSchema_SqlError_YieldsDropSchemaFailedCode()
    {
        var ex = new InvalidOperationException("relation does not exist");
        var (code, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        code.Should().Be("drop_schema_failed");
        snippet.Should().Contain("relation");
    }

    [Test]
    public void Classify_DropRole_SqlError_YieldsDropRoleFailedCode()
    {
        var ex = new InvalidOperationException("role still owns objects");
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropRole, ex, _redactor);

        code.Should().Be("drop_role_failed");
    }

    [Test]
    public void Classify_TimeoutOnAnyStep_YieldsNetworkErrorCode()
    {
        // Network-shape detection beats step-specific defaults — a
        // timeout on drop-tenant-schema is "network_error", not
        // "drop_schema_failed", because the operator response is
        // different (retry vs. inspect).
        var ex = new TimeoutException("timed out after 30s");

        CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor).FailureCode
            .Should().Be("network_error");
        CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropRole, ex, _redactor).FailureCode
            .Should().Be("network_error");
        CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.EvictPool, ex, _redactor).FailureCode
            .Should().Be("network_error");
    }

    [Test]
    public void Classify_PermissionDeniedOnDropSchema_YieldsPermissionDeniedCode()
    {
        var ex = new InvalidOperationException("permission denied for database");
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        code.Should().Be("permission_denied");
    }

    [Test]
    public void Classify_PermissionDeniedOnDropRole_YieldsPermissionDeniedCode()
    {
        var ex = new InvalidOperationException("must be owner of role x");
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropRole, ex, _redactor);

        code.Should().Be("permission_denied");
    }

    [Test]
    public void Classify_EvictPool_SqlError_YieldsEvictPoolFailedCode()
    {
        var ex = new InvalidOperationException("data source disposed");
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.EvictPool, ex, _redactor);

        code.Should().Be("evict_pool_failed");
    }

    [Test]
    public void Classify_SocketException_OnDropSchema_YieldsNetworkErrorCode()
    {
        // SocketException is the canonical transport-level failure
        // surface. Must classify as network_error regardless of step.
        var ex = new SocketException();
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        code.Should().Be("network_error");
    }

    [Test]
    public void Classify_OperationCanceled_OnAnyStep_YieldsCancelledCode()
    {
        var ex = new OperationCanceledException("workflow cancelled by host");

        CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor).FailureCode
            .Should().Be("cancelled");
        CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.SoftDeleteRow, ex, _redactor).FailureCode
            .Should().Be("cancelled");
    }

    [Test]
    public void Classify_UnknownStep_YieldsStepFailedFallback()
    {
        // Future steps that haven't been added to the classifier's
        // switch arms must still produce a sensible code.
        var ex = new InvalidOperationException("anything");
        var (code, _) = CleanupFailureClassifier.ClassifyFailure(
            "future-unknown-step", ex, _redactor);

        code.Should().Be("step_failed");
    }

    // ── Snippet redaction + bounds ───────────────────────────────────

    [Test]
    public void Classify_RedactedSnippet_BoundedTo200Chars()
    {
        // Long error messages must not bloat ProvisioningDetail / event
        // payloads. 200 chars is enough for an operator triage signal;
        // full text lives in ILogger.
        var longMessage = new string('x', 4000);
        var ex = new InvalidOperationException(longMessage);
        var (_, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        snippet.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Test]
    public void Classify_RedactedSnippet_DoesNotContainBearerTokens()
    {
        // The redactor is the gate that keeps secrets out of the
        // long-lived event store. A bearer token in the raw message
        // must surface as [REDACTED] in the snippet.
        var ex = new InvalidOperationException(
            "auth failed: Authorization: Bearer sk-secret-credential-abcdef");
        var (_, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        snippet.Should().NotContain("sk-secret-credential-abcdef");
        snippet.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Classify_RedactedSnippet_ScrubsInternalUrl()
    {
        var ex = new InvalidOperationException(
            "could not connect to http://10.0.0.42:5432/internal");
        var (_, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        snippet.Should().NotContain("10.0.0.42");
    }

    [Test]
    public void Classify_RedactedSnippet_ScrubsAnthropicKey()
    {
        // The redactor scrubs Anthropic keys (sk-ant-...) before the
        // bearer-token rule fires. Verifies the classifier wires
        // through to the redactor's full ruleset, not just the bearer
        // rule.
        var ex = new InvalidOperationException(
            "auth failed: sk-ant-credential-payload-here");
        var (_, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, _redactor);

        snippet.Should().NotContain("credential-payload-here");
        snippet.Should().Contain("[REDACTED]");
    }

    [Test]
    public void Classify_NullRedactor_StillProducesCode_ButLeavesMessageUnredacted()
    {
        // Defensive — when the redaction infrastructure isn't wired
        // (test-only path), the classifier still produces a code so
        // dashboards keep working, and the snippet falls through.
        // Production wires the redactor; tests exercise this path
        // here.
        var ex = new InvalidOperationException("hello");
        var (code, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, redactor: null);

        code.Should().Be("drop_schema_failed");
        snippet.Should().Be("hello");
    }

    [Test]
    public void Classify_NullRedactor_StillBoundsSnippetTo200Chars()
    {
        // The bound is independent of redaction — even with null
        // redactor, the snippet must not exceed MaxSnippetChars (the
        // store size guard wins regardless).
        var longMessage = new string('y', 4000);
        var ex = new InvalidOperationException(longMessage);
        var (_, snippet) = CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex, redactor: null);

        snippet.Length.Should().BeLessThanOrEqualTo(200);
    }

    // ── Argument validation ──────────────────────────────────────────

    [Test]
    public void Classify_NullException_Throws()
    {
        var act = () => CleanupFailureClassifier.ClassifyFailure(
            CleanupSteps.DropSchema, ex: null!, _redactor);

        act.Should().Throw<ArgumentNullException>();
    }
}
