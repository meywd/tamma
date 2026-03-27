using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.CodeIndex;

/// <summary>
/// Fire-and-forget activity that triggers an incremental code index update
/// via the KB API after code changes are committed.
/// On ANY failure the activity logs a warning and completes normally —
/// it must never fail the parent workflow.
/// </summary>
[Activity(
    "Tamma.CodeIndex",
    "Update Code Index",
    "Trigger incremental vector-index update for changed files",
    Kind = ActivityKind.Task
)]
public class UpdateCodeIndexActivity : CodeActivity
{
    [Input(Description = "JSON array of changed file paths (may be null)")]
    public Input<string?> ChangedFilesJson { get; set; } = default!;

    [Input(Description = "Repository URL or path")]
    public Input<string?> RepositoryPath { get; set; } = default!;

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<UpdateCodeIndexActivity>? _logger;

    [JsonConstructor]
    public UpdateCodeIndexActivity() : this(null, null, null)
    {
    }

    public UpdateCodeIndexActivity(
        ILogger<UpdateCodeIndexActivity>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        try
        {
            var changedFilesRaw = ChangedFilesJson.Get(context);
            var repositoryPath = RepositoryPath.Get(context);

            List<string>? changedFiles = null;
            if (!string.IsNullOrWhiteSpace(changedFilesRaw))
            {
                try
                {
                    changedFiles = JsonSerializer.Deserialize<List<string>>(changedFilesRaw);
                }
                catch
                {
                    _logger?.LogWarning(
                        "UpdateCodeIndex: Failed to parse ChangedFilesJson, will trigger full incremental index");
                }
            }

            var fileCount = changedFiles?.Count ?? 0;
            _logger?.LogInformation(
                "UpdateCodeIndex: Triggering index for {FileCount} changed files (repo: {Repo})",
                fileCount, repositoryPath ?? "(default)");

            var kbApiUrl = _configuration?["Tamma:KbApiUrl"]
                           ?? _configuration?["Cors:ApiUrl"]
                           ?? "http://localhost:3000";

            var httpClient = _httpClientFactory?.CreateClient("kb-index") ?? new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var payload = new Dictionary<string, object?>();
            if (changedFiles != null && changedFiles.Count > 0)
            {
                payload["changedFiles"] = changedFiles;
            }
            if (!string.IsNullOrWhiteSpace(repositoryPath))
            {
                payload["repositoryPath"] = repositoryPath;
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{kbApiUrl.TrimEnd('/')}/api/knowledge-base/index/trigger";
            var response = await httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation("UpdateCodeIndex: Index trigger accepted (HTTP {StatusCode})",
                    (int)response.StatusCode);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning(
                    "UpdateCodeIndex: Index trigger returned HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "UpdateCodeIndex: Failed to trigger index update — continuing workflow");
        }
    }
}
