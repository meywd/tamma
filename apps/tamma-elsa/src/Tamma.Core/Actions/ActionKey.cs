namespace Tamma.Core.Actions;

/// <summary>
/// The composite address of one catalogued action: a namespace (which vocabulary
/// owns the key) plus the owning vocabulary's wire string (Story 43-2 AC2).
/// Wire form is <c>"{namespace}:{key}"</c>, e.g. <c>"agent-action:deploy"</c>,
/// <c>"tool:file_write"</c>.
///
/// <para>
/// Parsing splits on the FIRST <c>':'</c>, ordinal (43-2 D6):
/// <c>git_operations.read</c> contains a <c>'.'</c> but no <c>':'</c>; nothing in
/// any key vocabulary contains a <c>':'</c>, and a first-<c>':'</c> split keeps
/// the parser total even if one later does. Casing is ordinal-strict, matching
/// <c>EnumWire</c>'s posture — non-canonical casing is rejected, never silently
/// accepted.
/// </para>
/// </summary>
public readonly record struct ActionKey(ActionNamespace Ns, string Key)
{
    /// <summary>The canonical wire form: <c>"{namespace}:{key}"</c>.</summary>
    public string ToWire() => $"{Ns.ToWire()}:{Key}";

    /// <summary>
    /// Fail-loud parse of a wire form. Throws <see cref="TammaError"/> code
    /// <c>ACTION.KEY.INVALID</c> (severity High, non-retryable) on a missing
    /// colon, an empty namespace, an empty key, or an unknown namespace wire.
    /// The API layer should prefer <see cref="TryParse"/> (43-6 returns 400 on a
    /// bad wire; it must not need to catch).
    /// </summary>
    /// <exception cref="TammaError">Code <c>ACTION.KEY.INVALID</c>.</exception>
    public static ActionKey Parse(string wire)
    {
        if (TryParse(wire, out var key)) return key;

        throw new TammaError(
            "ACTION.KEY.INVALID",
            $"'{wire}' is not a valid action key; expected '{{namespace}}:{{key}}' with a known namespace wire.",
            new Dictionary<string, object?> { ["wire"] = wire },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>Non-throwing variant of <see cref="Parse"/>.</summary>
    public static bool TryParse(string? wire, out ActionKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(wire)) return false;

        var separator = wire.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == wire.Length - 1) return false;

        var nsWire = wire[..separator];
        var actionKey = wire[(separator + 1)..];
        if (!ActionNamespaceExtensions.TryParse(nsWire, out var ns)) return false;

        key = new ActionKey(ns, actionKey);
        return true;
    }
}
