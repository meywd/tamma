using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// State machine + lifecycle-enum drift tests (Story 39-2 AC2/AC7, Design
/// Decision D4/D5). Every legal transition allowed; a representative illegal set
/// rejected with both state names in the message; terminals and enum shapes
/// pinned.
/// </summary>
[TestFixture]
public class DocumentStateMachineTests
{
    // ---------------------------------------------------------------------
    // Enum shape pins (drift anchors)
    // ---------------------------------------------------------------------

    [Test]
    public void DocumentState_has_exactly_six_members() =>
        Enum.GetValues<DocumentState>().Length.Should().Be(6);

    [TestCase(DocumentState.Draft, "draft")]
    [TestCase(DocumentState.Validated, "validated")]
    [TestCase(DocumentState.Reviewed, "reviewed")]
    [TestCase(DocumentState.Accepted, "accepted")]
    [TestCase(DocumentState.Rejected, "rejected")]
    [TestCase(DocumentState.Escalated, "escalated")]
    public void DocumentState_wire_strings_are_canonical(DocumentState state, string wire) =>
        state.ToWire().Should().Be(wire);

    [Test]
    public void DocumentLifecycleOutcome_has_exactly_four_members() =>
        // The 39-6 drift anchor: the closed outcome set (D5).
        Enum.GetValues<DocumentLifecycleOutcome>().Length.Should().Be(4);

    [TestCase(DocumentLifecycleOutcome.ReviewUndecidable, "review-undecidable")]
    [TestCase(DocumentLifecycleOutcome.AmbiguityAboveThreshold, "ambiguity-above-threshold")]
    [TestCase(DocumentLifecycleOutcome.RoundsExhausted, "rounds-exhausted")]
    [TestCase(DocumentLifecycleOutcome.ValidationExhausted, "validation-exhausted")]
    public void DocumentLifecycleOutcome_wire_strings_are_canonical(DocumentLifecycleOutcome outcome, string wire) =>
        outcome.ToWire().Should().Be(wire);

    // ---------------------------------------------------------------------
    // Legal transitions — exhaustive over the declared map
    // ---------------------------------------------------------------------

    [Test]
    public void Every_pair_in_the_legal_map_is_allowed()
    {
        foreach (var (from, destinations) in DocumentStateMachine.LegalTransitions)
        {
            foreach (var to in destinations)
            {
                DocumentStateMachine.CanTransition(from, to)
                    .Should().BeTrue($"'{from.ToWire()}' -> '{to.ToWire()}' is in the legal map");

                var assert = () => DocumentStateMachine.AssertTransition(from, to);
                assert.Should().NotThrow();
            }
        }
    }

    [Test]
    public void Legal_map_matches_the_D4_specification()
    {
        DocumentStateMachine.LegalTransitions[DocumentState.Draft]
            .Should().BeEquivalentTo(new[] { DocumentState.Validated, DocumentState.Escalated });
        DocumentStateMachine.LegalTransitions[DocumentState.Validated]
            .Should().BeEquivalentTo(new[] { DocumentState.Reviewed, DocumentState.Escalated });
        DocumentStateMachine.LegalTransitions[DocumentState.Reviewed]
            .Should().BeEquivalentTo(new[] { DocumentState.Accepted, DocumentState.Rejected, DocumentState.Escalated });
        DocumentStateMachine.LegalTransitions[DocumentState.Accepted].Should().BeEmpty();
        DocumentStateMachine.LegalTransitions[DocumentState.Rejected].Should().BeEmpty();
        DocumentStateMachine.LegalTransitions[DocumentState.Escalated].Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Illegal transitions — rejected, naming both states
    // ---------------------------------------------------------------------

    [Test]
    [TestCase(DocumentState.Draft, DocumentState.Accepted)]
    [TestCase(DocumentState.Draft, DocumentState.Reviewed)]
    [TestCase(DocumentState.Draft, DocumentState.Rejected)]
    [TestCase(DocumentState.Validated, DocumentState.Accepted)]
    [TestCase(DocumentState.Validated, DocumentState.Draft)]
    [TestCase(DocumentState.Reviewed, DocumentState.Draft)]
    [TestCase(DocumentState.Accepted, DocumentState.Rejected)]
    [TestCase(DocumentState.Accepted, DocumentState.Draft)]
    [TestCase(DocumentState.Rejected, DocumentState.Escalated)]
    [TestCase(DocumentState.Escalated, DocumentState.Accepted)]
    // self-transitions
    [TestCase(DocumentState.Draft, DocumentState.Draft)]
    [TestCase(DocumentState.Reviewed, DocumentState.Reviewed)]
    public void Illegal_transition_is_rejected_naming_both_states(DocumentState from, DocumentState to)
    {
        DocumentStateMachine.CanTransition(from, to).Should().BeFalse();

        var act = () => DocumentStateMachine.AssertTransition(from, to);
        var error = act.Should().Throw<TammaError>()
            .Which;
        error.Code.Should().Be("DOCUMENT.STATE.ILLEGAL_TRANSITION");
        error.Message.Should().Contain(from.ToWire()).And.Contain(to.ToWire());
    }

    // ---------------------------------------------------------------------
    // Terminals
    // ---------------------------------------------------------------------

    [Test]
    public void Terminal_states_are_accepted_rejected_escalated()
    {
        var terminals = Enum.GetValues<DocumentState>()
            .Where(DocumentStateMachine.IsTerminal)
            .ToArray();

        terminals.Should().BeEquivalentTo(new[]
        {
            DocumentState.Accepted, DocumentState.Rejected, DocumentState.Escalated,
        });
    }

    [Test]
    [TestCase(DocumentState.Draft)]
    [TestCase(DocumentState.Validated)]
    [TestCase(DocumentState.Reviewed)]
    public void Non_terminal_states_are_not_terminal(DocumentState state) =>
        DocumentStateMachine.IsTerminal(state).Should().BeFalse();

    [Test]
    public void Escalated_is_reachable_from_every_non_terminal_state()
    {
        // D4: the typed unhandleable outcomes escalate from different stages.
        DocumentStateMachine.CanTransition(DocumentState.Draft, DocumentState.Escalated).Should().BeTrue();
        DocumentStateMachine.CanTransition(DocumentState.Validated, DocumentState.Escalated).Should().BeTrue();
        DocumentStateMachine.CanTransition(DocumentState.Reviewed, DocumentState.Escalated).Should().BeTrue();
    }
}
