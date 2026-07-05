using Tamma.Activities.Core;

namespace Tamma.Activities.TDD;

/// <summary>
/// Story 4-5 (AC1 — <c>CodeFileWrittenEvent</c>) — central catalogue of the
/// <c>CODE.*</c> DCB event types emitted by the code-producing steps of the
/// red-green-refactor TDD cycle: <see cref="WriteTestsActivity"/> (RED, test
/// authoring), <see cref="WriteImplementationActivity"/> (GREEN, implementation),
/// and <see cref="ApplyRefactoringActivity"/> (REFACTOR). Every code change the
/// autonomous loop makes is therefore observable on the DCB audit stream
/// (<c>domain_events</c>), satisfying the "every code/git op observable" audit rule.
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/> uses. No
/// activity holds a DB / repository dependency of its own (a directly-injected
/// <c>IEventRepository</c> would be inert inside the Elsa engine and silently drop
/// every event). The drain resolves the tenant from the workflow's <c>TenantId</c>
/// variable, so a SaaS caller's code events carry the tenant column while a
/// single-user run is platform-scope (TenantId null) — never a throw.</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention (<c>CLAUDE.md</c>: <c>CODE.GENERATED.SUCCESS</c>). The FAILED types
/// ALWAYS fire on the failure edge (loud, error-status) so the loop never records a
/// silent false success. <c>CODE.GENERATED.FAILED</c> matches the
/// <c>SensitiveActionCatalog</c> code of the same name (this is the emitter that
/// fulfils that catalogue's <c>MapsExistingEmitter</c> claim).</para>
///
/// <list type="bullet">
///   <item><description><c>CODE.GENERATED.SUCCESS</c> / <c>CODE.GENERATED.FAILED</c>
///     — an LLM produced test or implementation code (RED / GREEN). The
///     <c>operation</c> tag/data distinguishes <c>testing</c> from
///     <c>implementation</c>.</description></item>
///   <item><description><c>CODE.REFACTORED.SUCCESS</c> / <c>CODE.REFACTORED.FAILED</c>
///     — the REFACTOR phase applied refactoring suggestions to the
///     implementation.</description></item>
/// </list>
/// </summary>
public static class CodeEvents
{
    public const string GeneratedSuccess = "CODE.GENERATED.SUCCESS";
    public const string GeneratedFailed = "CODE.GENERATED.FAILED";
    public const string RefactoredSuccess = "CODE.REFACTORED.SUCCESS";
    public const string RefactoredFailed = "CODE.REFACTORED.FAILED";

    /// <summary>The <c>operation</c> discriminators (mirrors the story's
    /// <c>operation</c> enum: generation / modification / refactoring / testing /
    /// documentation). The two generation phases share <c>CODE.GENERATED.*</c> and
    /// are told apart by this value.</summary>
    public const string OperationImplementation = "implementation";
    public const string OperationTesting = "testing";
    public const string OperationRefactoring = "refactoring";

    /// <summary>True for the loud (error-status) FAILED types — used by tests /
    /// callers to assert a failure was recorded as a failure, never a false
    /// success.</summary>
    public static bool IsFailureType(string type)
        => type == GeneratedFailed || type == RefactoredFailed;

    /// <summary>
    /// Build a <c>CODE.GENERATED.*</c> event for a code-generation step (RED test
    /// authoring or GREEN implementation). Tags carry the queryable DCB index keys
    /// (<c>storyId</c>/<c>sessionId</c>/<c>operation</c>); <c>Data</c> carries the
    /// files-changed payload (<c>files</c>/<c>fileCount</c>, optional
    /// <c>testCount</c>). Pure (no Elsa context) — exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildGenerated(
        bool success,
        string? storyId,
        Guid sessionId,
        string operation,
        IReadOnlyList<string>? files,
        int? testCount,
        string? error)
        => Build(
            success ? GeneratedSuccess : GeneratedFailed,
            success, storyId, sessionId, operation, files, testCount, error);

    /// <summary>
    /// Build a <c>CODE.REFACTORED.*</c> event for the REFACTOR phase. Same tag /
    /// data shape as <see cref="BuildGenerated"/> with <c>operation=refactoring</c>.
    /// Pure — exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildRefactored(
        bool success,
        string? storyId,
        Guid sessionId,
        IReadOnlyList<string>? files,
        string? error)
        => Build(
            success ? RefactoredSuccess : RefactoredFailed,
            success, storyId, sessionId, OperationRefactoring, files, testCount: null, error);

    private static TammaEvent Build(
        string type,
        bool success,
        string? storyId,
        Guid sessionId,
        string operation,
        IReadOnlyList<string>? files,
        int? testCount,
        string? error)
    {
        var fileList = (files ?? Array.Empty<string>()).ToList();

        var tags = new Dictionary<string, object?>
        {
            ["operation"] = operation,
        };
        if (!string.IsNullOrWhiteSpace(storyId)) tags["storyId"] = storyId;
        if (sessionId != Guid.Empty) tags["sessionId"] = sessionId.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["source"] = "ai_generated",
            ["fileCount"] = fileList.Count,
            ["files"] = fileList,
        };
        if (testCount is not null) data["testCount"] = testCount.Value;
        if (!success && !string.IsNullOrWhiteSpace(error)) data["reason"] = error;

        return new TammaEvent
        {
            EventType = type,
            Status = success ? "success" : "error",
            Error = success ? null : error,
            Tags = tags,
            Data = data,
        };
    }
}
