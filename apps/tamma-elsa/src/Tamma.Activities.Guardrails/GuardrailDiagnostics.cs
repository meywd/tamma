using Microsoft.CodeAnalysis;

namespace Tamma.Activities.Guardrails;

/// <summary>
/// Story 38-4 — the single build diagnostic emitted by
/// <see cref="EngineExternalCallAnalyzer"/>.
///
/// <para><b>TAMMA001</b> — an engine-surface type (<c>Tamma.Activities</c> /
/// <c>Tamma.ElsaServer</c>) either makes a direct external HTTP call, injects a
/// credential-holding vendor service, or invokes a denied Slack send. It is declared at
/// <see cref="DiagnosticSeverity.Error"/> so it FAILS THE BUILD — the repo sets
/// <c>TreatWarningsAsErrors=false</c> (see <c>Directory.Build.props</c>), so a Warning
/// would not gate. This is the permanent backstop for rule 1 ("a workflow STEP must never
/// call an external API/provider directly"): the credential-holding code, the external
/// HTTP call, and the metering/audit all live in <c>Tamma.Api</c>; the engine delegates
/// over HTTP via <c>TammaApiClient</c> and holds NO external credential.</para>
/// </summary>
public static class GuardrailDiagnostics
{
    /// <summary>The diagnostic id — documented under <c>## TAMMA001</c> so a developer who
    /// hits it can self-serve the mediation fix.</summary>
    public const string Id = "TAMMA001";

    /// <summary>The analyzer category (used by rule-config / suppression tooling).</summary>
    public const string Category = "Tamma.Architecture";

    public static readonly DiagnosticDescriptor EngineDirectExternalCall = new(
        id: Id,
        title: "Engine step makes a direct external call or injects a vendor credential",
        messageFormat:
            "'{0}' performs a direct external call / injects credential-holding '{1}'. " +
            "Engine steps must delegate to Tamma.Api via TammaApiClient (rule 1; design §1). " +
            "See the /api/v1/llm/call (32-5) / /api/v1/git/* (38-1) / /api/v1/notifications/slack (38-3) pattern.",
        category: Category,
        // Error so it fails the build (TreatWarningsAsErrors is false repo-wide).
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A workflow step must never call an external API/provider directly or hold an external credential. " +
            "Route the effect through Tamma.Api via TammaApiClient; the API is the sole holder of external " +
            "credentials and the sole caller of external endpoints.",
        helpLinkUri:
            "https://github.com/meywd/tamma/blob/main/docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md");
}
