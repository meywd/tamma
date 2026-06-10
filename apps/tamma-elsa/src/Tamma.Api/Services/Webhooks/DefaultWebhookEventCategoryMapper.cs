using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks;

/// <summary>
/// Story 31-7 — production category mapper. The table is intentionally
/// terse; new event types append cleanly without disturbing existing
/// rows.
/// </summary>
public sealed class DefaultWebhookEventCategoryMapper : IWebhookEventCategoryMapper
{
    public WebhookEventCategory MapCategory(PlatformKind kind, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return WebhookEventCategory.Unknown;

        return kind switch
        {
            PlatformKind.GitHub => MapGitHub(eventType),
            PlatformKind.Gitea or PlatformKind.Forgejo => MapGitea(eventType),
            PlatformKind.GitLab => MapGitLab(eventType),
            _ => WebhookEventCategory.Unknown,
        };
    }

    private static WebhookEventCategory MapGitHub(string et) => et switch
    {
        "installation" or "installation_repositories" => WebhookEventCategory.Installation,
        "pull_request" or "pull_request_review"
            or "pull_request_review_comment" => WebhookEventCategory.PullRequest,
        "issues" or "issue_comment" => WebhookEventCategory.Issue,
        "push" => WebhookEventCategory.Push,
        "workflow_run" or "workflow_job" => WebhookEventCategory.WorkflowRun,
        "ping" => WebhookEventCategory.Ping,
        _ => WebhookEventCategory.Unknown,
    };

    private static WebhookEventCategory MapGitea(string et) => et switch
    {
        "create" or "delete" or "repository" => WebhookEventCategory.Installation,
        "pull_request" or "pull_request_comment"
            or "pull_request_review" => WebhookEventCategory.PullRequest,
        "issues" or "issue_comment" => WebhookEventCategory.Issue,
        "push" => WebhookEventCategory.Push,
        "workflow_run" or "workflow_job" => WebhookEventCategory.WorkflowRun,
        "ping" => WebhookEventCategory.Ping,
        _ => WebhookEventCategory.Unknown,
    };

    private static WebhookEventCategory MapGitLab(string et) => et switch
    {
        "system_hook" or "project" or "group" => WebhookEventCategory.Installation,
        "merge_request" => WebhookEventCategory.PullRequest,
        "issue" or "note" => WebhookEventCategory.Issue,
        "push" or "tag_push" => WebhookEventCategory.Push,
        "pipeline" or "build" or "job" => WebhookEventCategory.WorkflowRun,
        _ => WebhookEventCategory.Unknown,
    };
}
