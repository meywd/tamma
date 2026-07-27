using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Audit;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Descriptor-level metadata invariants (Story 43-2 AC9/AC11 + the epic README's
/// answered open questions): non-empty UI copy, site uniqueness where a site is
/// a distinct performing unit, the non-escalatable automation plane (Seam D is
/// deny-only), the informational-only secret reveal, and the optional compliance
/// join resolving against <see cref="SensitiveActionCatalog"/>.
/// </summary>
[TestFixture]
public class ActionDescriptorMetadataTests
{
    [Test]
    public void No_descriptor_has_empty_title_summary_or_site()
    {
        foreach (var d in ActionCatalog.All)
        {
            d.Title.Should().NotBeNullOrWhiteSpace(d.Key.ToWire());
            d.Summary.Should().NotBeNullOrWhiteSpace(d.Key.ToWire());
            d.SiteKey.Should().NotBeNullOrWhiteSpace(d.Key.ToWire());
        }
    }

    [Test]
    public void Site_keys_are_unique_within_effect_automation_and_platform_task()
    {
        // Each member of these planes is a distinct performing unit (route,
        // hosted-service class, task handler). tool is exempt (the two
        // git_operations.* members share one executor); agent-action and
        // document-type are exempt (registry-declared vocabularies share their
        // registry site).
        foreach (var ns in new[] { ActionNamespace.Effect, ActionNamespace.Automation, ActionNamespace.PlatformTask })
        {
            var sites = ActionCatalog.All.Where(d => d.Key.Ns == ns).Select(d => d.SiteKey).ToArray();
            sites.Should().OnlyHaveUniqueItems($"namespace '{ns.ToWire()}'");
        }
    }

    [Test]
    public void Every_automation_member_is_non_escalatable()
    {
        // Seam D (43-9) can only DENY: a sweeper cannot suspend for a person.
        // Asserted here so the property is true before anything relies on it —
        // the 43-6 API will reject mid-range thresholds on non-escalatable
        // members and the UI renders a two-state control.
        ActionCatalog.All.Where(d => d.Key.Ns == ActionNamespace.Automation)
            .Should().OnlyContain(d => !d.EscalatableToHuman);
    }

    [Test]
    public void Every_non_automation_member_is_escalatable()
    {
        ActionCatalog.All.Where(d => d.Key.Ns != ActionNamespace.Automation)
            .Should().OnlyContain(d => d.EscalatableToHuman);
    }

    [Test]
    public void SecretReveal_is_the_only_unenforceable_member()
    {
        // Epic README open question 2, ANSWERED 2026-07-25: reading a secret
        // never requires a human — the reveal is how an already-authorized action
        // gets its credential and can fire many times per run. What governs a
        // secret is the ACTION that needs it. The catalog row is informational
        // only; no admin-raised threshold on it may ever be enforced. Modelled as
        // a descriptor property (Enforceable=false), exactly as the answer
        // requires of 43-2.
        var unenforceable = ActionCatalog.All.Where(d => !d.Enforceable).Select(d => d.Key.ToWire());

        unenforceable.Should().BeEquivalentTo(new[] { "effect:secret.reveal" });
    }

    [Test]
    public void Every_sensitive_action_join_resolves_in_the_compliance_catalog()
    {
        // 43-2 D12: the optional join keeps SensitiveActionCatalog the compliance
        // artifact and this catalog the authorization artifact; a join that does
        // not resolve is a typo, not a policy.
        foreach (var d in ActionCatalog.All.Where(d => d.SensitiveActionCode is not null))
        {
            SensitiveActionCatalog.ByCode.Should().ContainKey(d.SensitiveActionCode!,
                $"descriptor '{d.Key.ToWire()}' joins a code that must exist");
        }
    }

    [Test]
    public void Destructive_members_are_marked_irreversible()
    {
        ActionCatalog.All.Where(d => d.Risk == ActionRisk.Destructive)
            .Should().OnlyContain(d => !d.Reversible,
                "a destructive action that claims reversibility is mislabelled");
    }
}
