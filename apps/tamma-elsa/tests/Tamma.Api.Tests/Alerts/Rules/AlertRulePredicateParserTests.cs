using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — unit tests for the predicate DSL parser.
/// Covers every op in the grammar + rejection cases with structured
/// field-path reporting.
/// </summary>
[TestFixture]
public class AlertRulePredicateParserTests
{
    // ── Grammar: always ────────────────────────────────────────

    [Test]
    public void Parse_Always_ReturnsAlwaysNode()
    {
        var ast = AlertRulePredicateParser.Parse("""{"op":"always"}""");
        ast.Should().BeOfType<AlertRulePredicate.Always>();
    }

    // ── Grammar: count_gte ─────────────────────────────────────

    [Test]
    public void Parse_CountGte_RequiredFieldsOk()
    {
        var ast = AlertRulePredicateParser.Parse(
            """{"op":"count_gte","window_seconds":300,"threshold":3}""");
        var c = ast.Should().BeOfType<AlertRulePredicate.CountGte>().Subject;
        c.WindowSeconds.Should().Be(300);
        c.Threshold.Should().Be(3);
        c.GroupBy.Should().ContainSingle().Which.Should().Be("tenantId");
    }

    [Test]
    public void Parse_CountGte_MissingWindow_ThrowsWithFieldPath()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"count_gte","threshold":3}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.window_seconds");
    }

    [Test]
    public void Parse_CountGte_NegativeThreshold_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"count_gte","window_seconds":60,"threshold":-1}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.threshold");
    }

    [Test]
    public void Parse_CountGte_CustomGroupBy_OverridesDefault()
    {
        var ast = AlertRulePredicateParser.Parse(
            """{"op":"count_gte","window_seconds":60,"threshold":2,"group_by":["workflowId","tenantId"]}""");
        var c = ast.Should().BeOfType<AlertRulePredicate.CountGte>().Subject;
        c.GroupBy.Should().Equal("workflowId", "tenantId");
    }

    [Test]
    public void Parse_CountGte_NonStringGroupByElement_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"count_gte","window_seconds":60,"threshold":2,"group_by":[42]}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.group_by[0]");
    }

    // ── Grammar: and / or ──────────────────────────────────────

    [Test]
    public void Parse_AndWithTwoClauses_Ok()
    {
        var ast = AlertRulePredicateParser.Parse("""
        {"op":"and","clauses":[
            {"op":"always"},
            {"op":"tag_eq","key":"severity","value":"critical"}
        ]}
        """);
        var and = ast.Should().BeOfType<AlertRulePredicate.And>().Subject;
        and.Clauses.Should().HaveCount(2);
    }

    [Test]
    public void Parse_AndWithEmptyClauses_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"and","clauses":[]}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.clauses");
    }

    [Test]
    public void Parse_OrWithOneClause_Ok()
    {
        var ast = AlertRulePredicateParser.Parse(
            """{"op":"or","clauses":[{"op":"always"}]}""");
        ast.Should().BeOfType<AlertRulePredicate.Or>()
            .Which.Clauses.Should().HaveCount(1);
    }

    [Test]
    public void Parse_NestedOp_ReportsPathIntoClause()
    {
        var act = () => AlertRulePredicateParser.Parse("""
        {"op":"and","clauses":[
            {"op":"count_gte","threshold":1}
        ]}
        """);
        // Inner node missing window_seconds → path should dive into
        // the clause.
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.clauses[0].window_seconds");
    }

    // ── Grammar: tag_eq / data_field_eq ────────────────────────

    [Test]
    public void Parse_TagEq_Ok()
    {
        var ast = AlertRulePredicateParser.Parse(
            """{"op":"tag_eq","key":"severity","value":"critical"}""");
        var t = ast.Should().BeOfType<AlertRulePredicate.TagEq>().Subject;
        t.Key.Should().Be("severity");
        t.Value.Should().Be("critical");
    }

    [Test]
    public void Parse_TagEq_MissingKey_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"tag_eq","value":"critical"}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$.key");
    }

    [Test]
    public void Parse_DataFieldEq_DottedPathOk()
    {
        var ast = AlertRulePredicateParser.Parse(
            """{"op":"data_field_eq","path":"foo.bar.baz","value":"42"}""");
        var d = ast.Should().BeOfType<AlertRulePredicate.DataFieldEq>().Subject;
        d.Path.Should().Be("foo.bar.baz");
        d.Value.Should().Be("42");
    }

    // ── Rejection: bad input ───────────────────────────────────

    [Test]
    public void Parse_EmptyObject_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse("{}");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Which.FieldPath.Should().Be("$");
    }

    [Test]
    public void Parse_UnknownOp_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse(
            """{"op":"dance"}""");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Where(ex => ex.Message.Contains("unknown op"));
    }

    [Test]
    public void Parse_NotJson_Throws()
    {
        var act = () => AlertRulePredicateParser.Parse("this is not json");
        act.Should().Throw<InvalidAlertRulePredicateException>()
            .Where(ex => ex.Message.Contains("not valid JSON"));
    }

    [Test]
    public void Parse_NullOrEmpty_Throws()
    {
        var act1 = () => AlertRulePredicateParser.Parse("");
        var act2 = () => AlertRulePredicateParser.Parse(null!);
        act1.Should().Throw<InvalidAlertRulePredicateException>();
        act2.Should().Throw<InvalidAlertRulePredicateException>();
    }
}
