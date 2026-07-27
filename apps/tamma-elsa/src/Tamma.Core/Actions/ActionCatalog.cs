using System.Collections.Frozen;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Actions;

/// <summary>
/// THE Action Catalog (Story 43-2 AC10–AC11): one closed, compile-checked index
/// of every consequential action Tamma can take, keyed by <see cref="ActionKey"/>
/// and partitioned by <see cref="ActionGroup"/>. The descriptor table lives in
/// <c>ActionCatalog.Descriptors.cs</c> — a hand-written array literal with
/// enum-referenced keys (never string literals), the <c>RolePhaseMap</c> posture;
/// no source generator (codegen is a stated repo non-goal) and no
/// reflection-at-startup authoring (the group assignment must be reviewable line
/// by line).
///
/// <para>
/// FAIL-LOUD AT STATIC INIT (the <c>PromptFileLoader</c> posture, 43-2 D7):
/// <see cref="BuildIndex"/> throws on any of eight inconsistency classes, each
/// with its own <c>ACTION.CATALOG.*</c> code naming the offending member —
/// adding an <see cref="AgentAction"/> member without a descriptor is a boot
/// failure, including in test hosts. Do not soften to log-and-continue: a
/// catalog that silently omits a member is the epic's core failure mode.
/// Both hosts must touch this type eagerly at composition (43-2 AC13 — the
/// <c>Program.cs</c> eager reads are wired outside this story's file lane).
/// </para>
///
/// <para>
/// LIMITATION (43-2 D9): for the <c>tool</c>/<c>effect</c>/<c>automation</c>/
/// <c>platform-task</c> planes the index validates against enums this same epic
/// authored; those planes are bound to REALITY by the reflection sweeps in
/// <c>Tamma.Activities.Tests/Actions/</c> and, fully, by Story 43-8's harnesses.
/// </para>
/// </summary>
public static partial class ActionCatalog
{
    /// <summary>
    /// The threshold applied to an action that somehow reaches the gate without a
    /// catalog entry: a person decides (epic decision D2 — allowed at RUNTIME
    /// through this fallback never being needed for enforcement decisions to
    /// stall; unmergeable in CI via the drift tests). Named constant, never a
    /// literal.
    /// </summary>
    public const int UnclassifiedFallback = AutonomyDial.AlwaysHuman;

    // NOTE: BuildDescriptors() is a METHOD in ActionCatalog.Descriptors.cs, not a
    // field — static field initialization order across partial-class files is
    // unspecified, and a field there could observe s_index initializing first.
    private static readonly IReadOnlyList<ActionDescriptor> s_descriptors = BuildDescriptors();

    private static readonly CatalogIndex s_index = BuildIndex(s_descriptors);

    /// <summary>Every catalogued descriptor, keyed by composite action key.</summary>
    public static FrozenDictionary<ActionKey, ActionDescriptor> ByKey => s_index.ByKey;

    /// <summary>
    /// The by-group index, PROJECTED from the descriptors (the
    /// <c>RolePhaseMap.s_rolesForAction</c> idiom) — never hand-maintained.
    /// </summary>
    public static FrozenDictionary<ActionGroup, FrozenSet<ActionKey>> ByGroup => s_index.ByGroup;

    /// <summary>All descriptors in declaration order (namespace, then wire).</summary>
    public static IReadOnlyList<ActionDescriptor> All => s_descriptors;

    /// <summary>Fail-loud lookup.</summary>
    /// <exception cref="TammaError">Code <c>ACTION.CATALOG.UNKNOWN_MEMBER</c>.</exception>
    public static ActionDescriptor Get(ActionKey key) =>
        s_index.ByKey.TryGetValue(key, out var descriptor)
            ? descriptor
            : throw new TammaError(
                "ACTION.CATALOG.UNKNOWN_MEMBER",
                $"'{key.ToWire()}' is not a catalogued action.",
                new Dictionary<string, object?> { ["key"] = key.ToWire() },
                retryable: false,
                severity: TammaErrorSeverity.High);

    /// <summary>Non-throwing lookup.</summary>
    public static bool TryGet(ActionKey key, out ActionDescriptor? descriptor)
    {
        var found = s_index.ByKey.TryGetValue(key, out var d);
        descriptor = d;
        return found;
    }

    /// <summary>The full wire set of the vocabulary owning <paramref name="ns"/>.</summary>
    public static IReadOnlyList<string> WiresOf(ActionNamespace ns) => ns switch
    {
        ActionNamespace.AgentAction => Enum.GetValues<AgentAction>().Select(a => a.ToWire()).ToArray(),
        ActionNamespace.DocumentType => Enum.GetValues<DocumentTypeKey>().Select(d => d.ToWire()).ToArray(),
        ActionNamespace.Tool => Enum.GetValues<ToolAction>().Select(t => t.ToWire()).ToArray(),
        ActionNamespace.Effect => Enum.GetValues<ExternalEffect>().Select(e => e.ToWire()).ToArray(),
        ActionNamespace.Automation => Enum.GetValues<BackgroundActor>().Select(b => b.ToWire()).ToArray(),
        ActionNamespace.PlatformTask => Enum.GetValues<PlatformTaskKind>().Select(p => p.ToWire()).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(ns), ns, "Unknown action namespace."),
    };

    /// <summary>Index pair built by <see cref="BuildIndex"/> (internal test seam).</summary>
    internal sealed record CatalogIndex(
        FrozenDictionary<ActionKey, ActionDescriptor> ByKey,
        FrozenDictionary<ActionGroup, FrozenSet<ActionKey>> ByGroup);

    /// <summary>
    /// Validates a descriptor table and builds the indexes. Internal so
    /// <c>ActionCatalogBuildIndexTests</c> can feed deliberately-bad arrays
    /// through the real code path (InternalsVisibleTo Tamma.Core.Tests). Eight
    /// distinct codes rather than one generic <c>ACTION.CATALOG.INVALID</c>: the
    /// failure lands at boot on a developer who has just added an enum member,
    /// and the message is the entire remediation UX (43-2 D8).
    /// </summary>
    internal static CatalogIndex BuildIndex(IReadOnlyList<ActionDescriptor> descriptors)
    {
        var byKey = new Dictionary<ActionKey, ActionDescriptor>();
        var siteKeys = new Dictionary<(ActionNamespace Ns, string SiteKey), ActionKey>();

        foreach (var d in descriptors)
        {
            if (!Enum.IsDefined(d.Key.Ns))
                throw Invalid("UNKNOWN_NAMESPACE_KEY",
                    $"Descriptor '{d.Key.Key}' carries undefined namespace value {(int)d.Key.Ns}.");

            if (!IsKnownKey(d.Key.Ns, d.Key.Key))
                throw Invalid("ORPHAN_DESCRIPTOR",
                    $"Descriptor '{d.Key.ToWire()}' has no backing member in the " +
                    $"'{d.Key.Ns.ToWire()}' vocabulary — delete the descriptor or add the member.");

            if (string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Summary)
                || string.IsNullOrWhiteSpace(d.SiteKey))
                throw Invalid("EMPTY_METADATA",
                    $"Descriptor '{d.Key.ToWire()}' has an empty Title, Summary or SiteKey.");

            if (!AutonomyDial.IsValidThreshold(d.DefaultMinAutonomy))
                throw Invalid("INVALID_DEFAULT",
                    $"Descriptor '{d.Key.ToWire()}' ships DefaultMinAutonomy {d.DefaultMinAutonomy}, " +
                    $"outside [{AutonomyDial.Min}, {AutonomyDial.Max}] ∪ {{{AutonomyDial.AlwaysHuman}}}.");

            if (!byKey.TryAdd(d.Key, d))
                throw Invalid("DUPLICATE_KEY",
                    $"Descriptor '{d.Key.ToWire()}' is declared more than once.");

            // Site uniqueness where a site is a distinct performing unit; tool is
            // exempt (git_operations.read/write share one executor), agent-action/
            // document-type are exempt (registry-declared vocabularies share their
            // registry site).
            if (d.Key.Ns is ActionNamespace.Effect or ActionNamespace.Automation or ActionNamespace.PlatformTask
                && !siteKeys.TryAdd((d.Key.Ns, d.SiteKey), d.Key))
                throw Invalid("DUPLICATE_SITE_KEY",
                    $"Descriptor '{d.Key.ToWire()}' repeats SiteKey '{d.SiteKey}' already used by " +
                    $"'{siteKeys[(d.Key.Ns, d.SiteKey)].ToWire()}'.");
        }

        // Totality: every member of every owning vocabulary has a descriptor.
        foreach (var ns in Enum.GetValues<ActionNamespace>())
        {
            foreach (var wire in WiresOf(ns))
            {
                if (!byKey.ContainsKey(new ActionKey(ns, wire)))
                    throw Invalid("MISSING_DESCRIPTOR",
                        $"'{ns.ToWire()}:{wire}' has no catalog descriptor — every vocabulary member " +
                        "must be catalogued (add the descriptor in ActionCatalog.Descriptors.cs).");
            }
        }

        // Partition: no group may rot into a dead label (43-3 AC1).
        var byGroup = Enum.GetValues<ActionGroup>()
            .ToDictionary(g => g, _ => new HashSet<ActionKey>());
        foreach (var d in byKey.Values)
            byGroup[d.Group].Add(d.Key);
        foreach (var (group, members) in byGroup)
        {
            if (members.Count == 0)
                throw Invalid("GROUP_EMPTY",
                    $"Action group '{group.ToWire()}' has zero members — a group must never be a dead label.");
        }

        return new CatalogIndex(
            byKey.ToFrozenDictionary(),
            byGroup.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet()));
    }

    private static bool IsKnownKey(ActionNamespace ns, string key) => ns switch
    {
        ActionNamespace.AgentAction => EnumWire<AgentAction>.TryParse(key, out _),
        ActionNamespace.DocumentType => EnumWire<DocumentTypeKey>.TryParse(key, out _),
        ActionNamespace.Tool => EnumWire<ToolAction>.TryParse(key, out _),
        ActionNamespace.Effect => EnumWire<ExternalEffect>.TryParse(key, out _),
        ActionNamespace.Automation => EnumWire<BackgroundActor>.TryParse(key, out _),
        ActionNamespace.PlatformTask => EnumWire<PlatformTaskKind>.TryParse(key, out _),
        _ => false,
    };

    private static TammaError Invalid(string code, string message) =>
        new(
            $"ACTION.CATALOG.{code}",
            message,
            retryable: false,
            severity: TammaErrorSeverity.Critical);
}
