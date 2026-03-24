using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Tamma.ElsaServer;

/// <summary>
/// Background service that seeds workflow definitions from JSON files into ELSA
/// on startup. Reads all *.json files from /app/workflows/ (configurable via
/// ELSA_WORKFLOWS_DIR). Skips workflows that already exist in the database
/// unless ELSA_SEED_FORCE=true is set.
/// </summary>
public class WorkflowSeeder : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<WorkflowSeeder> _logger;

    public WorkflowSeeder(IConfiguration config, ILogger<WorkflowSeeder> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var workflowsDir = Environment.GetEnvironmentVariable("ELSA_WORKFLOWS_DIR")
                           ?? "/app/workflows";

        if (!Directory.Exists(workflowsDir))
        {
            _logger.LogInformation("No workflows directory at {Dir}, skipping seed", workflowsDir);
            return;
        }

        var files = Directory.GetFiles(workflowsDir, "*.json");
        if (files.Length == 0)
        {
            _logger.LogInformation("No workflow JSON files in {Dir}", workflowsDir);
            return;
        }

        var force = string.Equals(
            Environment.GetEnvironmentVariable("ELSA_SEED_FORCE"), "true",
            StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Seeding {Count} workflow(s) from {Dir} (force={Force})",
            files.Length, workflowsDir, force);

        var baseUrl = _config["Elsa:Server:BaseUrl"] ?? "http://localhost:5000";
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        if (!await WaitForHealthyAsync(client, ct))
        {
            _logger.LogError("ELSA server not healthy after timeout, skipping seed");
            return;
        }

        var token = await GetAccessTokenAsync(client, ct);
        if (token == null)
        {
            _logger.LogError("Failed to authenticate with ELSA, skipping seed");
            return;
        }

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        foreach (var file in files)
        {
            try
            {
                await SeedWorkflowAsync(client, file, force, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed workflow from {File}",
                    Path.GetFileName(file));
            }
        }

        _logger.LogInformation("Workflow seeding complete");
    }

    private async Task<bool> WaitForHealthyAsync(HttpClient client, CancellationToken ct)
    {
        const int maxAttempts = 30;
        const int delaySeconds = 2;

        for (var i = 1; i <= maxAttempts; i++)
        {
            try
            {
                var resp = await client.GetAsync("/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("ELSA healthy on attempt {I}/{Max}", i, maxAttempts);
                    return true;
                }
            }
            catch
            {
                // Server not ready yet
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }

        return false;
    }

    private async Task<string?> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        var username = _config["Elsa:Identity:AdminUser:Name"] ?? "admin";
        var password = _config["Elsa:Identity:AdminUser:Password"] ?? "password";

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/elsa/api/identity/login",
                new { username, password },
                ct);

            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

            if (body.TryGetProperty("accessToken", out var tokenProp))
                return tokenProp.GetString();

            // Some ELSA versions use different property names
            if (body.TryGetProperty("token", out var altProp))
                return altProp.GetString();

            _logger.LogWarning("Login response missing accessToken: {Body}",
                body.GetRawText());
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ELSA login failed");
            return null;
        }
    }

    private async Task SeedWorkflowAsync(
        HttpClient client, string filePath, bool force, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var json = await File.ReadAllTextAsync(filePath, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("definitionId", out var defIdProp))
        {
            _logger.LogWarning("Skipping {File}: missing 'definitionId'", fileName);
            return;
        }

        var definitionId = defIdProp.GetString()!;
        var name = root.TryGetProperty("name", out var n)
            ? n.GetString() ?? fileName
            : fileName;

        // Check if workflow already exists
        if (!force)
        {
            var checkResp = await client.GetAsync(
                $"/elsa/api/workflow-definitions/{definitionId}", ct);

            if (checkResp.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Workflow '{Name}' ({Id}) exists — skipping " +
                    "(set ELSA_SEED_FORCE=true to overwrite)",
                    name, definitionId);
                return;
            }
        }

        // Import via ELSA's workflow definitions API
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var importResp = await client.PostAsync(
            "/elsa/api/workflow-definitions/import", content, ct);

        if (importResp.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Seeded workflow '{Name}' ({Id}) from {File}",
                name, definitionId, fileName);
        }
        else
        {
            var respBody = await importResp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Import of '{Name}' returned HTTP {Status}: {Body}",
                name, (int)importResp.StatusCode, respBody);
        }
    }
}
