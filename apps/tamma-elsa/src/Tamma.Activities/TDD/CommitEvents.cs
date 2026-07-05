using Tamma.Activities.Core;

namespace Tamma.Activities.TDD;

/// <summary>
/// Story 4-5 (AC2 — <c>CommitCreatedEvent</c>) — central catalogue of the
/// <c>COMMIT.*</c> DCB event types emitted by <see cref="CommitChangesActivity"/>,
/// the atomic TDD commit step (tests + implementation) at the tail of the
/// red-green-refactor cycle. Every commit the autonomous loop creates is therefore
/// observable on the DCB audit stream (<c>domain_events</c>) with its SHA, message,
/// branch, and file count — satisfying the "every code/git op observable" audit
/// rule.
///
/// <para>Emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern the branch / PR / merge ADL activities use. No activity holds a DB /
/// repository dependency of its own. The drain resolves the tenant from the
/// workflow's <c>TenantId</c> variable (single-user run → platform-scope, TenantId
/// null — never a throw).</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention. The FAILED type ALWAYS fires on the failure edge (no files to
/// commit, engine-callback failure, exception) as a loud (error-status) row so a
/// non-commit is never recorded as a silent false success.</para>
///
/// <list type="bullet">
///   <item><description><c>COMMIT.CREATED.SUCCESS</c> — an atomic commit landed
///     (carries <c>sha</c>/<c>message</c>/<c>branch</c>/<c>fileCount</c>).</description></item>
///   <item><description><c>COMMIT.CREATED.FAILED</c> — the commit did not happen
///     (empty change set / callback failure / exception). Loud, error-status.</description></item>
/// </list>
/// </summary>
public static class CommitEvents
{
    public const string CreatedSuccess = "COMMIT.CREATED.SUCCESS";
    public const string CreatedFailed = "COMMIT.CREATED.FAILED";

    /// <summary>True for the loud (error-status) FAILED type.</summary>
    public static bool IsFailureType(string type) => type == CreatedFailed;

    /// <summary>
    /// Build a <c>COMMIT.CREATED.*</c> event. Tags carry the queryable DCB index
    /// keys (<c>storyId</c>/<c>sessionId</c>/<c>branch</c>/<c>repository</c>, plus
    /// <c>sha</c> on success); <c>Data</c> carries the commit payload
    /// (<c>sha</c>/<c>message</c>/<c>branch</c>/<c>fileCount</c>/<c>files</c>) or the
    /// failure <c>reason</c>. Pure (no Elsa context) — exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildCreated(
        bool success,
        string? storyId,
        Guid sessionId,
        string? sha,
        string? message,
        string? branch,
        string? repository,
        IReadOnlyList<string>? files,
        string? error)
    {
        var fileList = (files ?? Array.Empty<string>()).ToList();

        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (sessionId != Guid.Empty) tags["sessionId"] = sessionId.ToString("D");
        if (!string.IsNullOrWhiteSpace(branch)) tags["branch"] = branch;
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (success && !string.IsNullOrWhiteSpace(sha)) tags["sha"] = sha;

        var data = new Dictionary<string, object?>
        {
            ["fileCount"] = fileList.Count,
            ["files"] = fileList,
        };
        if (!string.IsNullOrWhiteSpace(message)) data["message"] = message;
        if (!string.IsNullOrWhiteSpace(branch)) data["branch"] = branch;
        if (success && !string.IsNullOrWhiteSpace(sha)) data["sha"] = sha;
        if (!success && !string.IsNullOrWhiteSpace(error)) data["reason"] = error;

        return new TammaEvent
        {
            EventType = success ? CreatedSuccess : CreatedFailed,
            Status = success ? "success" : "error",
            Error = success ? null : error,
            Tags = tags,
            Data = data,
        };
    }
}
