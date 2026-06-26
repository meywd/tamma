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
/// Applies triage results: sets labels on the issue/creates issue for alerts,
/// posts a triage summary comment.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>TriageItemCycle.md</c> #8): the
/// activity is <b>fail-loud</b>. Previously its <c>RunAsync</c> wrapped every
/// engine-callback POST in a <c>try/catch</c> that logged and returned, and NEVER
/// checked <c>response.IsSuccessStatusCode</c> — a 4xx/5xx from <c>issue-labels</c> /
/// <c>issue-comment</c> / <c>create-issue</c> still surfaced as
/// <c>TRIAGE.APPLY.RESULT.COMPLETED</c> (a false success). It now checks the status of
/// each POST (<see cref="ApplyCoreAsync"/> + <see cref="EnsureSuccess"/> throw on the
/// first non-success).</para>
///
/// <para>Review fix (CRITICAL — "stuck, no terminal" hang): the activity is now a
/// <see cref="TammaOutcomeActivity"/> exposing an explicit <c>Success</c> /
/// <c>Failure</c> <see cref="FlowNode"/>. An apply failure (a non-success POST or a
/// network throw from <see cref="ApplyCoreAsync"/>) is caught here, emits a loud
/// <c>TRIAGE.APPLY.RESULT.FAILED</c> leaf event (secret-free, status-code only), sets
/// <see cref="FailureReason"/>, and completes the <c>Failure</c> outcome — it does NOT
/// rethrow. That edge is the precedent (<c>MergePullRequestActivity</c>'s <c>Error</c>
/// outcome → loud terminal): a Flowchart never schedules outbound edges of a
/// <i>faulted</i> node (Elsa 3.5 <c>Flowchart.ProcessChildCompletedAsync</c> only routes
/// a <c>Completed</c> child), so a bare throw under the default <c>FaultStrategy</c>
/// would HALT the cycle with no <c>TRIAGE.ISSUE.*</c> terminal and no <c>itemResult</c>.
/// Routing the failure as an outcome lets the cycle emit EXACTLY ONE loud
/// <c>TRIAGE.ISSUE.FAILED</c> terminal on apply failure. <see cref="EventType"/> is
/// <c>null</c> so the base does NOT auto-emit a false <c>.COMPLETED</c> on the caught
/// failure; the leaf <c>STARTED</c>/<c>COMPLETED</c>/<c>FAILED</c> events are emitted
/// explicitly here.</para>
///
/// <para>Build-out #7: the caller (the cycle) may supply a deterministically-rendered
/// comment (<see cref="CommentOverride"/>) and a vocabulary-validated label set
/// (<see cref="LabelsOverride"/>) so the applied labels/comment are the canonical,
/// validated grid rather than arbitrary LLM prose/labels. When unset, the activity
/// falls back to the decision JSON's own <c>labels</c>/<c>comment</c> (back-compat).</para>
///
/// Outcomes:
///   - Success: labels/comment applied (or the mock no-op short-circuit).
///   - Failure: an engine-callback POST failed (4xx/5xx) or threw — the cycle routes
///     this to a loud <c>TRIAGE.ISSUE.FAILED</c> terminal (no false success).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Apply Triage Result",
    "Apply labels and post triage comment on the issue, with an explicit Failure outcome",
    Kind = ActivityKind.Task
)]
[FlowNode("Success", "Failure")]
public class ApplyTriageResultActivity : TammaOutcomeActivity
{
    /// <summary>The leaf apply event prefix used for the explicit STARTED/COMPLETED/FAILED
    /// leaf events. <see cref="EventType"/> stays <c>null</c> so the base does not also
    /// auto-emit (which would surface a false <c>.COMPLETED</c> on a caught failure).</summary>
    public const string ApplyEventType = "TRIAGE.APPLY.RESULT";

    // Suppress the base auto-emit — a caught Failure outcome returns normally from
    // RunAsync, which would otherwise make the base emit a false .COMPLETED.
    public override string? EventType => null;

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Item JSON")]
    public Input<string> ItemJson { get; set; } = default!;

    [Input(Description = "PO decision JSON with labels, priority, comment")]
    public Input<string> DecisionJson { get; set; } = default!;

    [Input(Description = "Validated label set to apply (overrides the decision JSON labels when set; empty list → fall back)")]
    public Input<ICollection<string>?> LabelsOverride { get; set; } = new((ICollection<string>?)null);

    [Input(Description = "Deterministically-rendered comment to post (overrides the decision JSON comment when non-empty)")]
    public Input<string?> CommentOverride { get; set; } = new((string?)null);

    [Output(Description = "Secret-free apply-failure reason when the Failure outcome fires")]
    public Output<string?> FailureReason { get; set; } = default!;

    [JsonConstructor]
    public ApplyTriageResultActivity() { }

    public ApplyTriageResultActivity(
        ILogger<ApplyTriageResultActivity> logger,
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
        var itemJson = ItemJson.Get(context);
        var decisionJson = DecisionJson.Get(context);
        var labelsOverride = LabelsOverride.GetOrDefault(context);
        var commentOverride = CommentOverride.GetOrDefault(context);

        // Explicit leaf STARTED (the base no longer auto-emits — EventType is null).
        var startedAt = DateTime.UtcNow;
        EmitLeaf(context, $"{ApplyEventType}.STARTED", "started", null, null);

        // Resolve the apply client. A directly-injected ITriageApplyClient (test seam, or
        // a future DI registration) wins; otherwise build the HTTP client from config +
        // the http-client factory (resolved from the activity scope when the ctor did not
        // inject them — mirrors MergePullRequestActivity's `_github ?? context.GetService`).
        var injectedClient = context.GetService<ITriageApplyClient>();
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();
        var callbackUrl = configuration?["Engine:CallbackUrl"];

        ITriageApplyClient client;
        if (injectedClient != null)
        {
            client = injectedClient;
        }
        else if (!string.IsNullOrEmpty(callbackUrl) && httpClientFactory != null)
        {
            client = new HttpTriageApplyClient(httpClientFactory.CreateClient(), callbackUrl.TrimEnd('/'));
        }
        else
        {
            Logger?.LogInformation("[Mock] Would apply triage result");
            EmitLeaf(context, $"{ApplyEventType}.COMPLETED", "success", DateTime.UtcNow - startedAt, null);
            await context.CompleteActivityWithOutcomesAsync("Success");
            return;
        }

        try
        {
            await ApplyCoreAsync(
                client, repo, itemJson, decisionJson,
                labelsOverride?.ToArray(), commentOverride, Logger, context.CancellationToken);
        }
        catch (Exception ex)
        {
            // Fail loud — but as a ROUTABLE outcome, not a fault. A faulted node's
            // outbound edges never fire (Elsa 3.5 Flowchart), so a rethrow here would
            // halt the cycle with no TRIAGE.ISSUE.* terminal. Emit the loud leaf FAILED
            // (secret-free message — ApplyCoreAsync/EnsureSuccess throw status-code-only)
            // and complete the Failure outcome so the cycle routes a single loud
            // TRIAGE.ISSUE.FAILED terminal.
            Logger?.LogError(ex, "Triage apply failed for {Repo}", repo);
            FailureReason.Set(context, ex.Message);
            EmitLeaf(context, $"{ApplyEventType}.FAILED", "error", DateTime.UtcNow - startedAt, ex.Message);
            await context.CompleteActivityWithOutcomesAsync("Failure");
            return;
        }

        EmitLeaf(context, $"{ApplyEventType}.COMPLETED", "success", DateTime.UtcNow - startedAt, null);
        await context.CompleteActivityWithOutcomesAsync("Success");
    }

    private void EmitLeaf(
        ActivityExecutionContext context, string type, string status, TimeSpan? duration, string? error)
    {
        TammaEventEmitter.Emit(context, this, Logger, new TammaEvent
        {
            EventType = type,
            Status = status,
            Duration = duration,
            Error = error,
            Data = BuildEndData(context),
        });
    }

    /// <summary>
    /// Testable core (no Elsa context) — the load-bearing fail-loud logic (#8). Parses
    /// the item + decision, picks the validated labels / rendered comment (#7), then
    /// POSTs the labels/comment (or creates an issue for an alert) through the injectable
    /// <paramref name="client"/>. Every POST is checked: a non-success response or a
    /// throw propagates. <see cref="RunAsync"/> catches that throw, emits the loud
    /// <c>TRIAGE.APPLY.RESULT.FAILED</c> leaf event, and completes the <c>Failure</c>
    /// outcome so the cycle's fail-the-item edge fires (a routable outcome, not a fault —
    /// see the class summary for why a rethrow would halt the cycle). A null item/decision
    /// is an honest input bug — it throws, never a false success. Follows the
    /// <c>UpdateIssueStatusActivity.ExecuteCoreAsync</c> seam pattern.
    /// </summary>
    public static async Task ApplyCoreAsync(
        ITriageApplyClient client,
        string? repository,
        string? itemJson,
        string? decisionJson,
        IReadOnlyList<string>? labelsOverride,
        string? commentOverride,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var item = JsonSerializer.Deserialize<TriageItem>(itemJson ?? "",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var decision = JsonSerializer.Deserialize<TriageDecision>(decisionJson ?? "",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (item == null || decision == null)
            throw new InvalidOperationException(
                "Cannot apply triage result: item or decision JSON did not deserialize.");

        // #7 — prefer the caller's validated label set / rendered comment; fall back to
        // the decision JSON's own values for back-compat when the override is unset.
        var labels = labelsOverride is { Count: > 0 }
            ? new List<string>(labelsOverride)
            : decision.Labels;
        var comment = !string.IsNullOrWhiteSpace(commentOverride)
            ? commentOverride!
            : decision.Comment;

        if (item.Type == "issue" && item.Number > 0)
        {
            if (labels is { Count: > 0 })
                EnsureSuccess(await client.SetLabelsAsync(repository ?? "", item.Number, labels, ct),
                    "issue-labels", item.Number);

            if (!string.IsNullOrEmpty(comment))
                EnsureSuccess(await client.PostCommentAsync(repository ?? "", item.Number, comment, ct),
                    "issue-comment", item.Number);
        }
        else
        {
            EnsureSuccess(
                await client.CreateIssueAsync(
                    repository ?? "", item.Title, $"{item.Body}\n\n---\n{comment}", labels, ct),
                "create-issue", item.Number);

            logger?.LogInformation("Created issue for {Type}: {Title}", item.Type, item.Title);
        }
    }

    /// <summary>
    /// #8 — fail loud. A non-success engine-callback response must <c>throw</c> so
    /// <see cref="RunAsync"/> emits <c>TRIAGE.APPLY.RESULT.FAILED</c> and completes the
    /// <c>Failure</c> outcome (the cycle's fail-the-item edge) — never a swallowed 4xx/5xx
    /// reported as <c>.COMPLETED</c>. The thrown message is status-code only (no response
    /// body) to keep secrets out of the event/log. Exposed for unit testing.
    /// </summary>
    public static void EnsureSuccess(TriageApplyResult result, string endpoint, int issueNumber)
    {
        if (result.Success) return;
        throw new HttpRequestException(
            $"Triage apply failed: {endpoint} returned {result.StatusCode} for issue #{issueNumber}.");
    }
}

/// <summary>
/// Result of one triage-apply engine-callback POST. <see cref="Success"/> mirrors
/// <c>HttpResponseMessage.IsSuccessStatusCode</c>; <see cref="StatusCode"/> is the
/// numeric code for the (secret-free) error message.
/// </summary>
public readonly record struct TriageApplyResult(bool Success, int StatusCode)
{
    public static TriageApplyResult Ok() => new(true, 200);
    public static TriageApplyResult Fail(int statusCode) => new(false, statusCode);
}

/// <summary>
/// Injectable seam for the triage-apply engine callbacks (issue-labels / issue-comment /
/// create-issue), so <see cref="ApplyTriageResultActivity.ApplyCoreAsync"/> is unit
/// testable without a live HTTP server. Mirrors <c>IIssueCallbackClient</c>.
/// </summary>
public interface ITriageApplyClient
{
    Task<TriageApplyResult> SetLabelsAsync(string repository, int issueNumber, IReadOnlyList<string> labels, CancellationToken ct);
    Task<TriageApplyResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct);
    Task<TriageApplyResult> CreateIssueAsync(string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct);
}

/// <summary>Default <see cref="ITriageApplyClient"/> over the engine-callback HTTP API.</summary>
internal sealed class HttpTriageApplyClient : ITriageApplyClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpTriageApplyClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<TriageApplyResult> SetLabelsAsync(string repository, int issueNumber, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var r = await _http.PostAsJsonAsync($"{_baseUrl}/api/engine/issue-labels",
            new { repository, issueNumber, labels }, ct);
        return ToResult(r);
    }

    public async Task<TriageApplyResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct)
    {
        var r = await _http.PostAsJsonAsync($"{_baseUrl}/api/engine/issue-comment",
            new { repository, issueNumber, body }, ct);
        return ToResult(r);
    }

    public async Task<TriageApplyResult> CreateIssueAsync(string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var r = await _http.PostAsJsonAsync($"{_baseUrl}/api/engine/create-issue",
            new { repository, title, body, labels }, ct);
        return ToResult(r);
    }

    private static TriageApplyResult ToResult(HttpResponseMessage r)
        => new(r.IsSuccessStatusCode, (int)r.StatusCode);
}

public class TriageDecision
{
    public string Priority { get; set; } = "normal";
    public string Type { get; set; } = "unknown";
    public string Complexity { get; set; } = "medium";
    public string Automation { get; set; } = "needs-human";
    public List<string> Labels { get; set; } = new();
    public string? Comment { get; set; }
    public string? Reasoning { get; set; }
}
