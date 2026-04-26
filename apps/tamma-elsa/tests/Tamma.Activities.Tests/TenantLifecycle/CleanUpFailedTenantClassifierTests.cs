using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// M1 — unit tests for the cleanup-step exception classifier. Verifies
/// that:
/// <list type="bullet">
///   <item>Step name + exception type → stable
///     <see cref="CleanUpFailedTenantActivity.CleanupFailureRecord.FailureCode"/>.</item>
///   <item>Raw <see cref="System.Exception.Message"/> is wrapped through
///     <see cref="IErrorRedactor"/> and never leaks bearer tokens or
///     internal URLs into the snippet.</item>
///   <item>Snippet is bounded (200 chars) so the long-lived event store
///     can't grow on a chatty exception.</item>
/// </list>
/// </summary>
[TestFixture]
public class CleanUpFailedTenantClassifierTests
{
    private IErrorRedactor _redactor = null!;

    [SetUp]
    public void Setup() => _redactor = new ErrorRedactor();

    [Test]
    public void Classify_DropDatabase_SqlError_YieldsDropDatabaseFailedCode()
    {
        var ex = new InvalidOperationException("relation does not exist");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, _redactor);

        record.FailureCode.Should().Be("drop_database_failed");
        record.RedactedSnippet.Should().Contain("relation");
    }

    [Test]
    public void Classify_DropRole_SqlError_YieldsDropRoleFailedCode()
    {
        var ex = new InvalidOperationException("role still owns objects");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-role", ex, _redactor);

        record.FailureCode.Should().Be("drop_role_failed");
    }

    [Test]
    public void Classify_TimeoutOnDropDatabase_YieldsNetworkErrorCode()
    {
        var ex = new TimeoutException("timed out after 30s");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, _redactor);

        record.FailureCode.Should().Be("network_error");
    }

    [Test]
    public void Classify_PermissionDeniedOnDropRole_YieldsPermissionDeniedCode()
    {
        var ex = new InvalidOperationException("permission denied for role x");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-role", ex, _redactor);

        record.FailureCode.Should().Be("permission_denied");
    }

    [Test]
    public void Classify_EvictPool_AlwaysYieldsEvictPoolFailedCode()
    {
        var ex = new InvalidOperationException("anything");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "evict-pool", ex, _redactor);

        record.FailureCode.Should().Be("evict_pool_failed");
    }

    [Test]
    public void RedactedSnippet_ScrubsBearerToken()
    {
        // M1 — the redactor must scrub Bearer tokens before the snippet
        // lands in long-lived storage.
        var ex = new InvalidOperationException(
            "auth failed: Authorization: Bearer sk-secret-credential-abcdef");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, _redactor);

        record.RedactedSnippet.Should().NotContain("sk-secret-credential-abcdef");
        record.RedactedSnippet.Should().Contain("[REDACTED]");
    }

    [Test]
    public void RedactedSnippet_ScrubsInternalUrl()
    {
        var ex = new InvalidOperationException(
            "could not connect to http://10.0.0.42:5432/internal");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, _redactor);

        record.RedactedSnippet.Should().NotContain("10.0.0.42");
    }

    [Test]
    public void RedactedSnippet_BoundedTo200Chars()
    {
        // Long error messages must NOT bloat ProvisioningDetail / event
        // payloads. 200 chars is enough for an operator triage signal;
        // full text lives in ILogger.
        var longMessage = new string('x', 4000);
        var ex = new InvalidOperationException(longMessage);
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, _redactor);

        record.RedactedSnippet.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Test]
    public void Classify_NullRedactor_StillProducesCode_ButLeavesMessageUnredacted()
    {
        // Defensive — when redaction infrastructure isn't wired, the
        // classifier still produces a code (so dashboards keep working)
        // and the snippet falls through. Production wires the redactor;
        // tests exercise the null-fallback path here.
        var ex = new InvalidOperationException("hello");
        var record = CleanUpFailedTenantActivity.ClassifyFailure(
            "drop-tenant-db", ex, redactor: null);

        record.FailureCode.Should().Be("drop_database_failed");
        record.RedactedSnippet.Should().Be("hello");
    }
}
