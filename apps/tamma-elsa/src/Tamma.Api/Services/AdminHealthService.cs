using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;

namespace Tamma.Api.Services;

/// <summary>
/// Per-service health probe envelope for the admin dashboard. Mirrors the
/// TS <c>ServiceCheck</c> shape (snake_case JSON via System.Text.Json camelCase
/// default). <c>Status</c> is one of <c>healthy | unhealthy | unknown</c>.
/// </summary>
public record ServiceCheck(
    string Name,
    string Status,
    long ResponseTime,
    string CheckedAt,
    string? Details = null);

public record AdminHealthResponse(IReadOnlyList<ServiceCheck> Services, string CheckedAt);

public interface IAdminHealthService
{
    Task<AdminHealthResponse> GetHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Aggregates infrastructure health probes for the admin dashboard. Mirrors
/// the TS <c>health-routes.ts</c> behavior: pings six services in parallel
/// (self / Postgres / ELSA / OpenSearch / RabbitMQ / ChromaDB) with a 5s
/// timeout per probe, catches per-service failures so one outage doesn't
/// poison the others, and serializes a flat <c>ServiceCheck[]</c> envelope.
/// Probes that have no configured URL report <c>status="unknown"</c>.
/// </summary>
public class AdminHealthService : IAdminHealthService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ControlPlaneDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public AdminHealthService(ControlPlaneDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public async Task<AdminHealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        var probes = new List<Task<ServiceCheck>>
        {
            Task.FromResult(new ServiceCheck(
                "Tamma API", "healthy", 0, NowIso())),
            CheckPostgresAsync(ct),
            CheckHttpServiceAsync("ELSA Server",
                Combine(_config["Elsa:ServerUrl"], "/health"), null, ct),
            CheckHttpServiceAsync("OpenSearch",
                Combine(_config["OpenSearch:Url"], "/_cluster/health"), null, ct),
            CheckHttpServiceAsync("RabbitMQ",
                Combine(_config["RabbitMQ:ManagementUrl"], "/api/health/checks/alarms"),
                BasicAuth(_config["RabbitMQ:User"], _config["RabbitMQ:Password"]), ct),
            CheckHttpServiceAsync("ChromaDB",
                Combine(_config["ChromaDb:Url"], "/api/v2/heartbeat"), null, ct),
        };

        var results = await Task.WhenAll(probes);
        return new AdminHealthResponse(results, NowIso());
    }

    private async Task<ServiceCheck> CheckPostgresAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cts.Token);
            sw.Stop();
            return new ServiceCheck("PostgreSQL", "healthy", sw.ElapsedMilliseconds, NowIso());
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ServiceCheck("PostgreSQL", "unhealthy", sw.ElapsedMilliseconds, NowIso(), ex.Message);
        }
    }

    private async Task<ServiceCheck> CheckHttpServiceAsync(
        string name, string? url, string? authorization, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
            return new ServiceCheck(name, "unknown", 0, NowIso(), "URL not configured");

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(authorization))
                req.Headers.TryAddWithoutValidation("Authorization", authorization);

            var http = _httpClientFactory.CreateClient();
            using var res = await http.SendAsync(req, cts.Token);
            sw.Stop();

            if (res.IsSuccessStatusCode)
                return new ServiceCheck(name, "healthy", sw.ElapsedMilliseconds, NowIso());

            return new ServiceCheck(name, "unhealthy", sw.ElapsedMilliseconds, NowIso(),
                $"HTTP {(int)res.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ServiceCheck(name, "unhealthy", sw.ElapsedMilliseconds, NowIso(),
                $"Timed out after {ProbeTimeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ServiceCheck(name, "unhealthy", sw.ElapsedMilliseconds, NowIso(), ex.Message);
        }
    }

    private static string NowIso() => DateTime.UtcNow.ToString("o");

    private static string? Combine(string? baseUrl, string path)
    {
        if (string.IsNullOrEmpty(baseUrl)) return null;
        var trimmed = baseUrl.TrimEnd('/');
        return $"{trimmed}{path}";
    }

    private static string? BasicAuth(string? user, string? password)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password)) return null;
        var raw = $"{user}:{password}";
        return $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))}";
    }
}
