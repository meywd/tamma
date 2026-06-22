using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Activities.ADL;

/// <summary>
/// Story 2.8 (AC6) / FR-20 — central catalogue of <c>PR.*</c> DCB event types
/// and a builder that materialises a <see cref="DomainEvent"/> ready for
/// <c>IEventRepository.AppendAsync</c>.
///
/// <para>The autonomous loop's pull-request step is issue-scoped, so PR events
/// live in <c>domain_events</c> (which carries a first-class
/// <see cref="DomainEvent.IssueNumber"/> column) rather than the control-plane
/// <c>platform_events</c> stream used for tenant-lifecycle. Modelled on
/// <c>TenantLifecycleEvents.BuildEvent</c>.</para>
///
/// <list type="bullet">
///   <item><description><c>PR.CREATED.SUCCESS</c> — a PR was opened (or an
///     existing open PR for the same head→base was reused / updated).</description></item>
///   <item><description><c>PR.CREATED.FAILED</c> — PR creation failed
///     (permission, conflict, rate-limit, API error). Always emitted on the
///     failure edge so the loop never reports a silent false success.</description></item>
///   <item><description><c>PR.MARKED_READY.SUCCESS</c> — reserved for the
///     parent-driven draft→ready flip (follow-on, not yet wired).</description></item>
/// </list>
/// </summary>
public static class PrEvents
{
    public const string CreatedSuccess = "PR.CREATED.SUCCESS";
    public const string CreatedFailed = "PR.CREATED.FAILED";
    public const string MarkedReadySuccess = "PR.MARKED_READY.SUCCESS";

    /// <summary>
    /// Build a <see cref="DomainEvent"/> for a <c>PR.*</c> transition. Tags carry
    /// <c>issueId</c>, <c>issueNumber</c>, <c>repository</c>, <c>prNumber</c> and
    /// (when present) <c>tenantId</c>; <paramref name="data"/> carries the
    /// metrics payload (url, base/head, filesChanged, lines, coverage, reviewers,
    /// labels, isDraft, durationMs). <see cref="DomainEvent.IssueNumber"/> is set
    /// to <paramref name="issueNumber"/> so the issue stream filters work.
    /// </summary>
    public static DomainEvent BuildEvent(
        string type,
        int issueNumber,
        string repository,
        int? prNumber = null,
        Guid? tenantId = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("type must be supplied", nameof(type));

        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
            ["repository"] = repository,
        };
        if (prNumber is not null) tags["prNumber"] = prNumber.Value.ToString();
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            IssueNumber = issueNumber,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the
    /// workflow inputs. Returns <c>null</c> for empty / single-user / unparseable
    /// values (PR events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
