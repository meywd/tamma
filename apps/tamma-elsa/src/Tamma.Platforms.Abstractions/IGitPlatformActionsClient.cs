using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC1 — the "CI surface" git-platform drivers may
/// optionally implement. Drivers without CI dispatch (pure-git
/// forges, or platforms where Tamma is read-only) return null from
/// <see cref="IGitPlatformDriver.Actions"/>.
///
/// <para>5 methods covering the agent-dispatch loop: dispatch,
/// poll-status, list-jobs, fetch-artifact, cancel.</para>
///
/// <para>Status &amp; conclusion strings are platform-native — see
/// <see cref="WorkflowRun"/> for rationale. Callers branch on
/// strings appropriate to their platform; the abstraction does NOT
/// normalize them.</para>
/// </summary>
public interface IGitPlatformActionsClient
{
    /// <summary>
    /// Dispatch a workflow / pipeline. Returns the run that was
    /// created when the platform exposes that on the dispatch
    /// response (GitHub does not — driver may re-fetch via list-runs
    /// to give callers a usable run id).
    /// </summary>
    Task<PlatformResult<WorkflowRun>> DispatchWorkflowAsync(
        string owner, string repoName,
        WorkflowDispatchRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch the status of a single run.
    /// </summary>
    Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default);

    /// <summary>
    /// Epic 31 P3 — list recent runs / pipelines, newest first,
    /// optionally filtered to a branch. Backs the CI mediation
    /// plane's build-status read ("latest run on this branch") so it
    /// needs no platform-specific client. An empty list is a
    /// successful result (the caller decides what "no runs" means).
    /// </summary>
    Task<PlatformResult<IReadOnlyList<WorkflowRun>>> ListRunsAsync(
        string owner, string repoName,
        ListWorkflowRunsRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// List the jobs / stages of a run.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default);

    /// <summary>
    /// Epic 31 P3 (seam 6) — list the artifacts a run produced (the
    /// agent-dispatch collect step reads the <c>tamma-result</c>
    /// artifact by NAME before downloading it). Platforms whose
    /// artifacts hang off jobs rather than runs (GitLab) surface one
    /// entry per artifact-bearing job with the driver's job-scoped
    /// artifact id encoding.
    /// </summary>
    Task<PlatformResult<IReadOnlyList<Artifact>>> ListRunArtifactsAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default);

    /// <summary>
    /// Download an artifact's bytes as a stream. Caller owns the
    /// stream and MUST dispose it.
    ///
    /// <para>Drivers SHOULD enforce a size cap (mirrors today's
    /// 4 MB cap on GitHub artifact downloads — see
    /// <c>OctokitGitHubActionsClient.DefaultMaxArtifactBytes</c>) and
    /// return <see cref="PlatformError.InvalidRequest"/> with code
    /// <c>"artifact_too_large"</c> rather than letting an attacker
    /// OOM the API.</para>
    /// </summary>
    Task<PlatformResult<Stream>> DownloadArtifactAsync(
        string owner, string repoName, string artifactId,
        CancellationToken ct = default);

    /// <summary>
    /// Cancel a running run. May be a no-op on completed runs;
    /// drivers return Ok in that case.
    /// </summary>
    Task<PlatformResult<bool>> CancelRunAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default);
}
