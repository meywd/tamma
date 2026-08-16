using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Models;
using Tamma.Activities.SecretsRotation.Activities;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 29-6 — top-level Elsa workflow that drives the rotation saga.
/// Accepts input bag <c>{ secretId, rotationCorrelationId, newPlaintext?,
/// generateLength?, operatorUserId?, graceWindowSeconds? }</c>; the body
/// is a single <see cref="RotateSecretSagaActivity"/> that emits all the
/// per-step events and runs the compensation path on probe/push
/// failure.
///
/// <para>Keeping the workflow shape declarative + thin (one code
/// activity) means:</para>
/// <list type="bullet">
///   <item><description>Operators trigger the same Elsa workflow for
///     postgres-role, cranl-env, hmac, and generic-http rotations —
///     the handler port is the only thing that varies.</description></item>
///   <item><description>The composite activity is unit-testable in
///     isolation without Elsa (see
///     <c>RotateSecretSagaActivityTests</c>).</description></item>
///   <item><description>Studio shows one node per rotation so the
///     timeline stays readable — per-step events flow through
///     <see cref="Tamma.Activities.SecretsRotation.Contracts.IRotationAuditEmitter"/>
///     not per-activity nodes.</description></item>
/// </list>
/// </summary>
public class RotateSecretWorkflow : WorkflowBase
{
    public const string DefinitionId = "rotate-secret";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Rotate Secret";
        builder.DefinitionId = DefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Generic rotation saga: mint → push → probe → activate → retire. "
            + "Handler plugins (postgres, cranl, hmac, generic-http) own the "
            + "system-specific push/probe/rollback.";

        var secretId = builder.WithVariable<Guid>("SecretId", Guid.Empty).Persisted();
        var correlationId = builder.WithVariable<string>("RotationCorrelationId", string.Empty).Persisted();
        var newPlaintext = builder.WithVariable<string>("NewPlaintext", string.Empty).Persisted();
        var generateLength = builder.WithVariable<int>("GenerateLength", 32).Persisted();
        var operatorUserId = builder.WithVariable<Guid>("OperatorUserId", Guid.Empty).Persisted();
        var graceSeconds = builder.WithVariable<long>("GraceWindowSeconds", 0L).Persisted();
        var resultVar = builder.WithVariable<string>("Result", string.Empty).Persisted();
        var newVersionVar = builder.WithVariable<int>("NewVersionNumber", 0).Persisted();
        var oldVersionVar = builder.WithVariable<int>("OldVersionNumber", 0).Persisted();
        var errorVar = builder.WithVariable<string>("Error", string.Empty).Persisted();

        var initInputs = new SetVariable
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Variable = secretId,
            Value = new Input<object?>(ctx =>
            {
                var raw = ctx.GetInput<object?>("secretId");
                var id = raw switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var p) => p,
                    _ => Guid.Empty,
                };
                if (id == Guid.Empty)
                    throw new InvalidOperationException(
                        "RotateSecretWorkflow input 'secretId' is required and must be a non-empty Guid.");

                var corr = ctx.GetInput<string?>("rotationCorrelationId");
                if (string.IsNullOrWhiteSpace(corr))
                    throw new InvalidOperationException(
                        "RotateSecretWorkflow input 'rotationCorrelationId' is required.");
                correlationId.Set(ctx, corr);

                var plaintext = ctx.GetInput<string?>("newPlaintext") ?? string.Empty;
                newPlaintext.Set(ctx, plaintext);

                var genLen = ctx.GetInput<int?>("generateLength") ?? 32;
                generateLength.Set(ctx, genLen);

                var operatorRaw = ctx.GetInput<object?>("operatorUserId");
                var opId = operatorRaw switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var p) => p,
                    _ => Guid.Empty,
                };
                operatorUserId.Set(ctx, opId);

                var graceRaw = ctx.GetInput<object?>("graceWindowSeconds");
                long grace = graceRaw switch
                {
                    long l => l,
                    int i => i,
                    string s when long.TryParse(s, out var p) => p,
                    _ => 0L,
                };
                graceSeconds.Set(ctx, grace);

                return id;
            }),
        };

        var saga = new RotateSecretSagaActivity
        {
            Id = "RotateSecretSaga",
            Name = "Rotate Secret Saga",
            SecretId = new Input<Guid>(ctx => secretId.Get(ctx)),
            RotationCorrelationId = new Input<string>(ctx => correlationId.Get(ctx)),
            NewPlaintext = new Input<string>(ctx => newPlaintext.Get(ctx)),
            GenerateLength = new Input<int>(ctx => generateLength.Get(ctx)),
            OperatorUserId = new Input<Guid>(ctx => operatorUserId.Get(ctx)),
            GraceWindowSeconds = new Input<long>(ctx => graceSeconds.Get(ctx)),
            Result = new Output<string>(resultVar),
            NewVersionNumber = new Output<int>(newVersionVar),
            OldVersionNumber = new Output<int>(oldVersionVar),
            Error = new Output<string>(errorVar),
        };

        builder.Root = new Sequence
        {
            Activities = { initInputs, saga },
        };
    }
}
