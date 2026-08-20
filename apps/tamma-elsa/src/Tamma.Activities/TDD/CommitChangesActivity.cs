using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.Core;
using Tamma.Activities.TDD.Models;
using Tamma.Core;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that creates an atomic TDD commit including both test and implementation files.
/// Commit message format: feat({storyId}): {taskDescription} [TDD]
/// Only commits if the RED and GREEN phases succeeded.
///
/// <para><b>2026-08-18 — no commit is ever SIMULATED (E2E finding 28).</b> This
/// activity used to fall back to a <c>SimulateCommit</c> helper that minted a SHA
/// from <c>Guid.NewGuid()</c> and returned <c>Success = true</c>, which put
/// <c>COMMIT.CREATED.SUCCESS</c> rows carrying FABRICATED SHAs onto the DCB audit
/// stream for commits that never happened (E2E run 38: two "successful commits",
/// <c>head == base</c>, an empty PR). It took that branch on two triggers: an
/// <c>Anthropic:UseMock</c> host, and — far worse — a null <c>_configuration</c>,
/// which is the NORMAL state of a store-rehydrated activity instance (Elsa
/// rehydrates through the <see cref="JsonConstructorAttribute"/> ctor, leaving every
/// DI field null; same family as findings 27/28). So in the deployed engine the
/// fabricating branch was the DEFAULT branch.
///
/// The simulation is gone. Every path that did not produce a real commit now emits
/// the loud <c>COMMIT.CREATED.FAILED</c> event and throws a typed
/// <see cref="TammaError"/>; <c>tdd-cycle</c> runs on Elsa's default fault strategy,
/// so the throw faults the cycle instead of letting it walk on to its
/// <c>success = true</c> terminal.</para>
///
/// <para><b>What is still missing (recorded, not half-built).</b> The one write
/// seam here is <c>POST {Engine:CallbackUrl}/api/engine/execute-task</c> with
/// <c>action=git_commit</c> — and that endpoint is an LLM proxy
/// (<c>IExecuteTaskService</c>): it requires a <c>prompt</c> field, answers
/// <c>400 prompt is required</c> to this payload, and implements no git operation
/// at all. A genuine commit cannot be routed through the Epic 31 mediation plane
/// today for two independent reasons: (1) <c>IGitPlatformClient</c> has no
/// contents-write verb — it can read a file and create/delete a branch, but there is
/// no create-commit / put-contents member on the interface or on any of its three
/// drivers plus the null seam; (2) this activity only ever receives file PATHS
/// (<c>TestFiles</c> / <c>ImplementationFiles</c>), never file CONTENTS, so it could
/// not populate such a call even if the verb existed. Adding both is a cross-cutting
/// change to the platform abstraction, its drivers and the mediation surface — out of
/// scope here, so the failure is loud and typed instead. The real implementer stays
/// the agent-executor path (<c>ExecuteAgentActivity</c>, finding 27), which commits
/// on the branch itself.</para>
/// </summary>
[Activity(
    "Tamma.TDD",
    "Commit Changes",
    "Create atomic TDD commit with test and implementation files",
    Kind = ActivityKind.Task
)]
public class CommitChangesActivity : CodeActivity<CommitResult>
{
    private readonly ILogger<CommitChangesActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier (used in commit message)</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Task description (used in commit message)</summary>
    [Input(Description = "Task description for commit message")]
    public Input<string> TaskDescription { get; set; } = default!;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Working branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Test files to commit</summary>
    [Input(Description = "Test files to include in commit")]
    public Input<List<string>> TestFiles { get; set; } = default!;

    /// <summary>Implementation files to commit</summary>
    [Input(Description = "Implementation files to include in commit")]
    public Input<List<string>> ImplementationFiles { get; set; } = default!;

    [JsonConstructor]
    public CommitChangesActivity() { }

    public CommitChangesActivity(
        ILogger<CommitChangesActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var taskDescription = TaskDescription.Get(context);
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var testFiles = TestFiles.Get(context) ?? new List<string>();
        var implementationFiles = ImplementationFiles.Get(context) ?? new List<string>();

        // Ctor-or-GetService (findings 27/28): a store-rehydrated instance carries
        // null DI fields, and reading configuration off that null is exactly how the
        // fabricating branch became the default in the deployed engine.
        var configuration = _configuration ?? context.GetService<IConfiguration>();

        var allFiles = testFiles.Concat(implementationFiles).Distinct().ToList();
        var commitMessageFormat = configuration?["TDD:CommitMessageFormat"]
            ?? "feat({storyId}): {taskDescription} [TDD]";
        var commitMessage = commitMessageFormat
            .Replace("{storyId}", storyId)
            .Replace("{taskDescription}", taskDescription);

        _logger?.LogInformation(
            "TDD Commit: Creating commit for {FileCount} files in story {StoryId}, session {SessionId}",
            allFiles.Count, storyId, sessionId);

        try
        {
            if (allFiles.Count == 0)
            {
                _logger?.LogWarning(
                    "TDD Commit: No files to commit for session {SessionId}", sessionId);

                // An empty change set means the RED/GREEN phases produced nothing:
                // no commit happened, so this is a loud COMMIT.CREATED.FAILED and a
                // typed throw — never a quiet Success=false the cycle walks past.
                throw Fail(context, storyId, sessionId, commitMessage, branchName,
                    repositoryUrl, allFiles,
                    ErrorNoFiles, "No files to commit", retryable: false);
            }

            var callbackUrl = configuration?["Engine:CallbackUrl"];
            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                // The ONLY write seam this activity has. No callback URL (or no
                // readable configuration at all) means there is no way to create a
                // commit — which is precisely the state that used to fabricate one.
                throw Fail(context, storyId, sessionId, commitMessage, branchName,
                    repositoryUrl, allFiles, ErrorNoSeam,
                    configuration is null
                        ? "No commit seam: IConfiguration is unavailable on this activity "
                          + "instance, so Engine:CallbackUrl cannot be read"
                        : "No commit seam: Engine:CallbackUrl is not configured",
                    retryable: false);
            }

            var result = await CallEngineCommit(
                context, callbackUrl, repositoryUrl, branchName, commitMessage, allFiles,
                storyId, sessionId);

            _logger?.LogInformation(
                "TDD Commit: Committed for session {SessionId}. SHA={Sha}, Message=\"{Message}\"",
                sessionId, result.CommitSha, result.CommitMessage);

            // Story 4-5 (AC2) — capture the commit as a DCB event
            // (COMMIT.CREATED.SUCCESS) carrying sha / message / branch / file-count.
            // Reachable ONLY with a platform-issued SHA in hand: every other path
            // above throws, so a SUCCESS row on the audit stream now implies a
            // commit that actually exists.
            TammaEventEmitter.Emit(context, this, _logger,
                CommitEvents.BuildCreated(success: true, storyId, sessionId,
                    result.CommitSha, result.CommitMessage, branchName, repositoryUrl,
                    result.FilesCommitted, error: null));

            context.SetResult(result);
        }
        catch (TammaError)
        {
            // Already emitted its own COMMIT.CREATED.FAILED with the precise reason.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD Commit: Error creating commit for session {SessionId}", sessionId);

            throw Fail(context, storyId, sessionId, commitMessage, branchName,
                repositoryUrl, allFiles, ErrorBridgeFailed,
                $"Commit failed: {ex.Message}", retryable: true, inner: ex);
        }
    }

    /// <summary>Machine-readable codes for the four ways a TDD commit does not happen.</summary>
    public const string ErrorNoFiles = "TDD.COMMIT.NO_FILES";
    public const string ErrorNoSeam = "TDD.COMMIT.NO_SEAM";
    public const string ErrorNoSha = "TDD.COMMIT.NO_SHA";
    public const string ErrorBridgeFailed = "TDD.COMMIT.BRIDGE_FAILED";

    /// <summary>
    /// Emit the loud <c>COMMIT.CREATED.FAILED</c> row and build the typed error to
    /// throw. One helper so the event and the exception can never disagree about
    /// why the commit did not happen.
    /// </summary>
    private TammaError Fail(
        ActivityExecutionContext context,
        string? storyId,
        Guid sessionId,
        string commitMessage,
        string? branchName,
        string? repositoryUrl,
        List<string> files,
        string code,
        string reason,
        bool retryable,
        Exception? inner = null)
    {
        TammaEventEmitter.Emit(context, this, _logger,
            CommitEvents.BuildCreated(success: false, storyId, sessionId,
                sha: null, commitMessage, branchName, repositoryUrl, files,
                error: reason));

        return new TammaError(
            code,
            reason,
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["storyId"] = storyId,
                ["branch"] = branchName,
                ["repository"] = repositoryUrl,
                ["fileCount"] = files.Count,
                ["inner"] = inner?.Message,
            },
            retryable,
            TammaErrorSeverity.High);
    }

    /// <summary>
    /// A commit SHA is 7–40 hex characters. Anything else — absent, empty, a
    /// status word, an LLM's prose — is not a commit id, and accepting one would
    /// put an unverifiable SHA back on the audit stream.
    /// </summary>
    internal static bool IsCommitSha(string? sha)
        => sha is { Length: >= 7 and <= 40 }
           && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// Read a commit id out of the engine-callback response. True ONLY when the
    /// response does not report failure AND carries something that is actually a
    /// commit id. Pure, so the "what counts as proof a commit happened" rule is
    /// unit-testable without an Elsa context — the old code took HTTP 200 as the
    /// proof and copied <c>commitSha</c> even when the property was absent
    /// (yielding <c>""</c>), which is exactly the shape an LLM-proxy answer has.
    /// </summary>
    internal static bool TryReadCommitSha(JsonElement response, out string? sha)
    {
        sha = response.ValueKind == JsonValueKind.Object
              && response.TryGetProperty("commitSha", out var shaEl)
            ? shaEl.ValueKind == JsonValueKind.String ? shaEl.GetString() : null
            : null;

        var reportedFailure = response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("success", out var okEl)
            && okEl.ValueKind == JsonValueKind.False;

        return !reportedFailure && IsCommitSha(sha);
    }

    /// <summary>
    /// Ask the engine callback to create the commit and return it ONLY when the
    /// answer carries a real commit id. Today's <c>/api/engine/execute-task</c> is
    /// an LLM proxy that rejects this payload outright (<c>400 prompt is
    /// required</c>), so in every current deployment this ends in a loud
    /// <see cref="ErrorBridgeFailed"/> — which is the truthful outcome, and the
    /// reason the caller must never treat this step as best-effort.
    /// </summary>
    private async Task<CommitResult> CallEngineCommit(
        ActivityExecutionContext context,
        string callbackUrl,
        string repositoryUrl,
        string branchName,
        string commitMessage,
        List<string> files,
        string? storyId,
        Guid sessionId)
    {
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();
        if (httpClientFactory is null)
        {
            throw Fail(context, storyId, sessionId, commitMessage, branchName,
                repositoryUrl, files, ErrorNoSeam,
                "No commit seam: IHttpClientFactory is unavailable on this activity instance",
                retryable: false);
        }

        var httpClient = httpClientFactory.CreateClient();
        var requestBody = new
        {
            action = "git_commit",
            repository = repositoryUrl,
            branch = branchName,
            commitMessage,
            files
        };

        var response = await httpClient.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
        if (!response.IsSuccessStatusCode)
        {
            throw Fail(context, storyId, sessionId, commitMessage, branchName,
                repositoryUrl, files, ErrorBridgeFailed,
                $"Commit bridge refused the git_commit call with HTTP {(int)response.StatusCode}",
                retryable: true);
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A commit with no id is not a commit.
        if (!TryReadCommitSha(result, out var sha))
        {
            throw Fail(context, storyId, sessionId, commitMessage, branchName,
                repositoryUrl, files, ErrorNoSha,
                "Commit bridge answered without a commit SHA — no commit was created "
                + $"(commitSha=\"{sha}\")",
                retryable: false);
        }

        return new CommitResult
        {
            Success = true,
            CommitSha = sha!,
            CommitMessage = commitMessage,
            FilesCommitted = files
        };
    }
}
