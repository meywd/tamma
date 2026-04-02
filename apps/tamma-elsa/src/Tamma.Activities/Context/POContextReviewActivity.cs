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
/// PO reviews all role findings and produces:
/// - Summary of what's relevant for this work item
/// - Context IDs to fetch from vector DB (for downstream steps)
/// - Relevant links (PRs, docs, related issues)
///
/// Applies Minimum Viable Context — doses each downstream step
/// with only what it needs.
/// </summary>
[Activity(
    "Tamma.Context",
    "PO Context Review",
    "PO summarizes all role findings into actionable context",
    Kind = ActivityKind.Task
)]
public class POContextReviewActivity : TammaAsyncActivity
{
    public override string? EventType => "CONTEXT.PO.REVIEW";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Work item JSON")]
    public Input<string> WorkItemJson { get; set; } = default!;

    [Input(Description = "All role findings combined")]
    public Input<string> AllFindingsJson { get; set; } = default!;

    [Input(Description = "Context IDs from vector DB storage")]
    public Input<string> ContextIdsJson { get; set; } = default!;

    [Output(Description = "PO summary of the context")]
    public Output<string> Summary { get; set; } = default!;

    [Output(Description = "Relevant links as JSON array")]
    public Output<string> LinksJson { get; set; } = default!;

    [JsonConstructor]
    public POContextReviewActivity() { }

    public POContextReviewActivity(
        ILogger<POContextReviewActivity> logger,
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
        var workItemJson = WorkItemJson.Get(context);
        var allFindings = AllFindingsJson.Get(context);
        var contextIds = ContextIdsJson.Get(context);

        var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

        if (useMock)
        {
            Summary.Set(context, "Mock PO summary: work item requires changes to src/example.ts with existing test coverage. No security concerns. Follows repository pattern.");
            LinksJson.Set(context, "[]");
            return;
        }

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            Summary.Set(context, "No engine callback available for PO review.");
            LinksJson.Set(context, "[]");
            return;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();

            var prompt = $@"You are a Product Owner reviewing context gathered by a development team for a work item.

Work Item:
{workItemJson}

Findings from each role:
{allFindings}

Context IDs stored in vector DB:
{contextIds}

Produce a concise summary that:
1. What this work item needs (in plain language)
2. Key files and code areas involved
3. Existing test coverage status
4. Security considerations (if any)
5. Deployment/infrastructure impact (if any)
6. Architecture patterns to follow
7. Risks and concerns
8. Links to related PRs, issues, or documentation

Respond with JSON:
{{
  ""summary"": ""..."",
  ""links"": [""...""],
  ""scope"": ""what's in scope"",
  ""outOfScope"": ""what's NOT in scope"",
  ""risks"": [""...""]
}}";

            var response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task",
                new { prompt, role = "product_owner", repository = repo });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var output = result.TryGetProperty("output", out var o) ? o.GetString() : null;

                if (!string.IsNullOrEmpty(output))
                {
                    // Extract JSON
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var parsed = JsonSerializer.Deserialize<JsonElement>(output[jsonStart..(jsonEnd + 1)]);
                        Summary.Set(context, parsed.TryGetProperty("summary", out var s)
                            ? s.GetString() ?? "" : output);
                        LinksJson.Set(context, parsed.TryGetProperty("links", out var l)
                            ? l.GetRawText() : "[]");
                    }
                    else
                    {
                        Summary.Set(context, output);
                        LinksJson.Set(context, "[]");
                    }
                }
                else
                {
                    Summary.Set(context, "PO review returned no output.");
                    LinksJson.Set(context, "[]");
                }
            }
            else
            {
                Logger?.LogWarning("PO review returned {Status}", response.StatusCode);
                Summary.Set(context, "PO review failed.");
                LinksJson.Set(context, "[]");
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "PO context review failed");
            Summary.Set(context, $"PO review error: {ex.Message}");
            LinksJson.Set(context, "[]");
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["hasSummary"] = !string.IsNullOrEmpty(Summary.Get(context)),
    };
}
