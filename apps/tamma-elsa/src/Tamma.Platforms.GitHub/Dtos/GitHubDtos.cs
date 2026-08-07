using System.Text.Json.Serialization;

namespace Tamma.Platforms.GitHub.Dtos;

// ================================================================
// Epic 31 P1 stage 2 — GitHub REST v3 DTOs. Snake_case wire names are
// mapped explicitly; only the fields the driver projects into the
// neutral Tamma.Platforms.Abstractions.Models records are declared.
// ================================================================

internal sealed class GitHubOwnerDto
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}

internal sealed class GitHubRepoDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("owner")] public GitHubOwnerDto? Owner { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

/// <summary>Shape of <c>GET /installation/repositories</c> (App mode).</summary>
internal sealed class GitHubInstallationReposDto
{
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("repositories")] public List<GitHubRepoDto>? Repositories { get; set; }
}

internal sealed class GitHubBranchCommitDto
{
    [JsonPropertyName("sha")] public string? Sha { get; set; }
}

internal sealed class GitHubBranchDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("commit")] public GitHubBranchCommitDto? Commit { get; set; }
    [JsonPropertyName("protected")] public bool Protected { get; set; }
}

internal sealed class GitHubContentsDto
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("encoding")] public string? Encoding { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

internal sealed class GitHubRefObjectDto
{
    [JsonPropertyName("sha")] public string? Sha { get; set; }
}

internal sealed class GitHubRefDto
{
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("object")] public GitHubRefObjectDto? Object { get; set; }
}

internal sealed class GitHubPrBranchDto
{
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("sha")] public string? Sha { get; set; }
}

internal sealed class GitHubLabelDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class GitHubPullRequestDto
{
    [JsonPropertyName("number")] public long Number { get; set; }
    [JsonPropertyName("node_id")] public string? NodeId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("merged")] public bool Merged { get; set; }
    [JsonPropertyName("merged_at")] public DateTimeOffset? MergedAt { get; set; }
    [JsonPropertyName("merge_commit_sha")] public string? MergeCommitSha { get; set; }
    [JsonPropertyName("mergeable")] public bool? Mergeable { get; set; }
    [JsonPropertyName("mergeable_state")] public string? MergeableState { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("user")] public GitHubOwnerDto? User { get; set; }
    [JsonPropertyName("head")] public GitHubPrBranchDto? Head { get; set; }
    [JsonPropertyName("base")] public GitHubPrBranchDto? Base { get; set; }
    [JsonPropertyName("labels")] public List<GitHubLabelDto>? Labels { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class GitHubPrFileDto
{
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("additions")] public int Additions { get; set; }
    [JsonPropertyName("deletions")] public int Deletions { get; set; }
}

/// <summary>Shape of <c>GET /repos/{o}/{r}/compare/{base}...{head}</c>.</summary>
internal sealed class GitHubCompareDto
{
    [JsonPropertyName("files")] public List<GitHubPrFileDto>? Files { get; set; }
}

internal sealed class GitHubCommentDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("user")] public GitHubOwnerDto? User { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    // Review comments only — null on plain issue comments.
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("line")] public int? Line { get; set; }
}

internal sealed class GitHubIssueDto
{
    [JsonPropertyName("number")] public long Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("labels")] public List<GitHubLabelDto>? Labels { get; set; }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
}

internal sealed class GitHubCommitAuthorDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
}

internal sealed class GitHubCommitDetailDto
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("author")] public GitHubCommitAuthorDto? Author { get; set; }
}

internal sealed class GitHubCommitDto
{
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("commit")] public GitHubCommitDetailDto? Commit { get; set; }
}

internal sealed class GitHubMergeResultDto
{
    [JsonPropertyName("merged")] public bool Merged { get; set; }
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

internal sealed class GitHubHookConfigDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

internal sealed class GitHubHookDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("events")] public List<string>? Events { get; set; }
    [JsonPropertyName("config")] public GitHubHookConfigDto? Config { get; set; }
}

internal sealed class GitHubWorkflowRunDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("head_branch")] public string? HeadBranch { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("run_started_at")] public DateTimeOffset? RunStartedAt { get; set; }
}

internal sealed class GitHubWorkflowRunsListDto
{
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("workflow_runs")] public List<GitHubWorkflowRunDto>? WorkflowRuns { get; set; }
}

internal sealed class GitHubJobDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
}

internal sealed class GitHubJobsListDto
{
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("jobs")] public List<GitHubJobDto>? Jobs { get; set; }
}
