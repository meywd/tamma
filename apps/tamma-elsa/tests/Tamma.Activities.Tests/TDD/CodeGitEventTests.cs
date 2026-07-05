using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TDD;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.TDD;

/// <summary>
/// Story 4-5 (AC1 <c>CodeFileWrittenEvent</c> / AC2 <c>CommitCreatedEvent</c>) —
/// the code-change + commit DCB event-capture coverage. Asserts the pure event
/// builders (<see cref="CodeEvents"/> / <see cref="CommitEvents"/>) that the RED /
/// GREEN / REFACTOR / commit activities emit through <c>TammaEventEmitter</c>:
/// correct <c>AGGREGATE.ACTION.STATUS</c> type, status, tags (issue/story/session/
/// branch/repo/sha), and files-changed data — plus the loud FAILED edge and the
/// null-tenant single-user path (no throw). Also the TAMMA001 cutover proof: the
/// coverage adds NO credential-holding integration-service injection.
/// </summary>
[TestFixture]
public class CodeGitEventTests
{
    private static readonly Guid Session = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ===================================================================
    // CODE.GENERATED.* (AC1 — RED test authoring + GREEN implementation)
    // ===================================================================

    [Test]
    public void BuildGenerated_ImplementationSuccess_Emits_CodeGeneratedSuccess_WithTagsAndFiles()
    {
        var files = new[] { "src/foo.ts", "src/bar.ts" };
        var evt = CodeEvents.BuildGenerated(
            success: true, storyId: "story-4-5", sessionId: Session,
            operation: CodeEvents.OperationImplementation, files: files,
            testCount: null, error: null);

        evt.EventType.Should().Be(CodeEvents.GeneratedSuccess);
        evt.Status.Should().Be("success");
        evt.Error.Should().BeNull();
        evt.Tags.Should().ContainKey("storyId").WhoseValue.Should().Be("story-4-5");
        evt.Tags.Should().ContainKey("sessionId").WhoseValue.Should().Be(Session.ToString("D"));
        evt.Tags.Should().ContainKey("operation").WhoseValue.Should().Be("implementation");
        evt.Data.Should().ContainKey("fileCount").WhoseValue.Should().Be(2);
        evt.Data.Should().ContainKey("source").WhoseValue.Should().Be("ai_generated");
        evt.Data["files"].Should().BeEquivalentTo(files);
        CodeEvents.IsFailureType(evt.EventType).Should().BeFalse();
    }

    [Test]
    public void BuildGenerated_TestingSuccess_CarriesTestCount_AndTestingOperation()
    {
        var evt = CodeEvents.BuildGenerated(
            success: true, storyId: "story-4-5", sessionId: Session,
            operation: CodeEvents.OperationTesting, files: new[] { "src/foo.test.ts" },
            testCount: 7, error: null);

        evt.EventType.Should().Be(CodeEvents.GeneratedSuccess);
        evt.Tags.Should().ContainKey("operation").WhoseValue.Should().Be("testing");
        evt.Data.Should().ContainKey("operation").WhoseValue.Should().Be("testing");
        evt.Data.Should().ContainKey("testCount").WhoseValue.Should().Be(7);
    }

    [Test]
    public void BuildGenerated_Failure_Emits_CodeGeneratedFailed_LoudWithReason()
    {
        var evt = CodeEvents.BuildGenerated(
            success: false, storyId: "story-4-5", sessionId: Session,
            operation: CodeEvents.OperationImplementation, files: null,
            testCount: null, error: "LLM returned empty code");

        evt.EventType.Should().Be(CodeEvents.GeneratedFailed);
        evt.Status.Should().Be("error");
        evt.Error.Should().Be("LLM returned empty code");
        evt.Data.Should().ContainKey("reason").WhoseValue.Should().Be("LLM returned empty code");
        evt.Data.Should().ContainKey("fileCount").WhoseValue.Should().Be(0);
        CodeEvents.IsFailureType(evt.EventType).Should().BeTrue(
            "a failed code generation is a loud error-status audit row, never a silent success");
    }

    // ===================================================================
    // CODE.REFACTORED.* (AC1 — REFACTOR phase)
    // ===================================================================

    [Test]
    public void BuildRefactored_Success_Emits_CodeRefactoredSuccess_WithRefactoringOperation()
    {
        var evt = CodeEvents.BuildRefactored(
            success: true, storyId: "story-4-5", sessionId: Session,
            files: new[] { "src/foo.ts" }, error: null);

        evt.EventType.Should().Be(CodeEvents.RefactoredSuccess);
        evt.Status.Should().Be("success");
        evt.Tags.Should().ContainKey("operation").WhoseValue.Should().Be("refactoring");
        evt.Data.Should().ContainKey("operation").WhoseValue.Should().Be("refactoring");
        evt.Data.Should().ContainKey("fileCount").WhoseValue.Should().Be(1);
    }

    [Test]
    public void BuildRefactored_Failure_Emits_CodeRefactoredFailed_Loud()
    {
        var evt = CodeEvents.BuildRefactored(
            success: false, storyId: "story-4-5", sessionId: Session,
            files: null, error: "refactor broke tests");

        evt.EventType.Should().Be(CodeEvents.RefactoredFailed);
        evt.Status.Should().Be("error");
        evt.Data.Should().ContainKey("reason").WhoseValue.Should().Be("refactor broke tests");
        CodeEvents.IsFailureType(evt.EventType).Should().BeTrue();
    }

    // ===================================================================
    // Null-tenant / single-user path — no story/session must not throw and
    // must simply omit those tags (platform-scope).
    // ===================================================================

    [Test]
    public void BuildGenerated_NoStoryOrSession_OmitsThoseTags_NoThrow()
    {
        var evt = CodeEvents.BuildGenerated(
            success: true, storyId: null, sessionId: Guid.Empty,
            operation: CodeEvents.OperationImplementation, files: Array.Empty<string>(),
            testCount: null, error: null);

        evt.EventType.Should().Be(CodeEvents.GeneratedSuccess);
        evt.Tags.Should().NotContainKey("storyId");
        evt.Tags.Should().NotContainKey("sessionId");
        evt.Tags.Should().ContainKey("operation");
    }

    // ===================================================================
    // COMMIT.CREATED.* (AC2)
    // ===================================================================

    [Test]
    public void BuildCreated_Success_Emits_CommitCreatedSuccess_WithShaBranchAndFileCount()
    {
        var files = new[] { "src/foo.ts", "src/foo.test.ts" };
        var evt = CommitEvents.BuildCreated(
            success: true, storyId: "story-4-5", sessionId: Session,
            sha: "abc1234def", message: "feat(story-4-5): task [TDD]",
            branch: "feature/story-4-5", repository: "acme/widgets",
            files: files, error: null);

        evt.EventType.Should().Be(CommitEvents.CreatedSuccess);
        evt.Status.Should().Be("success");
        evt.Error.Should().BeNull();
        evt.Tags.Should().ContainKey("sha").WhoseValue.Should().Be("abc1234def");
        evt.Tags.Should().ContainKey("branch").WhoseValue.Should().Be("feature/story-4-5");
        evt.Tags.Should().ContainKey("repository").WhoseValue.Should().Be("acme/widgets");
        evt.Tags.Should().ContainKey("storyId").WhoseValue.Should().Be("story-4-5");
        evt.Tags.Should().ContainKey("sessionId").WhoseValue.Should().Be(Session.ToString("D"));
        evt.Data.Should().ContainKey("sha").WhoseValue.Should().Be("abc1234def");
        evt.Data.Should().ContainKey("message").WhoseValue.Should().Be("feat(story-4-5): task [TDD]");
        evt.Data.Should().ContainKey("branch").WhoseValue.Should().Be("feature/story-4-5");
        evt.Data.Should().ContainKey("fileCount").WhoseValue.Should().Be(2);
        evt.Data["files"].Should().BeEquivalentTo(files);
    }

    [Test]
    public void BuildCreated_Failure_Emits_CommitCreatedFailed_LoudWithReason_NoShaTag()
    {
        var evt = CommitEvents.BuildCreated(
            success: false, storyId: "story-4-5", sessionId: Session,
            sha: null, message: "feat(story-4-5): task [TDD]",
            branch: "feature/story-4-5", repository: "acme/widgets",
            files: Array.Empty<string>(), error: "No files to commit");

        evt.EventType.Should().Be(CommitEvents.CreatedFailed);
        evt.Status.Should().Be("error");
        evt.Error.Should().Be("No files to commit");
        evt.Tags.Should().NotContainKey("sha", "a failed commit has no SHA to index");
        evt.Data.Should().ContainKey("reason").WhoseValue.Should().Be("No files to commit");
        evt.Data.Should().ContainKey("fileCount").WhoseValue.Should().Be(0);
        CommitEvents.IsFailureType(evt.EventType).Should().BeTrue();
    }

    // ===================================================================
    // Cutover proof (TAMMA001) — the coverage pass injects NO
    // credential-holding vendor/git integration service into any engine
    // activity it touches. Emissions go through the transient-list drain only.
    // ===================================================================

    [Test]
    public void TouchedActivities_InjectNoCredentialHoldingIntegrationService()
    {
        foreach (var type in new[]
        {
            typeof(WriteTestsActivity),
            typeof(WriteImplementationActivity),
            typeof(ApplyRefactoringActivity),
            typeof(CommitChangesActivity),
        })
        {
            foreach (var ctor in type.GetConstructors())
            {
                ctor.GetParameters()
                    .Any(p => typeof(IGitHubIntegrationService).IsAssignableFrom(p.ParameterType)
                              || typeof(IIntegrationService).IsAssignableFrom(p.ParameterType))
                    .Should().BeFalse($"{type.Name} must not inject a credential-holding integration service");
            }

            type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => typeof(IGitHubIntegrationService).IsAssignableFrom(f.FieldType)
                          || typeof(IIntegrationService).IsAssignableFrom(f.FieldType))
                .Should().BeFalse($"{type.Name} must hold no credential-holding integration-service field");
        }
    }
}
