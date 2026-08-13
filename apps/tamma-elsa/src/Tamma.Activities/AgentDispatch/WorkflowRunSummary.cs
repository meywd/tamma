namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// The engine-side workflow-run projection <c>AgentMonitorService</c> loops
/// over (built from the mediated <c>GET /api/v1/agent-dispatch/.../runs[/{id}]</c>
/// responses). Epic 31 P3 (4/4): moved out of the deleted
/// <c>IGitHubActionsClient.cs</c> — the record is engine-plane wire data, not a
/// platform client surface; the GitHub-only Actions seam it used to ride is
/// gone (the API's mediation planes speak the platform driver abstraction).
/// </summary>
public sealed record WorkflowRunSummary(
    long Id,
    string Status,
    string Conclusion,
    string HtmlUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string HeadBranch,
    string Event,
    string ArtifactsUrl);
