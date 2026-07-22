using System.Text.Json;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>Shared fixtures for the acceptance-policy test suites (Story 39-5).</summary>
internal static class AcceptanceTestData
{
    /// <summary>A known-valid rules record; override individual knobs via <c>with</c>.</summary>
    public static AcceptanceRules ValidRules() => AcceptanceDefaults.Rules;

    public static ReviewerSelection SingleArchitect() => new(
        Mode: ReviewerMode.SingleReviewer,
        ReviewerRole: AgentRole.Architect.ToWire(),
        PanelRoles: Array.Empty<string>(),
        Quorum: null,
        DecisionRule: ReviewDecisionRule.Unanimous);

    public static DocumentEnvelope Envelope(DocumentTypeKey type)
    {
        using var payload = JsonDocument.Parse("{}");
        return DocumentEnvelope.CreateDraft(
            type: type,
            schemaVersion: 1,
            issueId: "ISSUE-1",
            correlationId: "corr-1",
            producedBy: DocumentProducer.Create("architect", "plan-review", "plan-review"),
            payload: payload.RootElement);
    }

    public static ResolvedAcceptanceRules Resolved(DocumentTypeKey type) => new(
        Rules: AcceptanceDefaults.For(type),
        Source: AcceptanceRulesSource.SystemDefault,
        Version: 1,
        DocumentTypeKey: type.ToWire(),
        ResolvedAt: DateTimeOffset.UnixEpoch);
}
