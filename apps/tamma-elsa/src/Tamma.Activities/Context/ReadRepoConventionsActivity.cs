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
/// Reads the repository's coding conventions from the engine callback API.
/// Calls GET {callbackUrl}/api/engine/repo-config?repo={repository}
/// and extracts the "conventions" field.
///
/// If no conventions are configured, returns an empty string — the
/// LlmCallWorkflow already handles empty conventions gracefully.
/// </summary>
[Activity(
    "Tamma.Context",
    "Read Repo Conventions",
    "Fetch project coding conventions from repo config for LLM prompt injection",
    Kind = ActivityKind.Task
)]
public class ReadRepoConventionsActivity : TammaAsyncActivity
{
    public override string? EventType => "CONTEXT.CONVENTIONS.READ";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Output(Description = "Coding conventions string for LLM prompt injection")]
    public Output<string> Conventions { get; set; } = default!;

    [JsonConstructor]
    public ReadRepoConventionsActivity() { }

    public ReadRepoConventionsActivity(
        ILogger<ReadRepoConventionsActivity> logger,
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
        var conventions = "";

        // 2026-08-13 (engine-driven E2E): store-rehydrated activities are built
        // by the [JsonConstructor] with NULL ctor-injected members — resolve
        // from the execution context (ctor-or-GetService idiom).
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();

        var callbackUrl = configuration?["Engine:CallbackUrl"];

        if (string.IsNullOrEmpty(callbackUrl) || httpClientFactory == null)
        {
            Logger?.LogWarning(
                "No Engine:CallbackUrl configured — cannot read repo conventions for {Repo}",
                repo);
            Conventions.Set(context, conventions);
            return;
        }

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            var url = $"{callbackUrl.TrimEnd('/')}/api/engine/repo-config?repo={Uri.EscapeDataString(repo)}";

            Logger?.LogInformation("Fetching repo config from {Url}", url);

            var response = await httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("conventions", out var conventionsElement)
                    && conventionsElement.ValueKind == JsonValueKind.String)
                {
                    conventions = conventionsElement.GetString() ?? "";
                }
            }
            else
            {
                Logger?.LogWarning(
                    "Failed to fetch repo config for {Repo}: HTTP {Status}",
                    repo, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error fetching repo conventions for {Repo}", repo);
            // Return empty conventions rather than failing the workflow
        }

        Conventions.Set(context, conventions);

        Logger?.LogInformation(
            "Repo conventions for {Repo}: {Length} chars",
            repo, conventions.Length);
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["conventionsLength"] = (this.GetOutput<string>(context, nameof(Conventions)) ?? "").Length,
    };
}
