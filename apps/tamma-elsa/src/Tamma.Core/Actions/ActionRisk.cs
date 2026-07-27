using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// Coarse risk class of a catalogued action (Story 43-2 AC3). This is Epic 42's
/// <c>ToolPermissionClass</c> relocated and generalized (43-10 records the
/// supersession). Risk is ORTHOGONAL to <see cref="ActionGroup"/> — a group is
/// what an admin assigns as a unit; risk is how consequential one action is.
/// There is deliberately NO <c>Destructive → AlwaysHuman</c> shipped-default
/// invariant (Story 43-3 AC10): it is unenforceable at the override layer, and
/// the explicit defaults table already states everything it would.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ActionRisk>))]
public enum ActionRisk
{
    /// <summary>Observes state; changes nothing.</summary>
    [Wire("read-only")] ReadOnly,

    /// <summary>Creates or updates state that can be corrected afterwards.</summary>
    [Wire("mutating")] Mutating,

    /// <summary>Executes a process or externally-triggered run whose side effects are open-ended.</summary>
    [Wire("command")] Command,

    /// <summary>Deletes, tears down, or promotes in a way that is hard or impossible to undo.</summary>
    [Wire("destructive")] Destructive,
}

/// <summary><see cref="ActionRisk"/> wire helper.</summary>
public static class ActionRiskExtensions
{
    /// <summary>The canonical wire string for <paramref name="risk"/>.</summary>
    public static string ToWire(this ActionRisk risk) => EnumWire<ActionRisk>.ToWire(risk);
}
