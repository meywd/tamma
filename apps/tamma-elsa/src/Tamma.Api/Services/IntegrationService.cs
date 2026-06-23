using Tamma.Core.Interfaces;

namespace Tamma.Api.Services;

/// <summary>
/// Facade implementation of IIntegrationService that delegates to focused services.
/// Adapts IntegrationResult&lt;T&gt; back to the original return types so existing
/// consumers compile unchanged.
/// </summary>
public class IntegrationService : IIntegrationService
{
    private readonly ISlackIntegrationService _slack;
    private readonly IGitHubIntegrationService _github;
    private readonly IJiraIntegrationService _jira;
    private readonly ICIIntegrationService _ci;
    private readonly IEmailIntegrationService _email;

    public IntegrationService(
        ISlackIntegrationService slack,
        IGitHubIntegrationService github,
        IJiraIntegrationService jira,
        ICIIntegrationService ci,
        IEmailIntegrationService email)
    {
        _slack = slack;
        _github = github;
        _jira = jira;
        _ci = ci;
        _email = email;
    }

    // ============================================
    // Slack delegation
    // ============================================

    public async Task SendSlackMessageAsync(string channel, string message)
    {
        var result = await _slack.SendSlackMessageAsync(channel, message);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    public async Task SendSlackDirectMessageAsync(string userId, string message)
    {
        var result = await _slack.SendSlackDirectMessageAsync(userId, message);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    // ============================================
    // Email delegation
    // ============================================

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var result = await _email.SendEmailAsync(to, subject, body);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    // ============================================
    // GitHub delegation
    // ============================================

    public async Task<GitHubBranchResult> CreateGitHubBranchAsync(string repository, string branchName)
    {
        var result = await _github.CreateGitHubBranchAsync(repository, branchName);
        return result.Success
            ? result.Data!
            : new GitHubBranchResult { Success = false, Error = result.Error };
    }

    public async Task<GitHubBranchResult> CreateGitHubBranchAsync(string repository, string branchName, string? baseBranch)
    {
        var result = await _github.CreateGitHubBranchAsync(repository, branchName, baseBranch);
        return result.Success
            ? result.Data!
            : new GitHubBranchResult { Success = false, Error = result.Error };
    }

    public async Task<bool> BranchExistsAsync(string repository, string branchName)
    {
        var result = await _github.BranchExistsAsync(repository, branchName);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data;
    }

    public async Task<List<GitHubCommit>> GetGitHubCommitsAsync(string repository, string branch, DateTime? since = null)
    {
        var result = await _github.GetGitHubCommitsAsync(repository, branch, since);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    public async Task<GitHubPullRequestResult> CreateGitHubPullRequestAsync(string repository, CreatePullRequestRequest request)
    {
        var result = await _github.CreateGitHubPullRequestAsync(repository, request);
        return result.Success
            ? result.Data!
            : new GitHubPullRequestResult { Success = false, Error = result.Error };
    }

    public async Task<GitHubPullRequestRef?> GetGitHubOpenPullRequestForBranchAsync(string repository, string headBranch, string baseBranch)
    {
        var result = await _github.GetGitHubOpenPullRequestForBranchAsync(repository, headBranch, baseBranch);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data;
    }

    public async Task<GitHubPullRequestResult> UpdateGitHubPullRequestAsync(string repository, int pullRequestNumber, CreatePullRequestRequest request)
    {
        var result = await _github.UpdateGitHubPullRequestAsync(repository, pullRequestNumber, request);
        return result.Success
            ? result.Data!
            : new GitHubPullRequestResult { Success = false, Error = result.Error };
    }

    public async Task<GitHubMergeResult> MergeGitHubPullRequestAsync(string repository, int pullRequestNumber)
    {
        var result = await _github.MergeGitHubPullRequestAsync(repository, pullRequestNumber);
        return result.Success
            ? result.Data!
            : new GitHubMergeResult { Success = false, Error = result.Error };
    }

    public async Task<GitHubMergeResult> MergeGitHubPullRequestAsync(string repository, int pullRequestNumber, string mergeStrategy)
    {
        var result = await _github.MergeGitHubPullRequestAsync(repository, pullRequestNumber, mergeStrategy);
        return result.Success
            ? result.Data!
            : new GitHubMergeResult { Success = false, Error = result.Error };
    }

    public async Task<GitHubPullRequestDetail> GetGitHubPullRequestAsync(string repository, int pullRequestNumber)
    {
        var result = await _github.GetGitHubPullRequestAsync(repository, pullRequestNumber);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    public async Task<List<GitHubFileChange>> GetGitHubFileChangesAsync(string repository, string branch)
    {
        var result = await _github.GetGitHubFileChangesAsync(repository, branch);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    public async Task<List<GitHubIssue>> ListGitHubIssuesAsync(string repository, string[]? labels = null, string state = "open")
    {
        var result = await _github.ListGitHubIssuesAsync(repository, labels, state);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    public async Task AssignGitHubIssueAsync(string repository, int issueNumber, string assignee)
    {
        var result = await _github.AssignGitHubIssueAsync(repository, issueNumber, assignee);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    public async Task CloseGitHubIssueAsync(string repository, int issueNumber, string? comment = null)
    {
        var result = await _github.CloseGitHubIssueAsync(repository, issueNumber, comment);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    public async Task DeleteGitHubBranchAsync(string repository, string branchName)
    {
        var result = await _github.DeleteGitHubBranchAsync(repository, branchName);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }

    public async Task<List<GitHubReviewComment>> GetPullRequestReviewCommentsAsync(string repository, int pullRequestNumber)
    {
        var result = await _github.GetPullRequestReviewCommentsAsync(repository, pullRequestNumber);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    // ============================================
    // CI/CD delegation
    // ============================================

    public async Task<TestRunResult> TriggerTestsAsync(string repository, string branch)
    {
        var result = await _ci.TriggerTestsAsync(repository, branch);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    public async Task<BuildStatus> GetBuildStatusAsync(string repository, string branch)
    {
        var result = await _ci.GetBuildStatusAsync(repository, branch);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        return result.Data!;
    }

    // ============================================
    // JIRA delegation
    // ============================================

    public async Task<JiraTicketResult> UpdateJiraTicketAsync(string ticketId, JiraTicketUpdate update)
    {
        var result = await _jira.UpdateJiraTicketAsync(ticketId, update);
        return result.Success
            ? result.Data!
            : new JiraTicketResult { Success = false, Error = result.Error };
    }

    public async Task<JiraTicket?> GetJiraTicketAsync(string ticketId)
    {
        var result = await _jira.GetJiraTicketAsync(ticketId);
        return result.Success ? result.Data : null;
    }
}
