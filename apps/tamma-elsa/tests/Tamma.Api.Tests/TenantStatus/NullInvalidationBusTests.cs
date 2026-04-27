using FluentAssertions;
using NUnit.Framework;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.TenantStatus;

/// <summary>
/// Round-2 follow-up — proves the
/// <see cref="NullTenantStatusInvalidationBus"/> is genuinely a no-op
/// and never throws / never blocks. Test fixtures rely on this seam
/// being safe to call unconditionally.
/// </summary>
[TestFixture]
public class NullInvalidationBusTests
{
    [Test]
    public async Task PublishAsync_Returns_Synchronously_With_Completed_Task()
    {
        var bus = new NullTenantStatusInvalidationBus();
        var task = bus.PublishAsync(Guid.NewGuid());
        task.IsCompleted.Should().BeTrue(
            "Null bus must complete synchronously without yielding to the scheduler");
        await task;
    }

    [Test]
    public async Task PublishAsync_Honours_Cancellation_Token_Without_Throwing()
    {
        var bus = new NullTenantStatusInvalidationBus();
        // Even when the caller's token is already cancelled, the
        // no-op bus should not throw — it has no work to abort, and
        // throwing would force admin endpoints to write defensive
        // try/catches around an inherently safe seam.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await bus.PublishAsync(Guid.NewGuid(), cts.Token);
    }

    [Test]
    public async Task PublishAsync_Is_Idempotent_For_Same_TenantId()
    {
        var bus = new NullTenantStatusInvalidationBus();
        var id = Guid.NewGuid();
        for (int i = 0; i < 100; i++)
        {
            await bus.PublishAsync(id);
        }
    }
}
