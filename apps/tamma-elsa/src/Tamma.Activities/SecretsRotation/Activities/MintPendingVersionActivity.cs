using System.Security.Cryptography;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 AC2 step 1 — resolve the secret, optionally generate a
/// random plaintext, then persist a new version row in <c>Pending</c>
/// state via <see cref="ISecretRotationGateway.MintPendingVersionAsync"/>.
///
/// <para>Populates the workflow-shared
/// <see cref="RotationWorkflowState"/> with the snapshot, the minted
/// version number, and the previous-active version number so later
/// activities can build <see cref="RotationTarget"/> without re-
/// querying the store.</para>
/// </summary>
public class MintPendingVersionActivity : RotationActivityBase
{
    [Input(Description = "Secret id to rotate.")]
    public Input<Guid> SecretId { get; set; } = default!;

    [Input(Description = "Rotation correlation id — threaded through all saga events + handler calls.")]
    public Input<string> RotationCorrelationId { get; set; } = default!;

    [Input(
        Description =
            "Pre-supplied plaintext. When empty the activity generates "
            + "GenerateLength bytes of CSPRNG entropy and base64url-encodes the result.")]
    public Input<string> NewPlaintext { get; set; } = new(string.Empty);

    [Input(Description = "Generator length (bytes) when NewPlaintext is empty. Default 32.")]
    public Input<int> GenerateLength { get; set; } = new(32);

    [Input(Description = "Operator user id (Guid.Empty for scheduled/auto rotations).")]
    public Input<Guid> OperatorUserId { get; set; } = new(Guid.Empty);

    [Input(Description = "Grace window seconds. 0 means use the default (900).")]
    public Input<long> GraceWindowSeconds { get; set; } = new(0L);

    public override string StepName => "mint-pending";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var secretId = SecretId.Get(context);
        var correlationId = RotationCorrelationId.Get(context);
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException(
                "RotationCorrelationId is required.", nameof(RotationCorrelationId));

        var gateway = ResolveGateway(context);
        var snapshot = await gateway.GetSnapshotAsync(secretId, context.CancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            throw new InvalidOperationException(
                $"Secret '{secretId}' not found — cannot mint pending version.");

        var state = GetState(context);
        state.SecretId = secretId;
        state.RotationCorrelationId = correlationId;
        state.OperatorUserId = OperatorUserId.Get(context);
        state.GraceWindowSeconds = GraceWindowSeconds.Get(context);
        state.Snapshot = snapshot;
        state.PreviousVersionNumber = snapshot.ActiveVersionNumber;
        state.HandlerSystem = snapshot.ConsumerSystem;

        var newPlaintext = NewPlaintext.Get(context);
        if (string.IsNullOrWhiteSpace(newPlaintext))
        {
            var length = Math.Max(16, Math.Min(256, GenerateLength.Get(context)));
            newPlaintext = GenerateRandom(length);
        }
        state.NewPlaintext = newPlaintext;

        // Emit Started on the first meaningful step.
        await EmitAsync(
            context,
            RotationAuditEvents.Started,
            data: new Dictionary<string, object?>
            {
                ["handlerSystem"] = snapshot.ConsumerSystem,
                ["previousVersion"] = snapshot.ActiveVersionNumber,
            }).ConfigureAwait(false);

        var newVersionNumber = await gateway.MintPendingVersionAsync(
                secretId,
                newPlaintext,
                correlationId,
                state.OperatorUserId,
                context.CancellationToken)
            .ConfigureAwait(false);

        state.NewVersionNumber = newVersionNumber;

        await EmitAsync(
            context,
            RotationAuditEvents.Staged,
            versionNumber: newVersionNumber,
            data: new Dictionary<string, object?>
            {
                ["previousVersion"] = state.PreviousVersionNumber,
            }).ConfigureAwait(false);
    }

    private static string GenerateRandom(int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(buffer);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
