using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The six planes of the Action Catalog's composite key (Story 43-2 AC1). Each
/// member names the vocabulary that owns the <see cref="ActionKey.Key"/> half of
/// a catalogued action; a flat ~153-member enum was rejected because it would
/// copy all 80 <see cref="AgentAction"/> wire strings into a second vocabulary —
/// the exact drift Epic 43 exists to prevent (design decision D1).
///
/// <para>
/// <c>agent-action</c> and <c>document-type</c> deliberately preserve
/// <see cref="Tamma.Core.Documents.Policy.EscalationClassKind"/>'s wire strings
/// byte-for-byte, so <c>agent-action:*</c>/<c>document-type:*</c> keys are a
/// strict superset of a vocabulary already persisted in
/// <c>acceptance_rules_overrides</c> (pinned by
/// <c>ActionNamespaceCompatibilityTests</c>).
/// </para>
///
/// <para>
/// PLACEMENT CONSTRAINT (43-2 D5): every enum in this family must live in
/// <c>Tamma.Core</c>, because <see cref="WireEnumJsonConverter{TEnum}"/> is
/// <c>internal</c> to this assembly — an enum declared in <c>Tamma.Api</c> could
/// not carry the converter attribute. Do not move the next one to another
/// assembly.
/// </para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ActionNamespace>))]
public enum ActionNamespace
{
    /// <summary>Key vocabulary: <see cref="AgentAction"/> wire strings (80 members).</summary>
    [Wire("agent-action")] AgentAction,

    /// <summary>Key vocabulary: <see cref="DocumentTypeKey"/> wire strings (10 members).</summary>
    [Wire("document-type")] DocumentType,

    /// <summary>Key vocabulary: <see cref="ToolAction"/> wire strings (8 members).</summary>
    [Wire("tool")] Tool,

    /// <summary>Key vocabulary: <see cref="ExternalEffect"/> wire strings (22 members).</summary>
    [Wire("effect")] Effect,

    /// <summary>Key vocabulary: <see cref="BackgroundActor"/> wire strings (25 members).</summary>
    [Wire("automation")] Automation,

    /// <summary>Key vocabulary: <see cref="PlatformTaskKind"/> wire strings (8 members).</summary>
    [Wire("platform-task")] PlatformTask,
}

/// <summary><see cref="ActionNamespace"/> wire helpers.</summary>
public static class ActionNamespaceExtensions
{
    /// <summary>The canonical wire string for <paramref name="ns"/>.</summary>
    public static string ToWire(this ActionNamespace ns) => EnumWire<ActionNamespace>.ToWire(ns);

    /// <summary>Case-sensitive (ordinal) lookup of the namespace for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out ActionNamespace ns) =>
        EnumWire<ActionNamespace>.TryParse(wire, out ns);
}
