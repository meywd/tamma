using System.Net.Http.Json;
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
/// Stores all role findings in the vector DB, keyed by issue/cycle ID.
/// Returns context IDs that downstream steps use to fetch relevant chunks.
/// </summary>
[Activity(
    "Tamma.Context",
    "Store Findings",
    "Store role scan findings in vector DB for downstream retrieval",
    Kind = ActivityKind.Task
)]
public class StoreFindingsActivity : TammaAsyncActivity
{
    public override string? EventType => "CONTEXT.STORE";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Dev findings JSON")]
    public Input<string> DevFindingsJson { get; set; } = default!;

    [Input(Description = "QA findings JSON")]
    public Input<string> QAFindingsJson { get; set; } = default!;

    [Input(Description = "Security findings JSON")]
    public Input<string> SecurityFindingsJson { get; set; } = default!;

    [Input(Description = "DevOps findings JSON")]
    public Input<string> DevOpsFindingsJson { get; set; } = default!;

    [Input(Description = "Architect findings JSON")]
    public Input<string> ArchitectFindingsJson { get; set; } = default!;

    [Output(Description = "Context IDs for vector DB retrieval")]
    public Output<string> ContextIdsJson { get; set; } = default!;

    [JsonConstructor]
    public StoreFindingsActivity() { }

    public StoreFindingsActivity(
        ILogger<StoreFindingsActivity> logger,
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
        var issueNum = IssueNumber.Get(context);

        // 2026-08-13 (engine-driven E2E): store-rehydrated activities are built
        // by the [JsonConstructor] with NULL ctor-injected members — resolve
        // from the execution context (ctor-or-GetService idiom).
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();

        var callbackUrl = configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || httpClientFactory == null)
        {
            // Mock: return fake context IDs
            ContextIdsJson.Set(context, JsonSerializer.Serialize(new[]
            {
                $"ctx:{issueNum}:dev",
                $"ctx:{issueNum}:qa",
                $"ctx:{issueNum}:architect",
            }));
            return;
        }

        try
        {
            var httpClient = httpClientFactory.CreateClient();

            var findings = new Dictionary<string, string>
            {
                ["dev"] = DevFindingsJson.Get(context),
                ["qa"] = QAFindingsJson.Get(context),
                ["security"] = SecurityFindingsJson.Get(context),
                ["devops"] = DevOpsFindingsJson.Get(context),
                ["architect"] = ArchitectFindingsJson.Get(context),
            };

            var response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/store-context",
                new
                {
                    repository = repo,
                    issueNumber = issueNum,
                    findings,
                });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var ids = result.TryGetProperty("contextIds", out var c)
                    ? c.GetRawText()
                    : "[]";
                ContextIdsJson.Set(context, ids);
            }
            else
            {
                Logger?.LogWarning("Store findings returned {Status}", response.StatusCode);
                ContextIdsJson.Set(context, "[]");
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to store findings in vector DB");
            ContextIdsJson.Set(context, "[]");
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
    };
}
