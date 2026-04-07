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
/// Stores a single role's findings in the vector DB immediately after extraction.
/// Returns a context ID for this role's entry. This allows partial results to
/// persist even if later scans fail.
/// </summary>
[Activity(
    "Tamma.Context",
    "Store Role Finding",
    "Store one role's scan findings in vector DB",
    Kind = ActivityKind.Task
)]
public class StoreRoleFindingActivity : TammaAsyncActivity
{
    public override string? EventType => "CONTEXT.STORE_ROLE";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Role that produced the findings (e.g. developer, tester, security)")]
    public Input<string> Role { get; set; } = default!;

    [Input(Description = "Findings JSON from this role's scan")]
    public Input<string> FindingsJson { get; set; } = default!;

    [Output(Description = "Context ID for this role's stored finding")]
    public Output<string> ContextId { get; set; } = default!;

    [JsonConstructor]
    public StoreRoleFindingActivity() { }

    public StoreRoleFindingActivity(
        ILogger<StoreRoleFindingActivity> logger,
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
        var role = Role.Get(context);
        var findings = FindingsJson.Get(context);

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            // Mock: return fake context ID
            ContextId.Set(context, $"ctx:{issueNum}:{role}");
            return;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();

            var response = await httpClient.PostAsJsonAsync(
                $"{callbackUrl.TrimEnd('/')}/api/engine/store-context",
                new
                {
                    repository = repo,
                    issueNumber = issueNum,
                    findings = new Dictionary<string, string>
                    {
                        [role] = findings,
                    },
                });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (result.TryGetProperty("contextIds", out var c) && c.GetArrayLength() > 0)
                {
                    ContextId.Set(context, c[0].GetString() ?? $"ctx:{issueNum}:{role}");
                }
                else
                {
                    ContextId.Set(context, $"ctx:{issueNum}:{role}");
                }
            }
            else
            {
                Logger?.LogWarning("Store role finding returned {Status} for {Role}", response.StatusCode, role);
                ContextId.Set(context, "");
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to store {Role} findings in vector DB", role);
            ContextId.Set(context, "");
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["role"] = Role.Get(context),
    };
}
