using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 AC2 step-10 + AC5 — wiring assertions for
/// <see cref="QueueWelcomeEmailActivity"/>. Like the other lifecycle
/// activities it runs inside the Elsa runtime and is not directly callable
/// in a unit test (constructing a real <c>ActivityExecutionContext</c>
/// requires the workflow engine), so these tests lock the parts that
/// don't need a live runtime:
///
/// <list type="bullet">
///   <item><description>Inherits the shared
///     <see cref="TenantLifecycleActivity"/> base (STEP_* emission +
///     replay-safe contract).</description></item>
///   <item><description>Declares the kebab-case <c>StepName</c> used as the
///     <c>tags-&gt;&gt;'step'</c> value.</description></item>
/// </list>
///
/// <para>The exactly-once-per-tenant insert behaviour is covered directly
/// by <c>PlatformEmailOutboxRepositoryTests.EnqueueWelcomeOnceAsync_*</c>
/// against a real <c>ControlPlaneDbContext</c>.</para>
/// </summary>
[TestFixture]
public class QueueWelcomeEmailActivityTests
{
    [Test]
    public void QueueWelcomeEmailActivity_HasCorrectStepName()
    {
        var activity = new QueueWelcomeEmailActivity();
        activity.StepName.Should().Be("queue-welcome-email");
    }

    [Test]
    public void QueueWelcomeEmailActivity_InheritsTenantLifecycleActivity()
    {
        typeof(QueueWelcomeEmailActivity)
            .Should()
            .BeDerivedFrom<TenantLifecycleActivity>();
    }
}
