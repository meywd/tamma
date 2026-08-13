using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Api.Services.Agents.Scripted;

/// <summary>
/// The deterministic in-process implementation behind the opt-in "scripted"
/// provider (2026-08-13, the Epic 31 P5 LLM stub). Pure function of the call's
/// (role, action, documentType) keys + the loaded script: same call ⇒ same
/// response, zero tokens, zero cost, no network.
///
/// <para><b>Key resolution order</b> (first hit wins; keys normalized to
/// lowercase): <c>{role}/{action}@{documentType}</c> →
/// <c>@{documentType}</c> → registry example for the document type →
/// <c>{role}/{action}</c> → <c>*</c>. A miss returns a FAILED response
/// (HTTP 422-shaped, non-retryable) whose error message names every key
/// tried — the "unscripted cell" contract, never a silent default.</para>
/// </summary>
public sealed class ScriptedLlmResponder : IScriptedLlmResponder
{
    /// <summary>Error banner for an unscripted cell (pinned by tests).</summary>
    public const string MissingCellError = "SCRIPTED_PROVIDER_MISSING_CELL";

    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly ILogger<ScriptedLlmResponder>? _logger;

    public ScriptedLlmResponder(
        IReadOnlyDictionary<string, string>? overrides = null,
        ILogger<ScriptedLlmResponder>? logger = null)
    {
        _overrides = overrides ?? new Dictionary<string, string>();
        _logger = logger;
    }

    /// <summary>
    /// Load the override map from a script file
    /// (<c>{"responses": {key: text}}</c>). Fail-loud: a set-but-missing or
    /// unparseable file throws — a test pointing at a wrong path must never
    /// silently run the built-in script instead.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadOverrides(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                $"Scripted-provider script file not found: '{scriptPath}' " +
                $"({ScriptedLlmProviderOptions.SectionName}:ScriptPath).", scriptPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(scriptPath));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("responses", out var responses)
            || responses.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Scripted-provider script file '{scriptPath}' must be an object with a " +
                "'responses' object: { \"responses\": { \"role/action[@doc]\": \"text\" } }.");
        }

        foreach (var prop in responses.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Scripted-provider script '{scriptPath}': key '{prop.Name}' must map to a string.");
            }
            map[Normalize(prop.Name)] = prop.Value.GetString() ?? string.Empty;
        }

        return map;
    }

    /// <inheritdoc />
    public bool CanHandle(string? provider) =>
        !string.IsNullOrWhiteSpace(provider)
        && string.Equals(provider.Trim(), ScriptedProviderPosture.ProviderKey,
            StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public NormalizedLlmResponse Respond(ScriptedLlmCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var role = Normalize(call.Role);
        var action = Normalize(call.Action);
        var docType = Normalize(call.DocumentType);

        var tried = new List<string>();
        var text = ResolveText(role, action, docType, tried);

        if (text is null)
        {
            var detail =
                $"{MissingCellError}: no scripted response for role='{role}', action='{action}', " +
                $"documentType='{docType}'. Keys tried (in order): [{string.Join(", ", tried)}]. " +
                "Add the cell to the script-override file " +
                $"({ScriptedLlmProviderOptions.SectionName}:ScriptPath) or to ScriptedCycleLibrary.";

            _logger?.LogWarning(
                "Scripted provider miss: role={Role}, action={Action}, documentType={DocumentType}, " +
                "correlationId={CorrelationId}", role, action, docType, call.CorrelationId);

            // FAILED, non-retryable-shaped (422): ManagedAgent surfaces it as
            // PROVIDER_ERROR with the preserved status; the engine's RetryCheck
            // treats 4xx as non-transient, so the cycle fails LOUD, not slow.
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = 422,
                ErrorMessage = detail,
                StopReason = StopReason.EndTurn,
            };
        }

        _logger?.LogDebug(
            "Scripted provider hit: role={Role}, action={Action}, documentType={DocumentType}, " +
            "correlationId={CorrelationId}, chars={Chars}",
            role, action, docType, call.CorrelationId, text.Length);

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = text,
            Model = ScriptedProviderPosture.ProviderKey,
            // Zero tokens on purpose: the scripted provider spends nothing, so
            // budget/cost accounting stays truthful (cost basis computes to 0).
            PromptTokens = 0,
            CompletionTokens = 0,
            HttpStatusCode = 200,
            ToolCalls = null,
            StopReason = StopReason.EndTurn,
        };
    }

    private string? ResolveText(string role, string action, string docType, List<string> tried)
    {
        var cell = $"{role}/{action}";

        if (docType.Length > 0)
        {
            var qualified = $"{cell}@{docType}";
            tried.Add(qualified);
            if (TryGet(qualified, out var t1)) return t1;

            var typeDefault = $"@{docType}";
            tried.Add(typeDefault);
            if (TryGet(typeDefault, out var t2)) return t2;

            // Registry fallback: the type's own first valid example — valid by
            // the registry drift suite's self-check, so it passes the 39-9 ring.
            tried.Add($"(registry example for '{docType}')");
            var example = ScriptedCycleLibrary.DocumentExampleFor(docType);
            if (example is not null) return example;
        }

        tried.Add(cell);
        if (TryGet(cell, out var t3)) return t3;

        tried.Add("*");
        return TryGet("*", out var t4) ? t4 : null;
    }

    private bool TryGet(string key, out string? text)
    {
        if (_overrides.TryGetValue(key, out var fromOverride))
        {
            text = fromOverride;
            return true;
        }

        if (ScriptedCycleLibrary.Responses.TryGetValue(key, out var builtIn))
        {
            text = builtIn;
            return true;
        }

        text = null;
        return false;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
