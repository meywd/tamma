using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that reverts failed refactoring changes.
/// When refactored code breaks tests, this activity reverts the changes via git checkout
/// so the pre-refactoring (passing) implementation is restored.
/// This is a safety net — refactoring revert is logged but not treated as a failure.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Revert Refactoring",
    "Revert failed refactoring changes via git checkout",
    Kind = ActivityKind.Task
)]
public class RevertRefactoringActivity : CodeActivity
{
    private readonly ILogger<RevertRefactoringActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Working branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Files that were changed by the refactoring and need to be reverted</summary>
    [Input(Description = "Files changed by the refactoring to revert")]
    public Input<List<string>> FilesToRevert { get; set; } = default!;

    [JsonConstructor]
    public RevertRefactoringActivity() { }

    public RevertRefactoringActivity(
        ILogger<RevertRefactoringActivity> logger,
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
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var filesToRevert = FilesToRevert.Get(context) ?? new List<string>();

        _logger?.LogWarning(
            "TDD REFACTOR revert: Reverting {FileCount} files for session {SessionId} because refactoring broke tests",
            filesToRevert.Count, sessionId);

        try
        {
            // Ctor-or-GetService: a store-rehydrated instance has a null
            // _configuration, so reading the callback URL off it always missed in
            // the deployed engine (findings 27/28 family).
            var configuration = _configuration ?? context.GetService<IConfiguration>();
            var callbackUrl = configuration?["Engine:CallbackUrl"];

            if (!string.IsNullOrWhiteSpace(callbackUrl))
            {
                // Call engine to perform git checkout on the changed files
                await CallEngineRevert(callbackUrl, repositoryUrl, branchName, filesToRevert);

                _logger?.LogInformation(
                    "TDD REFACTOR revert: Reverted refactoring for session {SessionId}. "
                    + "Pre-refactoring implementation restored. This is not a failure — "
                    + "refactoring was optional.",
                    sessionId);
            }
            else
            {
                // NOT a revert. This used to fall through to a "Successfully
                // reverted … Pre-refactoring implementation restored" line, which
                // told an operator the working tree had been restored when nothing
                // had run. The refactoring stays applied and the caller proceeds
                // (revert is best-effort by design) — so say exactly that.
                _logger?.LogWarning(
                    "TDD REFACTOR revert: NO revert seam (Engine:CallbackUrl unset), so the "
                    + "broken refactoring is STILL APPLIED for session {SessionId}. Files that "
                    + "would have been reverted: {Files}",
                    sessionId, string.Join(", ", filesToRevert));
            }
        }
        catch (Exception ex)
        {
            // Revert failure is concerning but we proceed with whatever state we have
            _logger?.LogError(
                ex,
                "TDD REFACTOR revert: Error reverting files for session {SessionId}. " +
                "Manual intervention may be needed.",
                sessionId);
        }
    }

    private async Task CallEngineRevert(
        string callbackUrl,
        string repositoryUrl,
        string branchName,
        List<string> filesToRevert)
    {
        var httpClient = _httpClientFactory!.CreateClient();
        var requestBody = new
        {
            prompt = $"Revert the following files to their state before refactoring using git checkout: {string.Join(", ", filesToRevert)}",
            role = "implementer",
            action = "git_revert",
            repository = repositoryUrl,
            branch = branchName,
            files = filesToRevert
        };

        var response = await httpClient.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
        response.EnsureSuccessStatusCode();
    }
}
