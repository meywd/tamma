namespace Tamma.Activities.Security;

/// <summary>
/// Configuration options for ActionGate.
/// Bound from "Security:ActionGate" config section.
/// </summary>
public class ActionGateOptions
{
    /// <summary>
    /// Additional regex patterns to block, beyond the built-in defaults.
    /// Each string is compiled as a case-insensitive regex.
    /// </summary>
    public List<string> AdditionalBlockedPatterns { get; set; } = new();
}
