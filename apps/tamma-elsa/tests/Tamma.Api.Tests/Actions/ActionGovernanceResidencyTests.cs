using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-5 (AC5/D3 + AC3/D5) — the two residency obligations in ONE file so
/// they are read together: <c>action_assignments</c> and
/// <c>action_authorizations</c> must be ON the strict control-plane entity
/// list and must NOT be on the destructive startup DROP list — plus the
/// no-numeric-CHECK pin on <c>MinAutonomy</c>.
///
/// <para>Reading source text in a test is unusual; it is justified because
/// the DROP list is a raw SQL string literal with no other reflectable
/// surface, and the failure mode it guards — every admin tightening silently
/// reverted on the next restart — is catastrophic and invisible.</para>
/// </summary>
[TestFixture]
public class ActionGovernanceResidencyTests
{
    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tamma.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must locate the repo root to read source files");
        return dir!;
    }

    private static string ProgramCsText() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Tamma.Api", "Program.cs"));

    private static string MigrationText()
    {
        var path = Directory
            .GetFiles(
                Path.Combine(RepoRoot(), "src", "Tamma.Data", "Migrations", "ControlPlane"),
                "*_AddActionGovernance.cs")
            .Single(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal));
        return File.ReadAllText(path);
    }

    [Test]
    public void Tables_AreNotInTheDestructiveDropList()
    {
        // Extract the DROP TABLE … CASCADE literal Program.cs executes on
        // every startup without TAMMA_PRESERVE_DB=1.
        var text = ProgramCsText();
        var match = Regex.Match(text, @"DROP TABLE IF EXISTS(?<tables>[\s\S]*?)CASCADE;",
            RegexOptions.IgnoreCase);
        match.Success.Should().BeTrue("the Epic 19 startup wipe literal must exist in Program.cs");

        var dropList = match.Groups["tables"].Value;
        foreach (var table in new[] { "action_assignments", "action_authorizations" })
        {
            dropList.Should().NotContain(table,
                because: "Story 43-5 AC5 — {0} is SAFETY POLICY, not operational data: it is the "
                + "only thing between an agent and a production deploy, and the DROP list runs on "
                + "every restart. Putting it on the list would silently revert every admin "
                + "tightening on the next deploy — a governance surface that lies. This exclusion "
                + "is deliberate and tested; do NOT add the table 'for consistency'.", table);
        }
    }

    [Test]
    public void Tables_AreOnTheStrictControlPlaneList()
    {
        // The other half of the pair: CP-model membership (an unlisted table
        // would also fail ControlPlaneDbContextModelTests' BeEquivalentTo; this
        // assertion keeps the two obligations legible side by side).
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=cp_model_only;Username=t;Password=t")
            .Options;
        using var ctx = new ControlPlaneDbContext(options);

        var tables = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToHashSet();

        tables.Should().Contain("action_assignments");
        tables.Should().Contain("action_authorizations");
    }

    [Test]
    public void Migration_HasNoNumericConstraintOnMinAutonomy()
    {
        // AC3/D5 — a CHECK on the threshold VALUE would live in a migration
        // snapshot forever: a second permanent hardcoding of the AutonomyDial
        // bound, defeating Story 43-1. Validation is domain-side
        // (AutonomyDial.IsValidThreshold at the endpoints).
        var text = MigrationText();

        text.Should().Contain("\"MinAutonomy\" integer NULL",
            "the column exists and is nullable");
        Regex.IsMatch(text, "\"MinAutonomy\"\\s*(>=|<=|>|<|BETWEEN)", RegexOptions.IgnoreCase)
            .Should().BeFalse(
                "no numeric bound on MinAutonomy may be frozen into the migration (43-5 AC3)");
        // The only CHECK mentioning the column is the mode-row null-pattern tie.
        Regex.Matches(text, "CONSTRAINT[^,]*MinAutonomy[^,]*").Count.Should().Be(1);
    }

    [Test]
    public void Migration_IsIdempotent_TheDropListExclusionConsequence()
    {
        // Excluded tables persist while the migration history is wiped, so the
        // migration re-runs against an existing table: its DDL must be
        // IF NOT EXISTS-idempotent or every second deploy dies with 42P07
        // (the provider_settings precedent).
        var text = MigrationText();

        Regex.Matches(text, "CREATE TABLE IF NOT EXISTS").Count.Should().Be(2);
        Regex.Matches(text, "CREATE (UNIQUE )?INDEX IF NOT EXISTS").Count.Should().Be(3);
        text.Should().NotContain("migrationBuilder.CreateTable(",
            "the model-diff DDL is not idempotent; the raw SQL is the executable truth "
            + "(the doc comment may NAME CreateTable; the code must not CALL it)");
        text.Should().NotContain("REFERENCES tenants",
            "an FK to a wiped table would cascade the surviving policy rows away");
        text.Should().NotContain("REFERENCES users");
    }

    [Test]
    public void ProgramCs_DropListComment_StillMarksExclusionsDeliberate()
    {
        // The DROP list carries a "DELIBERATE EXCLUSIONS" comment block; if a
        // refactor deletes it, the next reader loses the only in-situ warning.
        ProgramCsText().Should().Contain("DELIBERATE EXCLUSIONS",
            "the exclusion rationale must stay attached to the DROP list itself");
    }
}
