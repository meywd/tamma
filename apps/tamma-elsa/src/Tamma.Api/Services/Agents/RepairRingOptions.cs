namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (AC2, AC9, Design Decision D8) — GLOBAL configuration for the
/// deterministic repair ring. There is deliberately NO per-call, per-workflow, or
/// per-prompt knob: the only lever is this options class, bound once from the
/// <c>RepairRing</c> config section in <c>Program.cs</c>.
///
/// <para><b>Hard cap by clamp, not validation (AC2).</b>
/// <see cref="EffectiveMaxRepairTurns"/> clamps <see cref="MaxRepairTurns"/> into
/// <c>[0, <see cref="HardCap"/>]</c>, so it is structurally impossible for any
/// configuration value — however large — to drive more than two in-conversation
/// repair turns. The tool-loop precedent showed the second identical correction
/// rarely converges; beyond the cap the correct escalation is the lifecycle's
/// review/revise ring, not more repair.</para>
///
/// <para><b>Dark by default (AC9).</b> <see cref="EnabledDocumentTypes"/> defaults
/// EMPTY: the mechanism ships OFF for every document type. Flipping a type ON
/// requires observed real-provider failure-rate evidence — recorded in a
/// <c>.dev/findings/</c> entry — before the extra turn is justified.</para>
/// </summary>
public sealed class RepairRingOptions
{
    /// <summary>The config section this options class binds from.</summary>
    public const string SectionName = "RepairRing";

    /// <summary>The absolute ceiling on in-conversation repair turns (AC2). No
    /// config value can exceed it — <see cref="EffectiveMaxRepairTurns"/> clamps.</summary>
    public const int HardCap = 2;

    /// <summary>Configured maximum repair turns (default 1). The EFFECTIVE value
    /// callers must use is <see cref="EffectiveMaxRepairTurns"/>, which enforces the
    /// hard cap; this raw setter can hold any value but never takes effect above
    /// <see cref="HardCap"/>.</summary>
    public int MaxRepairTurns { get; set; } = 1;

    /// <summary>
    /// The document-type KEYS (wire strings, e.g. <c>decomposition</c>) for which
    /// repair is enabled. Defaults EMPTY (AC9 — dark launch). Enabling a type
    /// requires a <c>.dev/findings/</c> entry with real-provider failure-rate
    /// evidence justifying the extra turn's token spend.
    /// </summary>
    public string[] EnabledDocumentTypes { get; set; } = Array.Empty<string>();

    /// <summary>The clamped, HARD-CAPPED maximum repair turns (AC2). This — never
    /// <see cref="MaxRepairTurns"/> — is what the ring is bounded by.</summary>
    public int EffectiveMaxRepairTurns => Math.Clamp(MaxRepairTurns, 0, HardCap);

    /// <summary>Whether repair is enabled for <paramref name="documentTypeKey"/>
    /// (case-insensitive membership of <see cref="EnabledDocumentTypes"/>).</summary>
    public bool IsEnabledFor(string documentTypeKey) =>
        EnabledDocumentTypes.Contains(documentTypeKey, StringComparer.OrdinalIgnoreCase);
}
