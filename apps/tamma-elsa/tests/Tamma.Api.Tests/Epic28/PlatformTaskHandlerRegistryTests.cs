using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for
/// <see cref="PlatformTaskHandlerRegistry"/>: snapshot-on-construction,
/// case-sensitive lookup, duplicate-type rejection, missing-handler
/// nullity.
/// </summary>
[TestFixture]
public class PlatformTaskHandlerRegistryTests
{
    private sealed class StubHandler : IPlatformTaskHandler
    {
        public StubHandler(string type) { TaskType = type; }
        public string TaskType { get; }
        public Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Test]
    public void Resolve_KnownType_ReturnsMatchingHandler()
    {
        var a = new StubHandler("a.task");
        var b = new StubHandler("b.task");
        var registry = new PlatformTaskHandlerRegistry(new[] { a, b });

        registry.Resolve("a.task").Should().BeSameAs(a);
        registry.Resolve("b.task").Should().BeSameAs(b);
    }

    [Test]
    public void Resolve_UnknownType_ReturnsNull()
    {
        var registry = new PlatformTaskHandlerRegistry(new[] { new StubHandler("x") });
        registry.Resolve("y").Should().BeNull();
    }

    [Test]
    public void Resolve_IsCaseSensitive()
    {
        var registry = new PlatformTaskHandlerRegistry(new[] { new StubHandler("MyType") });
        registry.Resolve("mytype").Should().BeNull(
            "task type matching is case-sensitive — handlers and DB rows must agree");
    }

    [Test]
    public void Constructor_DuplicateTaskType_Throws()
    {
        var act = () => new PlatformTaskHandlerRegistry(new[]
        {
            new StubHandler("dupe"),
            new StubHandler("dupe"),
        });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*dupe*");
    }

    [Test]
    public void Constructor_EmptyTaskType_Throws()
    {
        var act = () => new PlatformTaskHandlerRegistry(
            new[] { new StubHandler("") });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty TaskType*");
    }

    [Test]
    public void RegisteredTypes_ReflectsRegistrationSet()
    {
        var registry = new PlatformTaskHandlerRegistry(new[]
        {
            new StubHandler("zebra"),
            new StubHandler("alpha"),
        });
        registry.RegisteredTypes.Should().BeEquivalentTo(new[] { "zebra", "alpha" });
    }
}
