using Microsoft.Extensions.DependencyInjection;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Wave C.4 §4 — adapter that resolves a fresh scoped
/// <see cref="IAlertEventEmitter"/> per emission call. The health
/// monitor is a singleton (shared rolling window); the underlying
/// emitter is scoped (depends on scoped IEventRepository /
/// IPlatformEventPublisher). This adapter bridges the lifetime gap
/// without resorting to <c>IServiceProviderIsService</c>.
///
/// <para>When <see cref="IAlertEventEmitter"/> is absent from DI (dev
/// harness without AddTammaAlerts), every call is a no-op so the
/// monitor degrades quietly.</para>
/// </summary>
public sealed class ScopedAlertEventEmitter : IAlertEventEmitter
{
    private readonly IServiceProvider _rootServices;

    public ScopedAlertEventEmitter(IServiceProvider rootServices)
    {
        _rootServices = rootServices ?? throw new ArgumentNullException(nameof(rootServices));
    }

    public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct) =>
        WithScopeAsync(e => e.EmitBudgetExhaustedAsync(evt, ct));

    public Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct) =>
        WithScopeAsync(e => e.EmitAgentDispatchFailedAsync(evt, ct));

    public Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct) =>
        WithScopeAsync(e => e.EmitWorkflowRetryExceededAsync(evt, ct));

    public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct) =>
        WithScopeAsync(e => e.EmitPlatformApiUnhealthyAsync(evt, ct));

    public Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct) =>
        WithScopeAsync(e => e.EmitSecretRotationFailedAsync(evt, ct));

    private async Task WithScopeAsync(Func<IAlertEventEmitter, Task> action)
    {
        using var scope = _rootServices.CreateScope();
        var emitter = scope.ServiceProvider.GetService<IAlertEventEmitter>();
        if (emitter is null) return;
        await action(emitter).ConfigureAwait(false);
    }
}
