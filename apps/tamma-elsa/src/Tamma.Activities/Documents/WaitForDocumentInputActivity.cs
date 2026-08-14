using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-13 (D3) — the ONE generic domain-input gate. Suspends any lifecycle
/// binding on a tenant-folded, unguessable bookmark
/// (<c>document-input-{tenant}-{session}</c>) until a caller injects domain input
/// (e.g. a stakeholder's clarifying-question answers), then surfaces the input as a
/// typed outcome the flowchart branches on. Generalizes the legacy
/// <c>WaitForClarifyingAnswersActivity</c> (retired by this story): unlike the 39-8
/// decision gate — which deliberately arms NO SLA — an unanswered stakeholder input
/// must still time out loudly, so the durable <c>DelayFor</c> SLA is kept.
///
/// <para>Two resume paths are armed on suspend:
/// <list type="bullet">
///   <item><description>an input bookmark
///   (<see cref="InputBookmarkName"/>) resumed via the secure resume endpoint →
///   <c>Received</c>;</description></item>
///   <item><description>a DURABLE delay bookmark at the caller-supplied SLA
///   (<see cref="TimeoutMinutes"/>, default 4320 = 3 days) → <c>Timeout</c>. The
///   Delay bookmark is EF-persisted and re-armed on rehydration, so a host restart
///   mid-wait no longer drops the SLA.</description></item>
/// </list></para>
///
/// <para><b>SECURITY (IDOR)</b> — the bookmark name folds in the tenant id, so a
/// resume caller scoped to tenant A can never resolve tenant B's gate (a cross-tenant
/// attempt 404s). The session id is an unguessable 128-bit Guid.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Wait For Document Input",
    "Suspend any lifecycle binding until domain input is injected on the tenant-folded bookmark or the SLA expires",
    Kind = ActivityKind.Task
)]
[FlowNode("Received", "Timeout")]
public class WaitForDocumentInputActivity : Activity
{
    private readonly ILogger<WaitForDocumentInputActivity>? _logger;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Decision/input session id (bookmark scoping)")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Tenant id (GUID string or empty for single-user). Folded into the
    /// bookmark name so a cross-tenant resume can never resolve this gate.</summary>
    [Input(Description = "Tenant id (bookmark scoping — prevents cross-tenant resume/IDOR)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>SLA in minutes. Caller supplies it directly; ≤ 0 falls back to
    /// <see cref="TimeoutConfigKey"/> (if set) then the 3-day default.</summary>
    [Input(Description = "Input SLA in minutes (<= 0 → config key / 3-day default)")]
    public Input<int> TimeoutMinutes { get; set; } = new(0);

    /// <summary>Optional configuration key read for the SLA when <see cref="TimeoutMinutes"/>
    /// is ≤ 0 (the Clarify binding passes <c>Clarify:AnswerTimeoutMinutes</c>, preserving the
    /// legacy key). Empty → skip config lookup.</summary>
    [Input(Description = "Optional config key for the SLA when TimeoutMinutes <= 0")]
    public Input<string?> TimeoutConfigKey { get; set; } = new((string?)null);

    /// <summary>The injected domain input text (set on bookmark resume).</summary>
    [Output(Description = "Injected domain input text")]
    public Output<string?> InputJson { get; set; } = default!;

    /// <summary>Whether input was received (vs a timeout).</summary>
    [Output(Description = "Whether input was received")]
    public Output<bool> Received { get; set; } = default!;

    /// <summary>Whether the SLA expired with no input (durable timeout).</summary>
    [Output(Description = "Whether the input SLA expired with no response")]
    public Output<bool> TimedOut { get; set; } = default!;

    [JsonConstructor]
    public WaitForDocumentInputActivity() { }

    public WaitForDocumentInputActivity(
        ILogger<WaitForDocumentInputActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The SINGLE canonical input-bookmark name (<c>document-input-{tenant}-{session}</c>).
    /// Shared by the suspend side (<see cref="Execute"/>) and the resume side
    /// (<c>DocumentInputResumeEndpoint</c>) so the two match byte-for-byte — delegates to
    /// <see cref="LifecycleBookmarks.ForDocumentInput"/>.
    /// </summary>
    public static string InputBookmarkName(string? tenantId, Guid sessionId)
        => LifecycleBookmarks.ForDocumentInput(tenantId, sessionId);

    protected override void Execute(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var tenantId = TenantId.GetOrDefault(context);
        var bookmarkName = InputBookmarkName(tenantId, sessionId);

        _logger?.LogInformation(
            "Waiting for document input: bookmark={BookmarkName} for session {SessionId}",
            bookmarkName, sessionId);

        // 1) Input bookmark — resumed via the secure resume endpoint.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = bookmarkName,
            Callback = OnInputReceivedAsync,
            AutoBurn = true,
            IncludeActivityInstanceId = false,
        });

        // 2) Durable SLA — a DelayFor (Delay) bookmark re-armed by Elsa.Scheduling on
        //    rehydration. A never-answered input terminates as a real Timeout even across a
        //    host restart inside the SLA window.
        var supplied = TimeoutMinutes.Get(context);
        var slaMinutes = supplied;
        if (slaMinutes <= 0)
        {
            var configKey = TimeoutConfigKey.GetOrDefault(context);
            if (!string.IsNullOrWhiteSpace(configKey))
                slaMinutes = _configuration?.GetValue<int?>(configKey) ?? 0;
        }
        if (slaMinutes <= 0)
            slaMinutes = 4320;
        context.DelayFor(TimeSpan.FromMinutes(Math.Max(1, slaMinutes)), OnTimeoutAsync);

        _logger?.LogInformation(
            "Document-input wait armed; durable SLA timeout at +{SlaMinutes}min for session {SessionId}",
            slaMinutes, sessionId);
    }

    private async ValueTask OnInputReceivedAsync(ActivityExecutionContext context)
    {
        var (received, inputJson) = ReadInput(context.WorkflowInput);

        _logger?.LogInformation(
            "Document input resumed (external): Received={Received}, length={Length}",
            received, inputJson?.Length ?? 0);

        context.Set(InputJson, inputJson);
        context.Set(Received, received);
        context.Set(TimedOut, false);

        await context.CompleteActivityWithOutcomesAsync("Received");
    }

    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        _logger?.LogWarning(
            "Document-input SLA expired (durable timeout) for session {SessionId} — taking the Timeout terminal",
            sessionId);

        context.Set(InputJson, null);
        context.Set(Received, false);
        context.Set(TimedOut, true);

        await context.CompleteActivityWithOutcomesAsync("Timeout");
    }

    /// <summary>
    /// Pure read-back of the injected input from the bookmark resume input (exposed for
    /// unit testing). <c>Received</c> is coerced via <see cref="ResumeInput.AsBool"/> so it
    /// is correct whether the runtime delivers the flag as a boxed <see cref="bool"/>
    /// (in-process) or as a <see cref="string"/> / <see cref="System.Text.Json.JsonElement"/>
    /// (serializing dispatcher) — the #15/#437 lesson: never a bare <c>is true</c> on resume
    /// input. <c>InputJson</c> is read via <c>.ToString()</c>, already serialization-tolerant.
    /// </summary>
    public static (bool Received, string? InputJson) ReadInput(IDictionary<string, object> input)
    {
        var received = input.TryGetValue("Received", out var r) && ResumeInput.AsBool(r);
        var inputJson = input.TryGetValue("InputJson", out var ij) ? ij?.ToString() : null;
        return (received, inputJson);
    }
}
