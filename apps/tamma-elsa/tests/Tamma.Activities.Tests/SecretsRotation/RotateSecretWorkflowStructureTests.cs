using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Activities;
using Tamma.Activities.Tests.Workflows;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.SecretsRotation;

/// <summary>
/// Story 29-6 — structural assertions on
/// <see cref="RotateSecretWorkflow"/>. Mirrors the pattern of
/// <c>CreateTenantWorkflowStructureTests</c> so the rotation workflow
/// definition has the same baseline guarantees.
/// </summary>
[TestFixture]
public class RotateSecretWorkflowStructureTests
{
    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new RotateSecretWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be(RotateSecretWorkflow.DefinitionId);
        builder.Object.Name.Should().Be("Rotate Secret");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithInitAndSaga()
    {
        var workflow = new RotateSecretWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.Root.Should().BeOfType<Sequence>();
        var seq = (Sequence)builder.Object.Root;
        var acts = seq.Activities.ToList();
        acts.Should().HaveCount(2, "InitInputs + RotateSecretSaga");
        acts[0].Should().BeOfType<SetVariable>();
        acts[1].Should().BeOfType<RotateSecretSagaActivity>();
    }

    [Test]
    public void Build_DeclaresExpectedVariables()
    {
        var workflow = new RotateSecretWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var names = builder.Object.Variables.Select(v => v.Name).ToHashSet();
        names.Should().Contain(new[]
        {
            "SecretId", "RotationCorrelationId", "NewPlaintext",
            "GenerateLength", "OperatorUserId", "GraceWindowSeconds",
            "Result", "NewVersionNumber", "OldVersionNumber", "Error",
        });
    }

    [Test]
    public void SagaActivity_Metadata_IsConfiguredCorrectly()
    {
        var workflow = new RotateSecretWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var seq = (Sequence)builder.Object.Root;
        var saga = (RotateSecretSagaActivity)seq.Activities.ToList()[1];
        saga.Id.Should().Be("RotateSecretSaga");
        saga.Name.Should().Be("Rotate Secret Saga");
        saga.Result.Should().NotBeNull();
        saga.NewVersionNumber.Should().NotBeNull();
        saga.OldVersionNumber.Should().NotBeNull();
        saga.Error.Should().NotBeNull();
    }
}
