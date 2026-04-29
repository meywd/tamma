using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-2 test helper — a hand-rolled
/// <see cref="ITenantInfrastructureProvider"/> the workflow tests script
/// per-call. Strict-mock with Moq would also work but the workflow drives
/// many distinct paths through the same provider in a single run (provision
/// → poll status N times → deprovision on failure), and Moq's verification
/// noise overwhelms the test signal. The fake records every call for
/// post-run assertions.
/// </summary>
public sealed class FakeTenantInfrastructureProvider : ITenantInfrastructureProvider
{
    private readonly Queue<ProvisioningStatusSnapshot> _statusScript = new();
    public ProviderCapabilities Capabilities { get; set; }
    public Func<Guid, ProvisioningRequest, CancellationToken, Task<ProvisioningResult>>? OnProvision { get; set; }
    public Func<Guid, DeprovisioningRequest, CancellationToken, Task>? OnDeprovision { get; set; }
    public Func<Guid, CancellationToken, Task<TenantEndpoints>>? OnResolveEndpoints { get; set; }

    public List<(Guid TenantId, ProvisioningRequest Request)> ProvisionCalls { get; } = new();
    public List<Guid> StatusCalls { get; } = new();
    public List<(Guid TenantId, DeprovisioningRequest Request)> DeprovisionCalls { get; } = new();

    public string ProviderKey { get; }

    public FakeTenantInfrastructureProvider(
        string providerKey,
        ProviderCapabilities? capabilities = null)
    {
        ProviderKey = providerKey;
        Capabilities = capabilities ?? new ProviderCapabilities(
            providerKey,
            $"Fake {providerKey}",
            ProvisioningTopology.DatabaseOnly | ProvisioningTopology.DedicatedCompute,
            new[] { "germany-1", "us-east-1" });
    }

    public ProviderCapabilities GetCapabilities() => Capabilities;

    /// <summary>Push a status snapshot onto the queue
    /// <see cref="GetStatusAsync"/> will pop on its next call. Use to
    /// drive the probe loop through multiple states (e.g. AppDeploying →
    /// Ready) without hand-rolling a scripted lambda.</summary>
    public FakeTenantInfrastructureProvider EnqueueStatus(ProvisioningStatusSnapshot snap)
    {
        _statusScript.Enqueue(snap);
        return this;
    }

    public FakeTenantInfrastructureProvider EnqueueReady() =>
        EnqueueStatus(new ProvisioningStatusSnapshot(
            ProvisioningState.Ready, "ready", null, DateTimeOffset.UtcNow));

    public FakeTenantInfrastructureProvider EnqueueDeploying(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            EnqueueStatus(new ProvisioningStatusSnapshot(
                ProvisioningState.AppDeploying, "deploying", null, DateTimeOffset.UtcNow));
        }
        return this;
    }

    public FakeTenantInfrastructureProvider EnqueueProviderFailure(string reason, string detail) =>
        EnqueueStatus(new ProvisioningStatusSnapshot(
            ProvisioningState.Failed, detail, reason, DateTimeOffset.UtcNow));

    public Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId, ProvisioningRequest request, CancellationToken ct)
    {
        ProvisionCalls.Add((tenantId, request));
        if (OnProvision is null)
        {
            return Task.FromResult(new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Pending, "queued", null, DateTimeOffset.UtcNow),
                ProviderResourceIds: new Dictionary<string, string>
                {
                    ["fake_resource_id"] = $"res-{tenantId:N}",
                },
                Endpoints: null,
                ProvisioningDurationSeconds: 0.1));
        }
        return OnProvision(tenantId, request, ct);
    }

    public Task<ProvisioningStatusSnapshot> GetStatusAsync(Guid tenantId, CancellationToken ct)
    {
        StatusCalls.Add(tenantId);
        if (_statusScript.Count > 0)
        {
            return Task.FromResult(_statusScript.Dequeue());
        }
        // Default: keep returning the last state (or "pending" when empty).
        return Task.FromResult(new ProvisioningStatusSnapshot(
            ProvisioningState.Pending, "still_pending", null, DateTimeOffset.UtcNow));
    }

    public Task DeprovisionAsync(
        Guid tenantId, DeprovisioningRequest request, CancellationToken ct)
    {
        DeprovisionCalls.Add((tenantId, request));
        if (OnDeprovision is null) return Task.CompletedTask;
        return OnDeprovision(tenantId, request, ct);
    }

    public Task<TenantEndpoints> ResolveEndpointsAsync(Guid tenantId, CancellationToken ct)
    {
        if (OnResolveEndpoints is not null) return OnResolveEndpoints(tenantId, ct);
        return Task.FromResult(new TenantEndpoints(
            DatabaseUrl: $"postgres://fake/{tenantId:N}",
            EngineHost: $"fake-{tenantId:N}.example",
            EngineUrl: $"https://fake-{tenantId:N}.example"));
    }
}

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for the workflow tests so probe
/// loops are sub-second instead of waiting on the real clock. Driven via
/// <see cref="Advance"/> inside test bodies. Matches the
/// <c>FakeTimeProvider</c> shape used elsewhere in the suite (Story 28-7,
/// 28-10) without taking the package dependency.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;
    public TestClock(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now = _now.Add(span);
}
