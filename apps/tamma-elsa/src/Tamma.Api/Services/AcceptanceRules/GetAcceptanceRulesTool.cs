using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Services.AcceptanceRules;

/// <summary>
/// The <c>get_acceptance_rules</c> tool (Story 39-5 AC3a, Design Decision D6): the
/// 39-17 orchestrator agent calls it at decision time to read the effective rules
/// for a document type. It implements the existing <see cref="IToolExecutor"/>
/// seam but is PRINCIPAL-BOUND AT CONSTRUCTION and NOT globally DI-registered —
/// a singleton <c>IToolExecutor</c> registration would carry no principal and would
/// inject a principal-less instance into every coding-agent tool loop. The 39-17
/// host constructs one per tenant-agent session via
/// <see cref="GetAcceptanceRulesToolFactory"/>. Story 43-4's boot validator records
/// the exemption in <c>ToolCatalogAllowlists.NotDiRegisteredTools</c>
/// (shrink-only, one entry).
///
/// <para>The principal (<c>userId</c> XOR <c>tenantId</c>) comes from the SERVER
/// at construction, NEVER from the LLM's <c>argumentsJson</c> — only
/// <c>documentTypeKey</c> is an accepted argument (the <c>LlmCallWorkflow</c>
/// conventions discipline). The output JSON is the same
/// <see cref="ResolvedAcceptanceRules"/> the request embeds, serialized through
/// the one canonical <see cref="AcceptanceRulesJson.Options"/> (AC3 byte-identity).</para>
/// </summary>
public sealed class GetAcceptanceRulesTool : IToolExecutor
{
    private readonly IAcceptanceRulesResolver _resolver;
    private readonly Guid? _userId;
    private readonly Guid? _tenantId;
    private readonly ILogger<GetAcceptanceRulesTool>? _logger;

    /// <summary>
    /// Construct a principal-bound tool. Exactly one of <paramref name="userId"/>
    /// / <paramref name="tenantId"/> should be set (single-user vs SaaS); the
    /// factory enforces that.
    /// </summary>
    public GetAcceptanceRulesTool(
        IAcceptanceRulesResolver resolver,
        Guid? userId,
        Guid? tenantId,
        ILogger<GetAcceptanceRulesTool>? logger = null)
    {
        _resolver = resolver;
        _userId = userId;
        _tenantId = tenantId;
        _logger = logger;
    }

    public string ToolName => "get_acceptance_rules";

    public string Description =>
        "Read the effective acceptance rules (autonomy level, revision/repair bounds, escalation criteria, " +
        "reviewer selection, and decision/routing guidance) for a document type. Call this at decision time " +
        "to decide whether to decide the acceptance yourself or assign it to a role.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["documentTypeKey"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "The document-type wire key (e.g. 'plan', 'design', 'review'). Optional; defaults to 'review'.",
            },
        },
        ["required"] = Array.Empty<string>(),
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Only documentTypeKey is honored from the arguments. A principal
            // smuggled into argumentsJson is IGNORED — the principal is bound at
            // construction from the server (D6).
            var typeKey = "review";
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
                if (args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("documentTypeKey", out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    typeKey = el.GetString() ?? "review";
                }
            }

            if (!DocumentTypeKeyExtensions.TryParse(typeKey, out var documentType))
            {
                return Fail(toolCallId, sw,
                    $"Unknown document type '{typeKey}'. Valid types: " +
                    string.Join(", ", Enum.GetValues<DocumentTypeKey>().Select(k => k.ToWire())) + ".");
            }

            var resolved = _tenantId is { } tid && tid != Guid.Empty
                ? await _resolver.ResolveForTenantAsync(tid, documentType, cancellationToken)
                : await _resolver.ResolveAsync(_userId, documentType, cancellationToken);

            var output = JsonSerializer.Serialize(resolved, AcceptanceRulesJson.Options);
            return new ToolExecutionResult(toolCallId, ToolName, true, output, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Never throw — return a failure result (IToolExecutor contract).
            _logger?.LogWarning(ex, "get_acceptance_rules failed for {ToolCallId}", toolCallId);
            return Fail(toolCallId, sw, $"get_acceptance_rules failed: {ex.Message}");
        }
    }

    private ToolExecutionResult Fail(string toolCallId, Stopwatch sw, string message) =>
        new(toolCallId, ToolName, false, message, sw.ElapsedMilliseconds);
}

/// <summary>
/// Constructs a principal-bound <see cref="GetAcceptanceRulesTool"/> for the
/// 39-17 host (Design Decision D6). NOT added to the global
/// <see cref="IToolExecutor"/> DI set — the host mounts one per tenant-agent
/// session so the tool never leaks into a coding-agent tool loop.
/// </summary>
public sealed class GetAcceptanceRulesToolFactory
{
    private readonly IAcceptanceRulesResolver _resolver;
    private readonly ILoggerFactory? _loggerFactory;

    public GetAcceptanceRulesToolFactory(IAcceptanceRulesResolver resolver, ILoggerFactory? loggerFactory = null)
    {
        _resolver = resolver;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Create a tool bound to a single-user principal (<paramref name="userId"/>)
    /// or a SaaS tenant (<paramref name="tenantId"/>). Exactly one must be set.
    /// </summary>
    public GetAcceptanceRulesTool Create(Guid? userId = null, Guid? tenantId = null)
    {
        var hasUser = userId is { } u && u != Guid.Empty;
        var hasTenant = tenantId is { } t && t != Guid.Empty;
        if (hasUser == hasTenant)
            throw new ArgumentException(
                "Exactly one of userId / tenantId must be provided (single-user vs SaaS principal).");

        return new GetAcceptanceRulesTool(
            _resolver,
            hasUser ? userId : null,
            hasTenant ? tenantId : null,
            _loggerFactory?.CreateLogger<GetAcceptanceRulesTool>());
    }
}
