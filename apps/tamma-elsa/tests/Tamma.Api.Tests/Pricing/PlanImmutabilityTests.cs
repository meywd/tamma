using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (AC6, AC13) — the immutability guard. A plan version whose
/// <c>Status</c> is <c>active</c> or <c>deprecated</c> is immutable; only a
/// <c>draft</c> row is editable in place. The guard throws
/// <c>PLAN.VERSION.IMMUTABLE</c>. Pure unit test — no DB required.
/// </summary>
[TestFixture]
public class PlanImmutabilityTests
{
    private static PlanVersionEditor BuildEditor()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ControlPlaneDbContext(options);
        return new PlanVersionEditor(
            db,
            new RecordingPlatformEventPublisher(),
            TimeProvider.System,
            NullLogger<PlanVersionEditor>.Instance);
    }

    [TestCase("active")]
    [TestCase("deprecated")]
    public void EnsureMutableOrThrow_Rejects_Immutable_Status(string status)
    {
        var editor = BuildEditor();
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "team", Version = 1, Status = status };

        var act = () => editor.EnsureMutableOrThrow(plan);

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("PLAN.VERSION.IMMUTABLE");
    }

    [Test]
    public void EnsureMutableOrThrow_Allows_Draft()
    {
        var editor = BuildEditor();
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "team", Version = 2, Status = "draft" };

        var act = () => editor.EnsureMutableOrThrow(plan);

        act.Should().NotThrow();
    }

    [Test]
    public void EnsureMutableOrThrow_Immutable_Error_Is_High_Severity()
    {
        var editor = BuildEditor();
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "team", Version = 1, Status = "active" };

        var ex = Assert.Throws<TammaError>(() => editor.EnsureMutableOrThrow(plan));
        ex!.Severity.Should().Be(TammaErrorSeverity.High);
        ex.Context.Should().ContainKey("slug");
    }
}
