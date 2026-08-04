using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Central catalogue of <c>ISSUES.CREATE*</c> DCB event types emitted by the
/// Story 40-8 <c>create-issues</c> workflow via <see cref="CreateIssuesActivity"/>
/// (the defer/split triage-outcome tail of <c>SingleIssueCycleWorkflow</c>).
///
/// <para>New-but-minimal vocabulary (40-8 D5): the engine create-issue route
/// itself emits NO events (verified <c>EngineEndpoints.CreateIssue</c> /
/// <c>OctokitGitHubEngineCallbackService.CreateIssueAsync</c>) and no
/// issue-created family exists anywhere (<c>GitEventTypes</c> is
/// <c>GIT.ISSUE_UPDATED.*</c> only), so AC5's per-item audit trail rides the
/// EXISTING drain (<c>TammaEventEmitter</c> → <c>EventPersistenceMiddleware</c>)
/// with the existing <c>AGGREGATE.ACTION.STATUS</c> grammar. Mirrors
/// <see cref="IssueStatusEvents"/>.</para>
/// </summary>
public static class IssuesCreateEvents
{
    /// <summary>Batch started (one per activity run; data: repository, itemCount).</summary>
    public const string BatchStarted = "ISSUES.CREATE.STARTED";

    /// <summary>Batch finished (one per activity run; data: created/failed/skipped counts + warnings).</summary>
    public const string BatchCompleted = "ISSUES.CREATE.COMPLETED";

    /// <summary>One platform issue created (one per item — AC5).</summary>
    public const string ItemSuccess = "ISSUES.CREATE_ITEM.SUCCESS";

    /// <summary>One item's create POST failed (loud, per item; the batch continues).</summary>
    public const string ItemFailed = "ISSUES.CREATE_ITEM.FAILED";

    /// <summary>One item intentionally not created (duplicate title / invalid draft) — recorded, never silent.</summary>
    public const string ItemSkipped = "ISSUES.CREATE_ITEM.SKIPPED";
}

/// <summary>Result of one issue-create engine-callback POST. <see cref="Success"/>
/// mirrors <c>HttpResponseMessage.IsSuccessStatusCode</c>; <see cref="IssueNumber"/>
/// is the created platform issue number (0 on failure).</summary>
public readonly record struct IssueCreateResult(bool Success, int StatusCode, int IssueNumber)
{
    public static IssueCreateResult Ok(int issueNumber) => new(true, 201, issueNumber);
    public static IssueCreateResult Fail(int statusCode) => new(false, statusCode, 0);
}

/// <summary>One existing platform issue, as returned by the engine list route —
/// the dedupe read (40-8 D3).</summary>
public sealed record ExistingIssueRef(int Number, string Title, string State);

/// <summary>
/// Injectable seam for the issue-create engine callbacks
/// (<c>POST /api/engine/create-issue</c> + <c>GET /api/engine/issues</c>), so
/// <see cref="CreateIssuesActivity.CreateIssuesCoreAsync"/> is unit testable without a
/// live HTTP server. The <c>ITriageApplyClient</c>/<c>HttpTriageApplyClient</c> idiom
/// (the existing activity-side client for this same route — 40-8 D2: deliberately NOT
/// a <c>TammaApiClient</c> method, which would require the <c>ExternalEffect</c> key
/// Story 31-13 owns).
/// </summary>
public interface IIssueCreateClient
{
    Task<IssueCreateResult> CreateIssueAsync(
        string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct);

    /// <summary>One page of the repo's issues (any state). Used for the
    /// platform-side dedupe; page is 1-based.</summary>
    Task<IReadOnlyList<ExistingIssueRef>> ListIssuesAsync(
        string repository, int page, int perPage, CancellationToken ct);
}

/// <summary>Default <see cref="IIssueCreateClient"/> over the engine-callback HTTP API.
/// <paramref name="correlationId"/> (optional) travels as <c>X-Tamma-Correlation-Id</c>
/// — the 43-14 grant-threading seam (40-8 D9), unbound until 43-14 decides the wiring.</summary>
internal sealed class HttpIssueCreateClient : IIssueCreateClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _correlationId;

    public HttpIssueCreateClient(HttpClient http, string baseUrl, string? correlationId = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _correlationId = correlationId;
    }

    public async Task<IssueCreateResult> CreateIssueAsync(
        string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/api/engine/create-issue")
        {
            Content = JsonContent.Create(new { repository, title, body, labels }),
        };
        AddCorrelation(request);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return IssueCreateResult.Fail((int)response.StatusCode);

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<CreatedIssuePayload>(cancellationToken: ct);
            return IssueCreateResult.Ok(payload?.Number ?? 0);
        }
        catch (JsonException)
        {
            // Created but the body didn't parse — still a success, number unknown.
            return IssueCreateResult.Ok(0);
        }
    }

    public async Task<IReadOnlyList<ExistingIssueRef>> ListIssuesAsync(
        string repository, int page, int perPage, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/engine/issues" +
                  $"?repo={Uri.EscapeDataString(repository)}&state=all&per_page={perPage}&page={page}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCorrelation(request);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ListIssuesPayload>(cancellationToken: ct);
        return (IReadOnlyList<ExistingIssueRef>?)payload?.Issues
            ?.Select(i => new ExistingIssueRef(i.Number, i.Title ?? "", i.State ?? ""))
            .ToList() ?? Array.Empty<ExistingIssueRef>();
    }

    private void AddCorrelation(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_correlationId))
            request.Headers.TryAddWithoutValidation("X-Tamma-Correlation-Id", _correlationId);
    }

    private sealed record CreatedIssuePayload(int Number, string? HtmlUrl, string? Title);
    private sealed record ListedIssuePayload(int Number, string? Title, string? State);
    private sealed record ListIssuesPayload(List<ListedIssuePayload>? Issues, int Total);
}

/// <summary>
/// Story 40-8 — creates one platform issue per item of a JSON array of issue drafts
/// through the mediated engine route. The working end of the <c>create-issues</c>
/// workflow (<see cref="CreateIssuesActivity"/> is dispatched by
/// <c>SingleIssueCycleWorkflow</c>'s Defer/Split triage outcomes, which previously
/// dead-ended on a nonexistent definition —
/// <c>.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md</c>).
///
/// <para><b>Never a fault, never a hang (D4).</b> The parent cycle has NO failure edge
/// from its dispatch and ignores the child's outputs, so this child must ALWAYS
/// complete: malformed/empty <c>issuesJson</c> completes <c>Success</c> with 0 created
/// and a recorded warning; a per-item HTTP failure emits a loud per-item
/// <c>ISSUES.CREATE_ITEM.FAILED</c> event and continues; if any item failed the
/// activity completes the <c>Failure</c> outcome — both outcomes are routed by the
/// workflow to its output surface → <c>Finish</c>. A faulted node's outbound edges
/// never fire in Elsa 3.5, so nothing here rethrows
/// (the <see cref="ApplyTriageResultActivity"/> precedent).</para>
///
/// <para><b>Idempotent re-run (D3).</b> Before creating, the activity lists the repo's
/// issues (<c>state=all</c>, paged at <see cref="DedupePageSize"/>, capped at
/// <see cref="MaxDedupePages"/> pages with a loud warning when truncated) and skips any
/// item whose exact (trimmed, ordinal) title already exists; a per-run created-set
/// guards within-run duplicates. The PLATFORM is the durable record — a crash at any
/// point followed by a re-run of the same input produces the input set exactly once.
/// Known limitations (pinned by tests): duplicate titles inside one input collapse to
/// one issue (warned); an unrelated pre-existing same-title issue suppresses a create
/// (recorded as skipped). Under-creation-with-a-loud-record beats double-creation.</para>
///
/// <para><b>Mock short-circuit</b>: with no <c>Engine:CallbackUrl</c> configured (and
/// no injected client) the activity logs loudly, reports success with 0 created, and
/// completes — the <c>ApplyTriageResultActivity</c> parity behaviour.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Create Issues",
    "Create one platform issue per draft in a JSON array via the mediated engine route, with platform-side dedupe",
    Kind = ActivityKind.Task
)]
[FlowNode("Success", "Failure")]
public class CreateIssuesActivity : TammaOutcomeActivity
{
    /// <summary>Dedupe list page size (the engine route's maximum).</summary>
    public const int DedupePageSize = 100;

    /// <summary>Cap on dedupe list pages (bounded read on big repos — beyond the cap,
    /// dedupe degrades to within-run only and a warning is recorded).</summary>
    public const int MaxDedupePages = 10;

    // Suppress the base auto-emit — a caught Failure outcome returns normally from
    // RunAsync, which would otherwise make the base emit a false .COMPLETED.
    public override string? EventType => null;

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "JSON array of issue drafts ({title, body?, labels?}); empty/malformed completes with 0 created")]
    public Input<string> IssuesJson { get; set; } = default!;

    [Output(Description = "Issues created by this run")]
    public Output<int> CreatedCount { get; set; } = default!;

    [Output(Description = "Items whose create POST failed (each has a loud FAILED event)")]
    public Output<int> FailedCount { get; set; } = default!;

    [Output(Description = "Items skipped (duplicate title / invalid draft; each has a SKIPPED event)")]
    public Output<int> SkippedCount { get; set; } = default!;

    [Output(Description = "JSON array of created platform issue numbers")]
    public Output<string> IssueNumbersJson { get; set; } = default!;

    [JsonConstructor]
    public CreateIssuesActivity() { }

    public CreateIssuesActivity(
        ILogger<CreateIssuesActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
    };

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var repo = Repository.Get(context);
        var issuesJson = IssuesJson.Get(context);

        var startedAt = DateTime.UtcNow;
        Emit(context, IssuesCreateEvents.BatchStarted, "started", null, new()
        {
            ["repository"] = repo,
        });

        // Resolve the client: injected seam wins; else the HTTP client from config;
        // else the mock short-circuit (ApplyTriageResultActivity ctor-or-GetService idiom).
        var injectedClient = context.GetService<IIssueCreateClient>();
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();
        var callbackUrl = configuration?["Engine:CallbackUrl"];

        IIssueCreateClient client;
        if (injectedClient != null)
        {
            client = injectedClient;
        }
        else if (!string.IsNullOrEmpty(callbackUrl) && httpClientFactory != null)
        {
            client = new HttpIssueCreateClient(httpClientFactory.CreateClient(), callbackUrl.TrimEnd('/'));
        }
        else
        {
            Logger?.LogWarning(
                "[Mock] No Engine:CallbackUrl configured — create-issues short-circuits WITHOUT creating " +
                "{Repo} issues (parity with ApplyTriageResultActivity's mock path)", repo);
            SetOutputs(context, new CreateIssuesCoreResult(0, 0, 0,
                Array.Empty<int>(), new[] { "mock short-circuit: no Engine:CallbackUrl — nothing created" }));
            Emit(context, IssuesCreateEvents.BatchCompleted, "success", DateTime.UtcNow - startedAt, new()
            {
                ["repository"] = repo,
                ["created"] = 0,
                ["mock"] = true,
            });
            await context.CompleteActivityWithOutcomesAsync("Success");
            return;
        }

        var result = await CreateIssuesCoreAsync(
            client, repo, issuesJson,
            emitItemEvent: (type, status, error, data) => Emit(context, type, status, null, data, error),
            Logger, MaxDedupePages, context.CancellationToken);

        SetOutputs(context, result);

        Emit(context, IssuesCreateEvents.BatchCompleted,
            result.FailedCount > 0 ? "error" : "success",
            DateTime.UtcNow - startedAt,
            new()
            {
                ["repository"] = repo,
                ["created"] = result.CreatedCount,
                ["failed"] = result.FailedCount,
                ["skipped"] = result.SkippedCount,
                ["issueNumbers"] = result.IssueNumbers,
                ["warnings"] = result.Warnings,
            });

        await context.CompleteActivityWithOutcomesAsync(result.FailedCount > 0 ? "Failure" : "Success");
    }

    private void SetOutputs(ActivityExecutionContext context, CreateIssuesCoreResult result)
    {
        CreatedCount.Set(context, result.CreatedCount);
        FailedCount.Set(context, result.FailedCount);
        SkippedCount.Set(context, result.SkippedCount);
        IssueNumbersJson.Set(context, JsonSerializer.Serialize(result.IssueNumbers));
    }

    private void Emit(
        ActivityExecutionContext context, string type, string status, TimeSpan? duration,
        Dictionary<string, object?> data, string? error = null)
    {
        TammaEventEmitter.Emit(context, this, Logger, new TammaEvent
        {
            EventType = type,
            Status = status,
            Duration = duration,
            Error = error,
            Data = data,
        });
    }

    /// <summary>
    /// Testable core (no Elsa context) — parse-tolerant draft handling, platform-side
    /// dedupe, per-item create + events. NEVER throws: every failure mode lands in the
    /// counts/warnings so the activity always completes a routable outcome (D4).
    /// The <c>ApplyCoreAsync</c> seam pattern.
    /// </summary>
    /// <param name="emitItemEvent">Per-item event sink:
    /// (eventType, status, error, data). Null-safe.</param>
    public static async Task<CreateIssuesCoreResult> CreateIssuesCoreAsync(
        IIssueCreateClient client,
        string? repository,
        string? issuesJson,
        Action<string, string, string?, Dictionary<string, object?>>? emitItemEvent = null,
        ILogger? logger = null,
        int maxDedupePages = MaxDedupePages,
        CancellationToken ct = default)
    {
        var repo = repository ?? "";
        var created = 0;
        var failed = 0;
        var skipped = 0;
        var numbers = new List<int>();
        var warnings = new List<string>();

        void Skip(string reason, string? title)
        {
            skipped++;
            warnings.Add(title is null ? reason : $"{reason} (title: \"{title}\")");
            emitItemEvent?.Invoke(IssuesCreateEvents.ItemSkipped, "warning", reason, new()
            {
                ["repository"] = repo,
                ["title"] = title,
                ["reason"] = reason,
            });
        }

        // ── Tolerant parse (AC1: never a fault). Malformed/empty input → 0 created
        //    + a recorded warning; invalid entries are skipped loudly, valid ones ride.
        var items = new List<IssueDraft>();
        if (string.IsNullOrWhiteSpace(issuesJson))
        {
            warnings.Add("issuesJson is empty — nothing to create");
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(issuesJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add(
                        $"issuesJson is not a JSON array (got {doc.RootElement.ValueKind}) — nothing to create");
                }
                else
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object)
                        {
                            Skip("invalid draft: not a JSON object", null);
                            continue;
                        }
                        var title = ReadString(element, "title")?.Trim();
                        if (string.IsNullOrEmpty(title))
                        {
                            Skip("invalid draft: missing/empty title", null);
                            continue;
                        }
                        var body = ReadString(element, "body") ?? ReadString(element, "description") ?? "";
                        var labels = new List<string>();
                        if (TryGetPropertyIgnoreCase(element, "labels", out var l) && l.ValueKind == JsonValueKind.Array)
                            foreach (var lab in l.EnumerateArray())
                                if (lab.ValueKind == JsonValueKind.String && lab.GetString() is { Length: > 0 } s)
                                    labels.Add(s);
                        items.Add(new IssueDraft(title, body, labels));
                    }
                    if (items.Count == 0 && skipped == 0)
                        warnings.Add("issuesJson is an empty array — nothing to create");
                }
            }
            catch (JsonException ex)
            {
                warnings.Add($"issuesJson did not parse as JSON ({ex.Message}) — nothing to create");
            }
        }

        // ── Platform-side dedupe read (D3): the platform is the durable record of
        //    what a previous (crashed) run already created. A failed/truncated read
        //    degrades to within-run dedupe with a LOUD warning — under-protection is
        //    recorded, never silent.
        var knownTitles = new HashSet<string>(StringComparer.Ordinal);
        if (items.Count > 0)
        {
            try
            {
                var page = 1;
                while (page <= maxDedupePages)
                {
                    var existing = await client.ListIssuesAsync(repo, page, DedupePageSize, ct);
                    foreach (var issue in existing)
                        if (!string.IsNullOrWhiteSpace(issue.Title))
                            knownTitles.Add(issue.Title.Trim());
                    if (existing.Count < DedupePageSize)
                        break;
                    if (page == maxDedupePages)
                    {
                        warnings.Add(
                            $"dedupe list truncated at {maxDedupePages} pages × {DedupePageSize} — " +
                            "beyond this, dedupe degrades to within-run only");
                        break;
                    }
                    page++;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "create-issues dedupe read failed for {Repo} — degrading to within-run dedupe only", repo);
                warnings.Add($"dedupe read failed ({ex.GetType().Name}) — degraded to within-run dedupe only");
            }
        }

        // ── Per-item create. A failure (non-2xx or throw) is a loud per-item event
        //    and the batch CONTINUES; the core never throws (D4).
        foreach (var item in items)
        {
            if (knownTitles.Contains(item.Title))
            {
                Skip("already exists (exact title match) — not re-created", item.Title);
                continue;
            }

            IssueCreateResult result;
            try
            {
                result = await client.CreateIssueAsync(repo, item.Title, item.Body, item.Labels, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "create-issues item threw for {Repo}: {Title}", repo, item.Title);
                result = IssueCreateResult.Fail(0);
                failed++;
                emitItemEvent?.Invoke(IssuesCreateEvents.ItemFailed, "error",
                    $"create-issue threw {ex.GetType().Name}", new()
                    {
                        ["repository"] = repo,
                        ["title"] = item.Title,
                        ["exception"] = ex.GetType().Name,
                    });
                continue;
            }

            if (result.Success)
            {
                created++;
                numbers.Add(result.IssueNumber);
                knownTitles.Add(item.Title); // within-run dedupe
                emitItemEvent?.Invoke(IssuesCreateEvents.ItemSuccess, "success", null, new()
                {
                    ["repository"] = repo,
                    ["title"] = item.Title,
                    ["issueNumber"] = result.IssueNumber,
                });
            }
            else
            {
                failed++;
                emitItemEvent?.Invoke(IssuesCreateEvents.ItemFailed, "error",
                    $"create-issue returned {result.StatusCode}", new()
                    {
                        ["repository"] = repo,
                        ["title"] = item.Title,
                        ["statusCode"] = result.StatusCode,
                    });
            }
        }

        return new CreateIssuesCoreResult(created, failed, skipped, numbers, warnings);
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record IssueDraft(string Title, string Body, List<string> Labels);
}

/// <summary>Outcome of one <see cref="CreateIssuesActivity.CreateIssuesCoreAsync"/> run.</summary>
public sealed record CreateIssuesCoreResult(
    int CreatedCount,
    int FailedCount,
    int SkippedCount,
    IReadOnlyList<int> IssueNumbers,
    IReadOnlyList<string> Warnings);
