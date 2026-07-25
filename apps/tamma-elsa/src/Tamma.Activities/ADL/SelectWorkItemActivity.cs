using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Selects the highest-priority work item from multiple sources:
/// 1. Security alerts (critical/high → urgent)
/// 2. Failed CI on main (→ urgent)
/// 3. Issues with tamma-auto label (priority from labels)
/// 4. Untriaged issues (→ trigger triage)
///
/// Uses priority config to map labels to priority levels.
///
/// Outcomes:
///   - Selected: a work item was found
///   - NothingFound: no work items available
///   - NeedsTriage: found untriaged issues that need classification
/// </summary>
[Activity(
    "Tamma.ADL",
    "Select Work Item",
    "Find the highest-priority work item from issues, alerts, and CI",
    Kind = ActivityKind.Task
)]
[FlowNode("Selected", "NothingFound", "NeedsTriage")]
public class SelectWorkItemActivity : TammaOutcomeActivity
{
    public override string? EventType => "ADL.WORKITEM.SELECT";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    // --- Inputs ---

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Labels that mark issues for Tamma")]
    public Input<string[]> AutoLabels { get; set; } = new(new[] { "tamma-auto" });

    [Input(Description = "Labels to exclude")]
    public Input<string[]> ExcludeLabels { get; set; } = new(new[] { "blocked", "wontfix", "needs-human" });

    [Input(Description = "Bot username for assignment")]
    public Input<string> BotAssignee { get; set; } = new("tamma-bot");

    // --- Outputs ---

    [Output(Description = "Selected work item as JSON")]
    public Output<string?> WorkItemJson { get; set; } = default!;

    [Output(Description = "Work item type: issue, security-alert, ci-failure")]
    public Output<string?> WorkItemType { get; set; } = default!;

    [Output(Description = "Issue number (if applicable)")]
    public Output<int> IssueNumber { get; set; } = default!;

    [Output(Description = "Work item title")]
    public Output<string?> WorkItemTitle { get; set; } = default!;

    [Output(Description = "Resolved priority: urgent, high, normal, low")]
    public Output<string> Priority { get; set; } = default!;

    [Output(Description = "Count of untriaged issues found")]
    public Output<int> UntriagedCount { get; set; } = default!;

    [JsonConstructor]
    public SelectWorkItemActivity() { }

    public SelectWorkItemActivity(
        ILogger<SelectWorkItemActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var repo = Repository.Get(context);
        var autoLabels = AutoLabels.Get(context);
        var excludeLabels = ExcludeLabels.Get(context);
        var bot = BotAssignee.Get(context);

        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

        List<WorkItem> candidates;
        int untriaged;

        if (useMock)
        {
            candidates = SimulateCandidates();
            untriaged = 0;
        }
        else
        {
            (candidates, untriaged) = await FetchCandidates(repo, autoLabels, excludeLabels, bot);
        }

        UntriagedCount.Set(context, untriaged);

        if (candidates.Count == 0)
        {
            if (untriaged > 0)
            {
                // No auto-labeled issues, but untriaged ones exist → triage them
                WorkItemJson.Set(context, null);
                WorkItemType.Set(context, null);
                WorkItemTitle.Set(context, null);
                Priority.Set(context, "normal");

                Logger?.LogInformation("No work items ready, but {Count} untriaged issues found", untriaged);
                await context.CompleteActivityWithOutcomesAsync("NeedsTriage");
                return;
            }

            WorkItemJson.Set(context, null);
            WorkItemType.Set(context, null);
            WorkItemTitle.Set(context, null);
            Priority.Set(context, "none");

            Logger?.LogInformation("No work items found — repo is clean");
            await context.CompleteActivityWithOutcomesAsync("NothingFound");
            return;
        }

        // Sort by priority (urgent first), then by age (oldest first)
        candidates.Sort((a, b) =>
        {
            var pComp = PriorityValue(a.Priority).CompareTo(PriorityValue(b.Priority));
            if (pComp != 0) return pComp;
            return a.CreatedAt.CompareTo(b.CreatedAt);
        });

        var selected = candidates[0];

        WorkItemJson.Set(context, JsonSerializer.Serialize(selected));
        WorkItemType.Set(context, selected.Type);
        IssueNumber.Set(context, selected.Number);
        WorkItemTitle.Set(context, selected.Title);
        Priority.Set(context, selected.Priority);

        Logger?.LogInformation(
            "Selected work item: #{Number} [{Type}] {Title} (priority: {Priority})",
            selected.Number, selected.Type, selected.Title, selected.Priority);

        await context.CompleteActivityWithOutcomesAsync("Selected");
    }

    private async Task<(List<WorkItem> candidates, int untriaged)> FetchCandidates(
        string repo, string[] autoLabels, string[] excludeLabels, string bot)
    {
        var candidates = new List<WorkItem>();
        var untriaged = 0;

        try
        {
            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            if (string.IsNullOrEmpty(callbackUrl))
            {
                Logger?.LogWarning("No Engine:CallbackUrl configured, using mock candidates");
                return (SimulateCandidates(), 0);
            }

            var httpClient = _httpClientFactory!.CreateClient();

            // Fetch issues via engine callback
            var response = await httpClient.GetAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/issues?repo={Uri.EscapeDataString(repo)}&labels={string.Join(",", autoLabels)}");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var issues = JsonSerializer.Deserialize<EngineIssuesResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Issues;

                if (issues != null)
                {
                    foreach (var issue in issues)
                    {
                        // Skip excluded labels
                        if (issue.Labels.Any(l => excludeLabels.Contains(l)))
                            continue;
                        // Skip already assigned to someone else
                        if (!string.IsNullOrEmpty(issue.Assignee) && issue.Assignee != bot)
                            continue;

                        // Resolve priority from labels
                        issue.Priority = ResolvePriority(issue.Labels);
                        issue.Type = "issue";
                        candidates.Add(issue);
                    }
                }
            }

            // Check for untriaged issues (no priority label, no type label)
            var triagedLabels = new HashSet<string>(autoLabels.Concat(new[] {
                "bug", "feature", "chore", "question", "security", "docs",
                "priority-critical", "priority-high", "priority-medium", "priority-low"
            }));

            var untriagedResponse = await httpClient.GetAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/issues?repo={Uri.EscapeDataString(repo)}&state=open");

            if (untriagedResponse.IsSuccessStatusCode)
            {
                var json = await untriagedResponse.Content.ReadAsStringAsync();
                var allIssues = JsonSerializer.Deserialize<EngineIssuesResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Issues;

                if (allIssues != null)
                {
                    untriaged = allIssues.Count(i => !i.Labels.Any(l => triagedLabels.Contains(l)));
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error fetching candidates from engine");
        }

        return (candidates, untriaged);
    }

    private static string ResolvePriority(List<string> labels)
    {
        if (labels.Contains("priority-critical") || labels.Contains("security")) return "urgent";
        if (labels.Contains("priority-high") || labels.Contains("bug")) return "high";
        if (labels.Contains("priority-low")) return "low";
        return "normal";
    }

    private static int PriorityValue(string priority) => priority switch
    {
        "urgent" => 0,
        "high" => 1,
        "normal" => 2,
        "low" => 3,
        _ => 2,
    };

    private static List<WorkItem> SimulateCandidates() => new()
    {
        new WorkItem
        {
            Number = 1,
            Title = "[Mock] Sample issue for autonomous development",
            Type = "issue",
            Priority = "normal",
            Labels = new() { "tamma-auto" },
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        }
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["workItemType"] = this.GetOutput<string?>(context, nameof(WorkItemType)),
        ["issueNumber"] = this.GetOutput<int>(context, nameof(IssueNumber)),
        ["priority"] = this.GetOutput<string>(context, nameof(Priority)),
        ["untriagedCount"] = this.GetOutput<int>(context, nameof(UntriagedCount)),
    };
}

/// <summary>
/// The response envelope of <c>GET /api/engine/issues</c>.
///
/// <para><b>Why this type exists.</b> The endpoint returns
/// <c>{ issues, total }</c> (<c>EngineEndpoints.GetIssues</c>,
/// <c>Results.Ok(new { issues = r.Issues, total = r.Total })</c>) — an OBJECT.
/// Both engine-side callers used to deserialize the body straight into
/// <c>List&lt;WorkItem&gt;</c>, which throws <see cref="JsonException"/> against an
/// object. In <see cref="SelectWorkItemActivity"/> that throw was caught by a
/// broad <c>catch (Exception)</c> that only logged, so the real (non-mock) intake
/// path silently returned zero candidates and every run reported
/// <c>NothingFound</c>. Deserializing the envelope is the fix; keeping it as a
/// named type is what stops the next caller repeating it.</para>
/// </summary>
internal sealed class EngineIssuesResponse
{
    [JsonPropertyName("issues")]
    public List<WorkItem> Issues { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// Represents a work item from any source (issue, alert, CI failure, stale PR).
/// </summary>
public class WorkItem
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string Type { get; set; } = "issue";
    public string Priority { get; set; } = "normal";
    public List<string> Labels { get; set; } = new();
    public string? Assignee { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object?> Metadata { get; set; } = new();
}
