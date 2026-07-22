using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Story 39-11 — drift pins for the store status vocabulary (Design Decision D3).
/// The 7-member set + exact wire strings underpin the <c>ck_document_instances_status</c>
/// CHECK and the AC1/AC2/AC4 status filters; <see cref="DocumentInstanceStatusExtensions.FromState"/>
/// must be total over 39-2's <see cref="DocumentState"/> and never yield
/// <see cref="DocumentInstanceStatus.Superseded"/> (a store-only status set solely
/// by the revision write, D4).
/// </summary>
[TestFixture]
public class DocumentInstanceStatusTests
{
    [Test]
    public void Vocabulary_has_exactly_seven_members()
    {
        // Pinned by D3: adding/removing a member is a conscious edit that must also
        // update the CHECK constraint + migration. The 'in_review' + 'superseded'
        // store-only members are the two beyond the six lifecycle states.
        Enum.GetValues<DocumentInstanceStatus>().Should().HaveCount(7);
    }

    [TestCase(DocumentInstanceStatus.Draft, "draft")]
    [TestCase(DocumentInstanceStatus.Validated, "validated")]
    [TestCase(DocumentInstanceStatus.InReview, "in_review")]
    [TestCase(DocumentInstanceStatus.Accepted, "accepted")]
    [TestCase(DocumentInstanceStatus.Rejected, "rejected")]
    [TestCase(DocumentInstanceStatus.Superseded, "superseded")]
    [TestCase(DocumentInstanceStatus.Escalated, "escalated")]
    public void ToWire_and_Parse_round_trip_exact_strings(DocumentInstanceStatus status, string wire)
    {
        status.ToWire().Should().Be(wire);
        DocumentInstanceStatusExtensions.Parse(wire).Should().Be(status);
    }

    [Test]
    public void InReview_wire_carries_the_underscore()
    {
        // The only multi-word wire string — a drift here would silently break the
        // CHECK constraint match.
        DocumentInstanceStatus.InReview.ToWire().Should().Be("in_review");
    }

    [TestCase("in-review")]  // wrong separator
    [TestCase("Accepted")]   // wrong casing (ordinal)
    [TestCase("bogus")]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_throws_on_unknown_input(string input)
    {
        var act = () => DocumentInstanceStatusExtensions.Parse(input);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.STORE.UNKNOWN_STATUS");
    }

    [Test]
    public void FromState_is_total_over_every_DocumentState_and_never_superseded()
    {
        foreach (var state in Enum.GetValues<DocumentState>())
        {
            var mapped = DocumentInstanceStatusExtensions.FromState(state);
            mapped.Should().NotBe(DocumentInstanceStatus.Superseded,
                "superseded is store-only, set solely by the revision write (D4)");
        }
    }

    [TestCase(DocumentState.Draft, DocumentInstanceStatus.Draft)]
    [TestCase(DocumentState.Validated, DocumentInstanceStatus.Validated)]
    [TestCase(DocumentState.Reviewed, DocumentInstanceStatus.InReview)]
    [TestCase(DocumentState.Accepted, DocumentInstanceStatus.Accepted)]
    [TestCase(DocumentState.Rejected, DocumentInstanceStatus.Rejected)]
    [TestCase(DocumentState.Escalated, DocumentInstanceStatus.Escalated)]
    public void FromState_maps_each_state_per_D3(DocumentState state, DocumentInstanceStatus expected)
    {
        DocumentInstanceStatusExtensions.FromState(state).Should().Be(expected);
    }
}
