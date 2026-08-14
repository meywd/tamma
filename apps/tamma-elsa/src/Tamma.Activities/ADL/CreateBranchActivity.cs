using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Activities.ADL;

/// <summary>
/// Creates (or idempotently reuses) a feature branch that isolates an issue's
/// autonomous-development work. Branch name: <c>adl/{issueNumber}-{sanitized-title}</c>.
///
/// <para>Story 2.4 build-out: the activity now performs base-branch validation,
/// idempotent conflict resolution (branch already exists → suffix / timestamp /
/// abort per strategy — never a 422 hard-fail), and post-create validation, and
/// surfaces the base SHA + a typed error classification. The orchestration core
/// NEVER throws and NEVER reports a false success — every path completes with a
/// single Elsa outcome that the workflow routes (Created → success edge / Error →
/// explicit failure edge).</para>
///
/// Outcomes:
///   - Created: branch created (or reused under the conflict strategy).
///   - Error:   branch creation failed (the workflow routes this to the failure edge).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Create Branch",
    "Create a feature branch for autonomous development",
    Kind = ActivityKind.Task
)]
[FlowNode("Created", "Error")]
public class CreateBranchActivity : Activity
{
    /// <summary>Default conflict strategy when none is supplied.</summary>
    public const string DefaultConflictStrategy = "suffix";

    /// <summary>Default base branch when none is supplied.</summary>
    public const string DefaultBaseBranch = "main";

    /// <summary>Max sanitized-title length carried into the branch slug.</summary>
    public const int MaxTitleLength = 40;

    /// <summary>Cap on the suffix search so a pathological repo can't loop forever.</summary>
    public const int MaxConflictSuffix = 100;

    private readonly ILogger<CreateBranchActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Issue title for branch naming")]
    public Input<string> IssueTitle { get; set; } = default!;

    [Input(Description = "Base branch to cut from (default main)")]
    public Input<string> BaseBranch { get; set; } = new(DefaultBaseBranch);

    [Input(Description = "Conflict strategy: suffix | timestamp | abort (default suffix)")]
    public Input<string> ConflictStrategy { get; set; } = new(DefaultConflictStrategy);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "Created (or reused) branch name")]
    public Output<string?> BranchName { get; set; } = default!;

    [Output(Description = "SHA of the base ref the branch was cut from")]
    public Output<string?> BaseSha { get; set; } = default!;

    [Output(Description = "True when an existing-branch conflict was resolved (suffix/timestamp)")]
    public Output<bool> ConflictResolved { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> ErrorCode { get; set; } = default!;

    [Output(Description = "Human-readable failure reason when the Error outcome fires")]
    public Output<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public CreateBranchActivity() { }

    /// <summary>
    /// Story 38-1 — thin-client DI constructor. The activity holds NO
    /// <c>IGitHubIntegrationService</c> and no git token: it delegates the branch
    /// create to <c>POST /api/v1/git/{owner}/{repo}/branches</c> via
    /// <see cref="TammaApiClient"/>, where the per-tenant token lives.
    /// </summary>
    public CreateBranchActivity(
        ILogger<CreateBranchActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var issueNumber = IssueNumber.Get(context);
        var issueTitle = IssueTitle.Get(context) ?? "";
        var baseBranch = string.IsNullOrWhiteSpace(BaseBranch.Get(context)) ? DefaultBaseBranch : BaseBranch.Get(context)!;
        var strategy = string.IsNullOrWhiteSpace(ConflictStrategy.Get(context)) ? DefaultConflictStrategy : ConflictStrategy.Get(context)!;
        var tenantId = NormalizeTenant(TenantId.GetOrDefault(context));

        // The candidate branch name is generated engine-side (pure, token-free);
        // the API performs the idempotent conflict-resolution + create + validate.
        var candidate = GenerateBranchName(issueNumber, issueTitle);
        var request = new GitCreateBranchRequest
        {
            BranchName = candidate,
            BaseRef = baseBranch,
            ConflictStrategy = strategy,
            IssueNumber = issueNumber,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.CreateBranchAsync(repository, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);
        if (outcome.Outcome == "Created")
        {
            BranchName.Set(context, outcome.BranchName);
            BaseSha.Set(context, outcome.BaseSha);
            ConflictResolved.Set(context, outcome.ConflictResolved);
            await context.CompleteActivityWithOutcomesAsync("Created");
        }
        else
        {
            ErrorCode.Set(context, outcome.ErrorCode ?? "unknown");
            Error.Set(context, outcome.Error ?? "branch creation failed");
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    /// <summary>
    /// Story 38-1 (AC5) — project the git-mediation wire response into the SAME
    /// <see cref="BranchCreationOutcome"/> the local path produced, so the outputs
    /// (BranchName / BaseSha / ConflictResolved / ErrorCode / Error) and the
    /// Created/Error outcome are byte-compatible. A null response (guard 403 /
    /// token 503 / auth 401 / transport) fails closed to Error.
    /// </summary>
    public static BranchCreationOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return BranchCreationOutcome.Failed("git-mediation-unavailable", "git mediation endpoint unavailable");

        if (response.Success && response.Outcome == "Created")
            return BranchCreationOutcome.Created(response.BranchRef ?? "", response.BaseSha, response.ConflictResolved ?? false);

        return BranchCreationOutcome.Failed(
            response.FailureCode ?? "unknown",
            response.FailureReason ?? "branch creation failed");
    }

    /// <summary>Normalize a tenant-id input to a non-empty trimmed string or null
    /// (empty / whitespace ⇒ single-user / platform scope).</summary>
    internal static string? NormalizeTenant(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Pure-ish orchestration core (no Elsa context): ref-name validation →
    /// idempotent conflict resolution → base-branch-aware create → post-create
    /// validation, with typed error classification. Returns a typed outcome so the
    /// happy / idempotency / failure / validation paths are unit-testable against a
    /// mocked <see cref="IGitPlatformClient"/> (Epic 31 P2 — retyped from the
    /// GitHub-specific client onto the platform abstraction; the coarse error
    /// classes are preserved via <see cref="PlatformErrorText.ToLegacyString"/>).
    /// NEVER throws — exceptions become an <c>Error</c> outcome (no silent
    /// success); a transient existence lookup failure is an Error too (never
    /// mistaken for "absent → free to create").
    /// </summary>
    public static async Task<BranchCreationOutcome> ExecuteCoreAsync(
        IGitPlatformClient client,
        string repository,
        int issueNumber,
        string candidateName,
        string baseBranch,
        string conflictStrategy,
        ILogger? logger = null)
    {
        try
        {
            var (owner, repoName) = GitRepoName.Split(repository);

            // ── Ref-name injection hardening (cap #11) ──
            if (!IsValidRefName(candidateName))
            {
                logger?.LogError("Invalid branch ref name {Branch} for issue #{Issue}", candidateName, issueNumber);
                return BranchCreationOutcome.Failed("invalid_ref", $"invalid branch ref name: {candidateName}");
            }

            // ── Idempotent conflict resolution (AC3) ──
            var resolved = await ResolveConflictAsync(client, owner, repoName, candidateName, conflictStrategy, logger);
            if (!resolved.Success)
                return BranchCreationOutcome.Failed(resolved.ErrorCode!, resolved.Error!);

            var finalName = resolved.FinalName!;
            var conflictResolved = resolved.ConflictResolved;

            // ── Base-branch resolution + create (AC2 / AC4). The live path's
            //    CreateGitHubBranchAsync resolved the base ref internally and
            //    surfaced a "base_branch_not_found: …" sentinel on a missing
            //    base; the abstraction takes a SHA, so the base read happens
            //    here with the same sentinel. ──
            var baseRead = await client.GetBranchAsync(owner, repoName, baseBranch);
            if (baseRead is PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Failed baseFailed)
            {
                if (baseFailed.Error is PlatformError.NotFound)
                {
                    logger?.LogError("Base branch {Base} not found in {Repo}", baseBranch, repository);
                    return BranchCreationOutcome.Failed(
                        "base_branch_not_found", $"base_branch_not_found: base branch {baseBranch} not found");
                }
                var baseError = PlatformErrorText.ToLegacyString(baseFailed.Error);
                logger?.LogError("Base branch {Base} lookup failed: {Error}", baseBranch, baseError);
                return BranchCreationOutcome.Failed(ClassifyError(baseError), baseError);
            }
            if (baseRead is not PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Ok baseOk)
            {
                return BranchCreationOutcome.Failed("transient", "503: platform unavailable");
            }
            var baseSha = baseOk.Value.Sha;

            var create = await client.CreateBranchAsync(
                new CreateBranchRequest(owner, repoName, finalName, baseSha));
            if (create is PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Failed createFailed)
            {
                var error = PlatformErrorText.ToLegacyString(createFailed.Error);
                var code = ClassifyError(error);
                logger?.LogError("Failed to create branch {Branch} from {Base}: {Error}", finalName, baseBranch, error);
                return BranchCreationOutcome.Failed(code, error);
            }
            if (create is not PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Ok)
            {
                return BranchCreationOutcome.Failed("transient", "503: platform unavailable");
            }

            // ── Post-create validation (AC5) — confirm the ref actually exists. ──
            var verify = await BranchExistsAsync(client, owner, repoName, finalName);
            if (verify.Error is not null)
            {
                logger?.LogError("Post-create validation lookup failed for {Branch}: {Error}", finalName, verify.Error);
                return BranchCreationOutcome.Failed(ClassifyError(verify.Error), verify.Error);
            }
            if (!verify.Exists)
            {
                logger?.LogError("Branch {Branch} not found after create — validation failed", finalName);
                return BranchCreationOutcome.Failed("validation_failed", $"branch {finalName} not found after create");
            }

            logger?.LogInformation(
                "Created branch {Branch} for issue #{Number} from {Base}@{Sha}",
                finalName, issueNumber, baseBranch, baseSha ?? "?");
            return BranchCreationOutcome.Created(finalName, baseSha, conflictResolved);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error creating branch for issue #{Issue}", issueNumber);
            return BranchCreationOutcome.Failed("unknown", ex.Message);
        }
    }

    /// <summary>
    /// Existence probe over the abstraction's single-branch read:
    /// Ok → exists; NotFound → absent; anything else → a legacy-string
    /// error (never mistaken for "absent").
    /// </summary>
    private static async Task<(bool Exists, string? Error)> BranchExistsAsync(
        IGitPlatformClient client, string owner, string repoName, string branchName)
    {
        var result = await client.GetBranchAsync(owner, repoName, branchName);
        return result switch
        {
            PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Ok => (true, null),
            PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Failed f
                when f.Error is PlatformError.NotFound => (false, null),
            PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.Failed f =>
                (false, PlatformErrorText.ToLegacyString(f.Error)),
            _ => (false, "503: platform unavailable"),
        };
    }

    /// <summary>
    /// Resolve a branch-name conflict per the strategy. Returns the name to create
    /// (the original when free, a suffixed/timestamped name when occupied) or a
    /// failure. A transient existence-lookup failure surfaces as an Error rather
    /// than being treated as "absent → create" (no false success).
    /// </summary>
    private static async Task<ConflictResolution> ResolveConflictAsync(
        IGitPlatformClient client,
        string owner,
        string repoName,
        string candidate,
        string strategy,
        ILogger? logger)
    {
        var exists = await BranchExistsAsync(client, owner, repoName, candidate);
        if (exists.Error is not null)
            return ConflictResolution.Fail(ClassifyError(exists.Error), exists.Error);
        if (!exists.Exists)
            return ConflictResolution.Ok(candidate, conflictResolved: false);

        switch (strategy.ToLowerInvariant())
        {
            case "abort":
                logger?.LogWarning("Branch {Branch} already exists; abort strategy → failing", candidate);
                return ConflictResolution.Fail("branch_exists", $"branch {candidate} already exists");

            case "timestamp":
            {
                var stamped = $"{candidate}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                var stampedExists = await BranchExistsAsync(client, owner, repoName, stamped);
                if (stampedExists.Error is not null)
                    return ConflictResolution.Fail(ClassifyError(stampedExists.Error), stampedExists.Error);
                if (stampedExists.Exists)
                    return ConflictResolution.Fail("conflict_exhausted", $"timestamped branch {stamped} already exists");
                logger?.LogWarning("Branch {Base} exists; timestamp strategy → {Final}", candidate, stamped);
                return ConflictResolution.Ok(stamped, conflictResolved: true);
            }

            default: // "suffix"
            {
                for (var i = 2; i <= MaxConflictSuffix; i++)
                {
                    var suffixed = $"{candidate}-{i}";
                    var suffixedExists = await BranchExistsAsync(client, owner, repoName, suffixed);
                    if (suffixedExists.Error is not null)
                        return ConflictResolution.Fail(ClassifyError(suffixedExists.Error), suffixedExists.Error);
                    if (!suffixedExists.Exists)
                    {
                        logger?.LogWarning("Branch {Base} exists; suffix strategy → {Final}", candidate, suffixed);
                        return ConflictResolution.Ok(suffixed, conflictResolved: true);
                    }
                }
                logger?.LogError("Suffix strategy exhausted for {Branch} (>{Max})", candidate, MaxConflictSuffix);
                return ConflictResolution.Fail("conflict_exhausted", $"no free suffix for {candidate} within {MaxConflictSuffix}");
            }
        }
    }

    // ================================================================
    // Pure, testable helpers
    // ================================================================

    /// <summary>
    /// Generate the branch name <c>adl/{issueNumber}-{sanitized-title}</c>. When the
    /// title sanitizes to empty the slug is just <c>adl/{issueNumber}</c>.
    /// </summary>
    public static string GenerateBranchName(int issueNumber, string? issueTitle)
    {
        var sanitized = SanitizeBranchName(issueTitle ?? "");
        return string.IsNullOrEmpty(sanitized)
            ? $"adl/{issueNumber}"
            : $"adl/{issueNumber}-{sanitized}";
    }

    private static string SanitizeBranchName(string title)
    {
        var lowered = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('/', '-')
            .Replace('\\', '-');

        var chars = lowered
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(MaxTitleLength)
            .ToArray();

        return new string(chars).Trim('-');
    }

    /// <summary>
    /// Validate a branch ref name against the load-bearing git ref rules (cap #11):
    /// non-empty, no <c>..</c>, no leading <c>-</c>, no whitespace / control chars,
    /// no <c>~^:?*[</c>, no trailing slash, no double slash. Defensive — the
    /// generator already produces safe names, but a config-driven pattern or an
    /// odd title must never reach the API as an injection.
    /// </summary>
    public static bool IsValidRefName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith('-')) return false;
        if (name.EndsWith('/')) return false;
        if (name.Contains("..")) return false;
        if (name.Contains("//")) return false;
        if (name.EndsWith(".lock")) return false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') return false;
        }
        return Regex.IsMatch(name, @"^[A-Za-z0-9._/-]+$");
    }

    /// <summary>
    /// Classify a create / lookup failure for the failure edge: permission /
    /// missing-base / protected-base / transient / unknown. The integration layer
    /// surfaces a status-prefixed message (e.g. <c>"403: ..."</c>) or a typed
    /// <c>base_branch_not_found: ...</c> sentinel.
    /// </summary>
    public static string ClassifyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unknown";
        var lower = error.ToLowerInvariant();
        if (lower.Contains("base_branch_not_found") || lower.StartsWith("404")) return "base_branch_not_found";
        if (lower.Contains("protected")) return "base_branch_protected";
        if (lower.Contains("403") || lower.Contains("forbidden") || lower.Contains("permission")) return "permission_denied";
        if (lower.Contains("422")) return "base_branch_protected";
        if (lower.Contains("429") || lower.Contains("rate limit")
            || lower.Contains("500") || lower.Contains("502") || lower.Contains("503") || lower.Contains("504")
            || lower.Contains("timeout") || lower.Contains("unavailable")) return "transient";
        return "unknown";
    }

    private sealed class ConflictResolution
    {
        public bool Success { get; private init; }
        public string? FinalName { get; private init; }
        public bool ConflictResolved { get; private init; }
        public string? ErrorCode { get; private init; }
        public string? Error { get; private init; }

        public static ConflictResolution Ok(string finalName, bool conflictResolved)
            => new() { Success = true, FinalName = finalName, ConflictResolved = conflictResolved };

        public static ConflictResolution Fail(string errorCode, string error)
            => new() { Success = false, ErrorCode = errorCode, Error = error };
    }
}

/// <summary>
/// Typed result of <see cref="CreateBranchActivity.ExecuteCoreAsync"/> — maps
/// directly to the activity's Elsa outcome (Created / Error). On failure
/// <see cref="BranchName"/> is empty so a consumer can never read a false branch.
/// </summary>
public sealed class BranchCreationOutcome
{
    public string Outcome { get; init; } = "Error";
    public string? BranchName { get; init; }
    public string? BaseSha { get; init; }
    public bool ConflictResolved { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }

    public static BranchCreationOutcome Created(string branchName, string? baseSha, bool conflictResolved)
        => new() { Outcome = "Created", BranchName = branchName, BaseSha = baseSha, ConflictResolved = conflictResolved };

    public static BranchCreationOutcome Failed(string errorCode, string error)
        => new() { Outcome = "Error", BranchName = "", ErrorCode = errorCode, Error = error };
}
