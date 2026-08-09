using System.Text.Json.Serialization;

namespace Tamma.Platforms.GitLab.Dtos;

/// <summary>
/// Wire DTOs for GitLab REST API v4. Snake-case binding handled by
/// <c>JsonNamingPolicy.SnakeCaseLower</c> on the shared JSON options
/// in <see cref="GitLabHttpClient"/>.
///
/// <para>All records are mutable wire types — the driver maps them
/// into the platform-neutral <c>Models</c> records before returning to
/// callers.</para>
/// </summary>
internal sealed class GitLabProject
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? PathWithNamespace { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Visibility { get; set; }
    public string? Description { get; set; }
    public string? HttpUrlToRepo { get; set; }
    public string? WebUrl { get; set; }
    public GitLabNamespace? Namespace { get; set; }
}

internal sealed class GitLabNamespace
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? FullPath { get; set; }
}

internal sealed class GitLabBranchDto
{
    public string? Name { get; set; }
    public bool Protected { get; set; }
    public GitLabBranchCommit? Commit { get; set; }
}

internal sealed class GitLabBranchCommit
{
    public string? Id { get; set; }
}

internal sealed class GitLabFile
{
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string? Encoding { get; set; }
    public string? Content { get; set; }
    public string? Ref { get; set; }
}

internal sealed class GitLabMergeRequest
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? SourceBranch { get; set; }
    public string? TargetBranch { get; set; }
    public string? State { get; set; }
    public bool WorkInProgress { get; set; }
    public bool Draft { get; set; }
    public string? WebUrl { get; set; }
    public GitLabUser? Author { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Sha { get; set; }

    /// <summary>
    /// Epic 31 P6 M1 — base/start/head SHAs of the MR's latest diff
    /// version (single-MR GET). Empty right after MR creation (populates
    /// asynchronously per the API doc) — callers must tolerate null.
    /// </summary>
    public GitLabDiffRefs? DiffRefs { get; set; }
}

/// <summary>Wire shape of <c>merge_request.diff_refs</c>.</summary>
internal sealed class GitLabDiffRefs
{
    public string? BaseSha { get; set; }
    public string? StartSha { get; set; }
    public string? HeadSha { get; set; }
}

internal sealed class GitLabUser
{
    public string? Username { get; set; }
    public long Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class GitLabMrChanges
{
    public List<GitLabMrChange>? Changes { get; set; }
}

internal sealed class GitLabMrChange
{
    public string? OldPath { get; set; }
    public string? NewPath { get; set; }
    public string? Diff { get; set; }
    public bool NewFile { get; set; }
    public bool DeletedFile { get; set; }
    public bool RenamedFile { get; set; }
}

internal sealed class GitLabNote
{
    public long Id { get; set; }
    public string? Body { get; set; }
    public GitLabUser? Author { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class GitLabDiscussion
{
    public string? Id { get; set; }
    public List<GitLabNote>? Notes { get; set; }
}

internal sealed class GitLabHook
{
    public long Id { get; set; }
    public string? Url { get; set; }
    public bool PushEvents { get; set; }
    public bool MergeRequestsEvents { get; set; }
    public bool IssuesEvents { get; set; }
    public bool PipelineEvents { get; set; }
    public bool EnableSslVerification { get; set; }
}

internal sealed class GitLabPipeline
{
    public long Id { get; set; }
    public string? Status { get; set; }
    public string? Ref { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Source { get; set; }
}

internal sealed class GitLabJob
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Stage { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public GitLabArtifactsFile? ArtifactsFile { get; set; }
    public List<GitLabArtifactRef>? Artifacts { get; set; }
}

internal sealed class GitLabArtifactsFile
{
    public string? Filename { get; set; }
    public long Size { get; set; }
}

internal sealed class GitLabArtifactRef
{
    public string? FileType { get; set; }
    public long Size { get; set; }
    public string? Filename { get; set; }
}

internal sealed class GitLabVersionResponse
{
    public string? Version { get; set; }
    public string? Revision { get; set; }
}
