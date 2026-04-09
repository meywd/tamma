using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

[TestFixture]
public class PlanValidationTests
{
    [Test]
    public void ValidPlan_WithTasksAndFileMap_ReturnsValid()
    {
        var json = """{"tasks": [{"id": "T1"}], "fileMap": {"src/foo.ts": "create"}}""";
        var (planJson, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
        planJson.Should().Be(json);
    }

    [Test]
    public void ValidPlan_WithStepsAndFiles_ReturnsValid()
    {
        var json = """{"steps": [{"step": 1}], "files": ["src/bar.ts"]}""";
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Test]
    public void ValidPlan_WithTasksAndFilesToModify_ReturnsValid()
    {
        var json = """{"tasks": [{"id": "T1"}], "filesToModify": ["src/baz.ts"]}""";
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Test]
    public void EmptyResponse_ReturnsInvalid()
    {
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan("");

        isValid.Should().BeFalse();
        errors.Should().Contain("Empty plan");
    }

    [Test]
    public void NullWhitespace_ReturnsInvalid()
    {
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan("   ");

        isValid.Should().BeFalse();
        errors.Should().Contain("Empty plan");
    }

    [Test]
    public void EmptyJsonObject_ReturnsInvalid()
    {
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan("{}");

        isValid.Should().BeFalse();
        errors.Should().Contain("Empty plan");
    }

    [Test]
    public void InvalidJson_ReturnsInvalid()
    {
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan("{not valid json}");

        isValid.Should().BeFalse();
        errors.Should().Contain("Invalid JSON");
    }

    [Test]
    public void MissingTasksField_ReturnsInvalid()
    {
        var json = """{"fileMap": {"src/foo.ts": "create"}}""";
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeFalse();
        errors.Should().Contain("Missing 'tasks' or 'steps'");
    }

    [Test]
    public void MissingFileMapField_ReturnsInvalid()
    {
        var json = """{"tasks": [{"id": "T1"}]}""";
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeFalse();
        errors.Should().Contain("Missing file map");
    }

    [Test]
    public void JsonWrappedInMarkdown_ExtractsCorrectly()
    {
        var response = """
            Here is the plan:
            ```json
            {"tasks": [{"id": "T1"}], "fileMap": {"src/foo.ts": "create"}}
            ```
            Done.
            """;
        var (planJson, isValid, _) = PlanValidationHelper.ValidatePlan(response);

        isValid.Should().BeTrue();
        planJson.Should().Contain("tasks");
    }

    [Test]
    public void JsonWithLeadingText_ExtractsFirstBrace()
    {
        var response = """Sure! Here's the plan: {"tasks": [{"id": "T1"}], "files": ["a.ts"]}""";
        var (planJson, isValid, _) = PlanValidationHelper.ValidatePlan(response);

        isValid.Should().BeTrue();
        planJson.Should().StartWith("{");
        planJson.Should().EndWith("}");
    }

    [Test]
    public void ExtractJson_NoJsonBlock_ReturnsEmpty()
    {
        var result = PlanValidationHelper.ExtractJson("no json here");
        result.Should().BeEmpty();
    }

    [Test]
    public void BothErrors_MissingTasksAndFileMap_ReportsAll()
    {
        var json = """{"description": "something"}""";
        var (_, isValid, errors) = PlanValidationHelper.ValidatePlan(json);

        isValid.Should().BeFalse();
        errors.Should().Contain("Missing 'tasks' or 'steps'");
        errors.Should().Contain("Missing file map");
    }
}
