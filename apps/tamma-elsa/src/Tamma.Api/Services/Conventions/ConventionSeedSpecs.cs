using Tamma.Api.Services.Agents;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// A single system-default convention row to seed: the <c>(role, action)</c>
/// cell plus its transitional default <see cref="Body"/>.
/// </summary>
/// <param name="Role">Agent role wire string (e.g. <c>developer</c>).</param>
/// <param name="Action">Agent action wire string (e.g. <c>implement-feature</c>).</param>
/// <param name="Body">Non-empty system-default convention body (SPEC §3.5 transitional).</param>
public sealed record ConventionSeedSpec(string Role, string Action, string Body);

/// <summary>
/// Pure (DB-free) source of the convention system-default seed set.
///
/// <para><b>Single source of truth.</b> The <c>(role, action)</c> keyset is
/// derived by iterating <see cref="RolePhaseMap.EligibleActions"/> — the
/// IDENTICAL iteration <c>SystemPrompts.BuildRoleActionTemplates()</c> uses for
/// the prompt registry. Both the prompt seed (in code) and the convention seed
/// (DB rows) thus key off the same frozen taxonomy and CANNOT drift; the
/// anti-drift test (Story 27-16 AC2/AC5) asserts the three keysets — prompt
/// registry, convention seed, <see cref="RolePhaseMap.EligibleActions"/> — are
/// set-equal.</para>
///
/// <para>Exposed as a pure static method so the anti-drift test can call it
/// WITHOUT a database; the <see cref="ConventionStoreSeeder"/> consumes the same
/// list to upsert rows.</para>
///
/// <para><b>Transitional bodies (SPEC §3.5).</b> There is no authored
/// per-(role, action) convention content yet. Each cell ships a single
/// parameterised, HONEST, non-empty default that names the role + action and
/// instructs the agent to follow the project's own conventions, explicitly
/// marked as a customisable system default. It is deliberately NOT a fabricated
/// elaborate standard. The body MUST stay non-empty: once Story 27-9 implements
/// convention resolution under the locked mandate (tenant → system → error,
/// never empty/plain), a missing/empty system default would throw for a valid
/// <c>(role, action)</c> pair. A tenant admin replaces this with real
/// conventions via the Story 27-10 endpoint.</para>
/// </summary>
public static class ConventionSeedSpecs
{
    /// <summary>
    /// Build the full system-default seed set from the frozen taxonomy.
    ///
    /// <para>Deterministic ordering (AC4): iterates
    /// <see cref="RolePhaseMap.EligibleActions"/> (a <c>FrozenDictionary</c> of
    /// <c>FrozenSet</c>) in its stable enumeration order, so the same input
    /// always yields the same ordered list. The seeder is order-insensitive
    /// (it upserts keyed by <c>(role, action)</c>) but a stable order keeps
    /// test diffs and logs reproducible.</para>
    /// </summary>
    public static IReadOnlyList<ConventionSeedSpec> Build()
    {
        var specs = new List<ConventionSeedSpec>(RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count));

        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            foreach (var action in actions)
            {
                var actionWire = action.ToWire();
                specs.Add(new ConventionSeedSpec(roleWire, actionWire, DefaultBody(roleWire, actionWire)));
            }
        }

        return specs;
    }

    /// <summary>
    /// The code-baseline default body for a single typed <c>(role, action)</c>
    /// taxonomy cell — the canonical source the admin <c>ResetSystemDefaultAsync</c>
    /// re-applies (it restores a system default to exactly what a fresh seed
    /// would have written). Validates the pair against
    /// <see cref="RolePhaseMap.IsRoleEligibleForPhase"/> so a non-taxonomy cell
    /// (which has no baseline and is never seeded) is rejected rather than
    /// silently fabricating a body.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <c>(role, action)</c> is not a valid taxonomy cell — such a
    /// pair has no system default to reset to.
    /// </exception>
    public static string DefaultBodyFor(AgentRole role, AgentAction action)
    {
        var roleWire = role.ToWire();
        var actionWire = action.ToWire();
        if (!RolePhaseMap.IsRoleEligibleForPhase(actionWire, roleWire))
        {
            throw new ArgumentException(
                $"({roleWire}, {actionWire}) is not a taxonomy cell — it has no "
                + "seeded system default and therefore no baseline to reset to.",
                nameof(action));
        }

        return DefaultBody(roleWire, actionWire);
    }

    /// <summary>
    /// Transitional default convention body for a <c>(role, action)</c> cell
    /// (SPEC §3.5). A single parameterised template — honest, non-empty, and
    /// clearly marked as a system default a tenant admin should replace. NOT a
    /// fabricated standard.
    /// </summary>
    internal static string DefaultBody(string role, string action) =>
        $"Follow this project's established conventions when performing the " +
        $"'{action}' action as a {role}. Match the existing code style, naming, " +
        $"error-handling, testing, and documentation patterns already present in " +
        $"the repository; do not introduce new conventions unilaterally.\n\n" +
        $"(System default — transitional, SPEC §3.5. A tenant admin can " +
        $"replace this with project-specific {role}/{action} conventions.)";
}
