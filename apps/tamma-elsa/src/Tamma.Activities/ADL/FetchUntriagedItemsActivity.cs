using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Fetches untriaged items from multiple sources:
/// 1. Open issues with no triage labels
/// 2. Dependabot alerts (unaddressed)
/// 3. CodeQL alerts (open)
/// 4. Security advisories
///
/// An item is "untriaged" if it has none of the triage labels
/// (bug, feature, chore, priority-*, tamma-auto, etc.)
/// </summary>
[Activity(
    "Tamma.ADL",
    "Fetch Untriaged Items",
    "Fetch issues, Dependabot alerts, CodeQL findings that need triage",
    Kind = ActivityKind.Task
)]
public class FetchUntriagedItemsActivity : TammaAsyncActivity
{
    public override string? EventType => "TRIAGE.FETCH.ITEMS";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    private static readonly HashSet<string> TriageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "bug", "feature", "chore", "question", "security", "docs",
        "priority-critical", "priority-high", "priority-medium", "priority-low",
        "tamma-auto", "tamma-assist", "needs-human",
        "complexity-trivial", "complexity-simple", "complexity-medium",
        "complexity-complex", "complexity-epic",
        "tamma-processing", "tamma-completed", "tamma-error",
    };

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Output(Description = "Untriaged items as JSON array")]
    public Output<string> ItemsJson { get; set; } = default!;

    [Output(Description = "Total count of untriaged items")]
    public Output<int> TotalCount { get; set; } = default!;

    [JsonConstructor]
    public FetchUntriagedItemsActivity() { }

    public FetchUntriagedItemsActivity(
        ILogger<FetchUntriagedItemsActivity> logger,
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
        var items = new List<TriageItem>();

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

        if (useMock)
        {
            items = SimulateItems();
        }
        else if (!string.IsNullOrEmpty(callbackUrl) && _httpClientFactory != null)
        {
            var httpClient = _httpClientFactory.CreateClient();

            // Fetch open issues
            try
            {
                var response = await httpClient.GetAsync(
                    $"{callbackUrl.TrimEnd('/')}/api/engine/issues?repo={Uri.EscapeDataString(repo)}&state=open");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var issues = JsonSerializer.Deserialize<EngineIssuesResponse>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Issues;
                    if (issues != null)
                    {
                        foreach (var issue in issues)
                        {
                            if (!issue.Labels.Any(l => TriageLabels.Contains(l)))
                            {
                                items.Add(new TriageItem
                                {
                                    Type = "issue",
                                    Number = issue.Number,
                                    Title = issue.Title,
                                    Body = issue.Body,
                                    Labels = issue.Labels,
                                    Url = issue.Url,
                                    Source = "github-issues",
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to fetch issues");
            }

            // Fetch Dependabot alerts
            try
            {
                var response = await httpClient.GetAsync(
                    $"{callbackUrl.TrimEnd('/')}/api/engine/security-alerts?repo={Uri.EscapeDataString(repo)}&type=dependabot");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var alerts = JsonSerializer.Deserialize<List<SecurityAlert>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (alerts != null)
                    {
                        foreach (var alert in alerts)
                        {
                            items.Add(new TriageItem
                            {
                                Type = "dependabot-alert",
                                Title = $"[Dependabot] {alert.Package}: {alert.Title}",
                                Body = $"CVE: {alert.CveId}\nSeverity: {alert.Severity}\nPackage: {alert.Package}@{alert.CurrentVersion}\nFix: {alert.FixVersion}\n\n{alert.Description}",
                                Source = "dependabot",
                                Severity = alert.Severity,
                                Metadata = new()
                                {
                                    ["package"] = alert.Package,
                                    ["currentVersion"] = alert.CurrentVersion,
                                    ["fixVersion"] = alert.FixVersion,
                                    ["cveId"] = alert.CveId,
                                },
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to fetch Dependabot alerts");
            }

            // Fetch CodeQL alerts
            try
            {
                var response = await httpClient.GetAsync(
                    $"{callbackUrl.TrimEnd('/')}/api/engine/security-alerts?repo={Uri.EscapeDataString(repo)}&type=codeql");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var alerts = JsonSerializer.Deserialize<List<CodeQLAlert>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (alerts != null)
                    {
                        foreach (var alert in alerts)
                        {
                            items.Add(new TriageItem
                            {
                                Type = "codeql-alert",
                                Title = $"[CodeQL] {alert.Rule}: {alert.Description}",
                                Body = $"Rule: {alert.Rule}\nSeverity: {alert.Severity}\nFile: {alert.FilePath}:{alert.Line}\n\n{alert.Description}",
                                Source = "codeql",
                                Severity = alert.Severity,
                                Metadata = new()
                                {
                                    ["rule"] = alert.Rule,
                                    ["filePath"] = alert.FilePath,
                                    ["line"] = alert.Line,
                                },
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to fetch CodeQL alerts");
            }
        }

        var itemsJsonStr = JsonSerializer.Serialize(items);
        ItemsJson.Set(context, itemsJsonStr);
        TotalCount.Set(context, items.Count);

        Logger?.LogInformation("Found {Count} untriaged items ({Issues} issues, {Deps} dependabot, {CQ} codeql)",
            items.Count,
            items.Count(i => i.Type == "issue"),
            items.Count(i => i.Type == "dependabot-alert"),
            items.Count(i => i.Type == "codeql-alert"));
    }

    private static List<TriageItem> SimulateItems() => new()
    {
        new TriageItem
        {
            Type = "issue",
            Number = 999,
            Title = "[Mock] Untriaged issue for testing",
            Body = "This is a mock untriaged issue.",
            Source = "github-issues",
        }
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["totalCount"] = this.GetOutput<int>(context, nameof(TotalCount)),
    };
}

public class TriageItem
{
    public string Type { get; set; } = "issue";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public List<string> Labels { get; set; } = new();
    public string? Url { get; set; }
    public string Source { get; set; } = "";
    public string? Severity { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public class SecurityAlert
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Severity { get; set; } = "medium";
    public string Package { get; set; } = "";
    public string? CurrentVersion { get; set; }
    public string? FixVersion { get; set; }
    public string? CveId { get; set; }
}

public class CodeQLAlert
{
    public string Rule { get; set; } = "";
    public string? Description { get; set; }
    public string Severity { get; set; } = "warning";
    public string? FilePath { get; set; }
    public int? Line { get; set; }
}
