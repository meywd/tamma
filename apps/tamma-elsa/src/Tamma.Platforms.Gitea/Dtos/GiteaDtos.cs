using System.Text.Json.Serialization;

namespace Tamma.Platforms.Gitea.Dtos;

/// <summary>
/// Internal wire DTOs for deserializing Gitea REST v1 responses.
/// Public surface is the neutral <see cref="Tamma.Platforms.Abstractions.Models"/>
/// records; these classes exist only to ride <c>System.Text.Json</c>.
///
/// <para>Property names follow Gitea's snake_case JSON convention via
/// <see cref="JsonPropertyNameAttribute"/> rather than relying on a global
/// naming policy — Gitea is occasionally inconsistent (e.g.
/// <c>html_url</c> + <c>default_branch</c> are snake but
/// <c>private</c> drops the prefix) so per-property names are safer.</para>
/// </summary>
internal sealed class GiteaVersionDto
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class GiteaUserDto
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }
}

internal sealed class GiteaRepoDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("owner")]
    public GiteaUserDto? Owner { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("clone_url")]
    public string? CloneUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

internal sealed class GiteaBranchDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("commit")]
    public GiteaCommitDto? Commit { get; set; }

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }
}

internal sealed class GiteaCommitDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class GiteaCreateBranchDto
{
    [JsonPropertyName("new_branch_name")]
    public string? NewBranchName { get; set; }

    [JsonPropertyName("old_branch_name")]
    public string? OldBranchName { get; set; }

    [JsonPropertyName("old_ref_name")]
    public string? OldRefName { get; set; }
}

internal sealed class GiteaContentsDto
{
    /// <summary>file | dir | symlink | submodule</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>Base64-encoded file body when <c>type=file</c>.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

internal sealed class GiteaPullRequestRefDto
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

internal sealed class GiteaPullRequestDto
{
    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("user")]
    public GiteaUserDto? User { get; set; }

    [JsonPropertyName("head")]
    public GiteaPullRequestRefDto? Head { get; set; }

    [JsonPropertyName("base")]
    public GiteaPullRequestRefDto? Base { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class GiteaCreatePullDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("head")]
    public string? Head { get; set; }

    [JsonPropertyName("base")]
    public string? Base { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

internal sealed class GiteaMergePullDto
{
    [JsonPropertyName("Do")]
    public string? Do { get; set; }

    [JsonPropertyName("MergeMessageField")]
    public string? MergeMessage { get; set; }
}

internal sealed class GiteaPrFileDto
{
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }
}

internal sealed class GiteaIssueCommentDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("user")]
    public GiteaUserDto? User { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class GiteaCreateReviewDto
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("comments")]
    public List<GiteaCreateReviewCommentDto>? Comments { get; set; }
}

internal sealed class GiteaCreateReviewCommentDto
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("new_position")]
    public int NewPosition { get; set; }
}

internal sealed class GiteaReviewDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("user")]
    public GiteaUserDto? User { get; set; }

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset SubmittedAt { get; set; }
}

internal sealed class GiteaWebhookDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("config")]
    public Dictionary<string, string>? Config { get; set; }

    [JsonPropertyName("events")]
    public List<string>? Events { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

internal sealed class GiteaCreateWebhookDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("config")]
    public Dictionary<string, string>? Config { get; set; }

    [JsonPropertyName("events")]
    public List<string>? Events { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

internal sealed class GiteaWorkflowRunDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class GiteaWorkflowRunsListDto
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("workflow_runs")]
    public List<GiteaWorkflowRunDto>? WorkflowRuns { get; set; }
}

internal sealed class GiteaJobDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }
}

internal sealed class GiteaJobsListDto
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("jobs")]
    public List<GiteaJobDto>? Jobs { get; set; }
}

internal sealed class GiteaErrorDto
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Some Gitea endpoints return <c>errors</c> array.</summary>
    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }
}
