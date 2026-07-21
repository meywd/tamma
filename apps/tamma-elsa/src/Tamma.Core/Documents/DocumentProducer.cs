using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// Producer provenance for a <see cref="DocumentEnvelope"/> (Story 39-2 AC1;
/// Design Decision D7). Records which agent role + action + workflow produced the
/// document, expressed in exactly the <c>llm-call</c> dispatch vocabulary
/// (<see cref="AgentRole"/> / <see cref="AgentAction"/> wire strings), never free
/// strings.
///
/// <para>
/// Every property carries an explicit <c>[JsonPropertyName]</c> (Design Decision
/// D8) so the wire contract is deliberate, not an accident of C# naming.
/// </para>
/// </summary>
public sealed record DocumentProducer
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("workflow")]
    public required string WorkflowDefinitionId { get; init; }

    /// <summary>
    /// Validate and construct producer provenance. Role and action are parsed
    /// strictly through the agent taxonomy (throw on unknown), the (action, role)
    /// pair is asserted eligible via <see cref="RolePhaseMap.IsRoleEligibleForPhase"/>,
    /// and the workflow id is validated structurally as a non-empty kebab token.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.PRODUCER.INVALID</c> for an unknown role/action, an
    /// ineligible (role, action) pair, or a malformed workflow definition id.
    /// </exception>
    public static DocumentProducer Create(string role, string action, string workflowDefinitionId)
    {
        // Role + action must be canonical taxonomy wire strings. AgentRole/Action
        // Parse throw ArgumentException on unknown; rethrow as the typed
        // registry-facing error (D7).
        string roleWire;
        string actionWire;
        try
        {
            roleWire = AgentRoleExtensions.Parse(role).ToWire();
            actionWire = AgentActionExtensions.Parse(action).ToWire();
        }
        catch (ArgumentException ex)
        {
            throw new TammaError(
                "DOCUMENT.PRODUCER.INVALID",
                $"Invalid producer provenance: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["action"] = action,
                    ["workflow"] = workflowDefinitionId,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // The (action, role) pair must be a taxonomy-valid cell. NOTE the param
        // order: IsRoleEligibleForPhase(phase, role) — i.e. (action, role).
        if (!RolePhaseMap.IsRoleEligibleForPhase(actionWire, roleWire))
        {
            throw new TammaError(
                "DOCUMENT.PRODUCER.INVALID",
                $"Invalid producer provenance: role '{roleWire}' is not eligible for action '{actionWire}'.",
                new Dictionary<string, object?>
                {
                    ["role"] = roleWire,
                    ["action"] = actionWire,
                    ["workflow"] = workflowDefinitionId,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // Tamma.Core cannot reference Tamma.ElsaServer to enumerate real
        // DefinitionIds (dependency direction), so validate the workflow id
        // structurally only (D7): non-empty kebab-case.
        if (!KebabCase.IsKebab(workflowDefinitionId))
        {
            throw new TammaError(
                "DOCUMENT.PRODUCER.INVALID",
                $"Invalid producer provenance: workflow definition id '{workflowDefinitionId}' is not a valid kebab-case token.",
                new Dictionary<string, object?>
                {
                    ["role"] = roleWire,
                    ["action"] = actionWire,
                    ["workflow"] = workflowDefinitionId,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        return new DocumentProducer
        {
            Role = roleWire,
            Action = actionWire,
            WorkflowDefinitionId = workflowDefinitionId,
        };
    }
}
