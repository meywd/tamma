using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.Context;

/// <summary>
/// Role-based codebase scan. An LLM with a specific role scans the
/// codebase using tools (file read, search, git log) and returns findings.
///
/// If the role finds nothing relevant, returns empty findings (skip).
/// Each role receives previous roles' findings to build on.
/// </summary>
[Activity(
    "Tamma.Context",
    "Role Scan",
    "LLM scans codebase from a specific role perspective",
    Kind = ActivityKind.Task
)]
public class RoleScanActivity : TammaAsyncActivity
{
    public override string? EventType => $"CONTEXT.SCAN";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "LLM role: developer, tester, security, devops, architect")]
    public Input<string> Role { get; set; } = default!;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Work item JSON")]
    public Input<string> WorkItemJson { get; set; } = default!;

    [Input(Description = "Work item type: feature, bug, security, test, docs, chore")]
    public Input<string> WorkItemType { get; set; } = default!;

    [Input(Description = "Previous roles' findings JSON")]
    public Input<string> PreviousFindingsJson { get; set; } = new("{}");

    [Input(Description = "Specific scan instructions for this role")]
    public Input<string> ScanPrompt { get; set; } = default!;

    [Output(Description = "This role's findings as JSON")]
    public Output<string> FindingsJson { get; set; } = default!;

    [JsonConstructor]
    public RoleScanActivity() { }

    public RoleScanActivity(
        ILogger<RoleScanActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var role = Role.Get(context);
        var repo = Repository.Get(context);
        var workItemJson = WorkItemJson.Get(context);
        var workItemType = WorkItemType.Get(context);
        var previousFindings = PreviousFindingsJson.Get(context);
        var scanPrompt = ScanPrompt.Get(context);

        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

        if (useMock)
        {
            FindingsJson.Set(context, SimulateFindings(role));
            return;
        }

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            Logger?.LogWarning("No Engine:CallbackUrl, using mock for {Role} scan", role);
            FindingsJson.Set(context, SimulateFindings(role));
            return;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();

            var prompt = BuildPrompt(role, workItemJson, workItemType, previousFindings, scanPrompt);

            var response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
                new
                {
                    prompt,
                    role,
                    repository = repo,
                    enableTools = true, // allow file read, search, git log
                });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var output = result.TryGetProperty("output", out var o) ? o.GetString() : null;

                if (!string.IsNullOrEmpty(output))
                {
                    // Try to extract JSON from the response
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        FindingsJson.Set(context, output[jsonStart..(jsonEnd + 1)]);
                    }
                    else
                    {
                        FindingsJson.Set(context, JsonSerializer.Serialize(new { raw = output }));
                    }
                }
                else
                {
                    FindingsJson.Set(context, "{}");
                }
            }
            else
            {
                Logger?.LogWarning("{Role} scan returned {Status}", role, response.StatusCode);
                FindingsJson.Set(context, "{}");
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "{Role} scan failed", role);
            FindingsJson.Set(context, "{}");
        }

        Logger?.LogInformation("{Role} scan complete for {WorkItemType}", role, workItemType);
    }

    private static string BuildPrompt(string role, string workItemJson, string workItemType,
        string previousFindings, string scanPrompt)
    {
        return $@"You are a {role} reviewing a codebase for a {workItemType} work item.

Work Item:
{workItemJson}

Previous roles' findings:
{previousFindings}

Your task:
{scanPrompt}

If you find nothing relevant from your perspective, respond with empty JSON: {{}}

Otherwise, respond with a JSON object containing your findings. Structure depends on your role:
- developer: {{ ""files"": [...], ""snippets"": [...], ""dependencies"": [...], ""patterns"": [...] }}
- tester: {{ ""existingTests"": [...], ""coverageGaps"": [...], ""testPatterns"": [...], ""fixtures"": [...] }}
- security: {{ ""concerns"": [...], ""inputValidation"": [...], ""authChecks"": [...] }}
- devops: {{ ""configs"": [...], ""ciImpact"": [...], ""envVars"": [...] }}
- architect: {{ ""patterns"": [...], ""conventions"": [...], ""interfaces"": [...], ""boundaries"": [...] }}

Use the available tools (file_read, search_code, git_log) to scan the codebase. Be thorough but focused.";
    }

    private static string SimulateFindings(string role) => role switch
    {
        "developer" => JsonSerializer.Serialize(new
        {
            files = new[] { "src/example.ts" },
            snippets = new[] { "// mock code snippet" },
            dependencies = Array.Empty<string>(),
            patterns = new[] { "repository pattern" },
        }),
        "tester" => JsonSerializer.Serialize(new
        {
            existingTests = new[] { "src/example.test.ts" },
            coverageGaps = new[] { "no edge case tests" },
            testPatterns = new[] { "vitest + vi.mock" },
        }),
        _ => "{}",
    };

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["role"] = Role.Get(context),
        ["workItemType"] = WorkItemType.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["role"] = Role.Get(context),
        ["hasFindings"] = FindingsJson.Get(context) != "{}",
    };
}
