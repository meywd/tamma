namespace Tamma.Core.Actions;

/// <summary>
/// Story 43-8 (AC4) — declares that the decorated method is a PERFORMING SITE for
/// one <see cref="ExternalEffect"/> member of the Action Catalog. Read by
/// <c>MediationClientEffectSweepTests</c> (the bidirectional mediation-client
/// harness) and, once Story 43-9 lands, by the enforcement seam that evaluates the
/// gate before the effect fires.
///
/// <para>
/// WHAT THIS ATTRIBUTE CANNOT DO (43-8 AC10(b), D6 — stated here so a green sweep
/// is not read as a stronger guarantee than it is): it binds a <b>site</b>, not an
/// <b>effect</b>. Nothing verifies that the declared member is the effect the method
/// actually causes — <c>[PerformsEffect(ExternalEffect.GitBranchDelete)]</c> on a
/// method that creates a release passes every harness. The route plane has a
/// structural check for this (<c>ActionDescriptor.SiteKey</c> must equal
/// <c>"{METHOD} {routePattern}"</c>); the method plane has no equivalent, because a
/// C# method has no route pattern to compare against. The mitigation is review of
/// the (few, enumerable) attributed methods, not a test.
/// </para>
///
/// <para>
/// Likewise, a NEW capability grown inside an ALREADY-attributed method is invisible
/// to every harness in this epic: the method still carries exactly one attribute and
/// still names one effect.
/// </para>
/// </summary>
/// <param name="effect">The catalogued <c>effect:*</c> member this site performs.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PerformsEffectAttribute(ExternalEffect effect) : Attribute
{
    /// <summary>The catalogued <c>effect:*</c> member this site performs.</summary>
    public ExternalEffect Effect { get; } = effect;

    /// <summary>The composite catalog address of <see cref="Effect"/>.</summary>
    public ActionKey Key => new(ActionNamespace.Effect, Effect.ToWire());
}
