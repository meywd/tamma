using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC2 — the closed 8-member status vocabulary, count-pinned with
/// its wire strings pinned literally (the <c>ck_work_items_status</c> CHECK
/// contract 44-1 must mirror).
/// </summary>
[TestFixture]
public class WorkItemStatusTests
{
    [Test]
    public void Member_count_is_pinned()
    {
        Enum.GetValues<WorkItemStatus>().Should().HaveCount(8);

        // The exact eight wire strings 44-1's ck_work_items_status CHECK must
        // mirror — multi-word wires use '_' exactly as DocumentInstanceStatus's
        // in_review does.
        Enum.GetValues<WorkItemStatus>().Select(s => s.ToWire()).Should().Equal(
            "triage", "backlog", "ready", "in_progress", "in_review", "blocked", "done", "cancelled");
    }

    [Test]
    public void Triage_is_a_member()
    {
        // The member most likely to be "tidied away" by someone who has not
        // read D3: without it, imported/agent-filed items merge "nobody has
        // decided" into backlog's "we decided not now". Removing it later is a
        // fleet-wide migration, not a cleanup.
        WorkItemStatusExtensions.TryParse("triage", out var status).Should().BeTrue();
        status.Should().Be(WorkItemStatus.Triage);
    }

    [Test]
    public void Roundtrip_holds_for_every_member()
    {
        foreach (var status in Enum.GetValues<WorkItemStatus>())
        {
            WorkItemStatusExtensions.TryParse(status.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(status);
            WorkItemStatusExtensions.Parse(status.ToWire()).Should().Be(status);
        }

        // Ordinal: no coercion of casing or separators.
        WorkItemStatusExtensions.TryParse("In_Progress", out _).Should().BeFalse();
        WorkItemStatusExtensions.TryParse("in-progress", out _).Should().BeFalse();
    }

    [Test]
    public void Unknown_status_fails_loud()
    {
        var act = () => WorkItemStatusExtensions.Parse("superseded");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.UNKNOWN_STATUS");
    }

    [Test]
    public void Wire_set_is_deliberately_non_identical_to_DocumentInstanceStatus()
    {
        // D2: a work-item status describes a thing to be done, a document
        // status a revision's review position. The sets are deliberately
        // non-identical so a mistaken TryParse across them FAILS rather than
        // succeeding wrongly.
        foreach (var wire in new[] { "triage", "ready", "blocked", "done", "cancelled", "backlog" })
            EnumWire<DocumentInstanceStatus>.TryParse(wire, out _)
                .Should().BeFalse(because: $"'{wire}' must not be a document status");

        foreach (var wire in new[] { "validated", "superseded", "escalated", "draft", "accepted", "rejected" })
            WorkItemStatusExtensions.TryParse(wire, out _)
                .Should().BeFalse(because: $"'{wire}' must not be a work-item status");
    }
}
