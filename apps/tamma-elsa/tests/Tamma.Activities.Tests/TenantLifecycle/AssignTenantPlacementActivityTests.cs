using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data.Pooling;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — wiring assertions for
/// <see cref="AssignTenantPlacementActivity"/>. Like the other lifecycle
/// activities, <c>ProcessAsync</c> only runs inside the Elsa runtime
/// (constructing a real <c>ActivityExecutionContext</c> requires the
/// workflow engine), so these tests lock the runtime-free parts: the
/// base class, the kebab-case step name, and the
/// <c>ReconstructPlacement</c> helper the three downstream activities
/// use to turn the string workflow variables back into a
/// <c>TenantPlacement</c>.
///
/// <para>The placement decision itself (tier matching, capacity,
/// idempotency) is covered against a real Postgres by
/// <c>Tamma.Api.Tests/Tenancy/TenantPlacementServiceTests</c>.</para>
/// </summary>
[TestFixture]
public class AssignTenantPlacementActivityTests
{
    [Test]
    public void AssignTenantPlacementActivity_HasCorrectStepName()
    {
        new AssignTenantPlacementActivity().StepName.Should().Be("assign-placement");
    }

    [Test]
    public void AssignTenantPlacementActivity_InheritsTenantLifecycleActivity()
    {
        typeof(AssignTenantPlacementActivity)
            .Should()
            .BeDerivedFrom<TenantLifecycleActivity>();
    }

    [Test]
    public void ReconstructPlacement_RoundTripsTheActivityOutputFormat()
    {
        // The activity writes DatabaseId as Guid "D" + the schema name —
        // downstream activities must get the identical placement back.
        var databaseId = Guid.NewGuid();
        var schemaName = TenantNaming.SchemaName(Guid.NewGuid());

        var placement = AssignTenantPlacementActivity.ReconstructPlacement(
            databaseId.ToString("D"), schemaName, "TestStep");

        placement.DatabaseId.Should().Be(databaseId);
        placement.SchemaName.Should().Be(schemaName);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-guid")]
    public void ReconstructPlacement_InvalidDatabaseId_ThrowsNamingConsumerStep(string? databaseId)
    {
        var act = () => AssignTenantPlacementActivity.ReconstructPlacement(
            databaseId, "t_abc", "CreateTenantSchema");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("CreateTenantSchema:*DatabaseId*");
    }

    [Test]
    public void ReconstructPlacement_EmptyGuidDatabaseId_Throws()
    {
        var act = () => AssignTenantPlacementActivity.ReconstructPlacement(
            Guid.Empty.ToString("D"), "t_abc", "CreateTenantRole");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("CreateTenantRole:*DatabaseId*");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ReconstructPlacement_MissingSchemaName_ThrowsNamingConsumerStep(string? schemaName)
    {
        var act = () => AssignTenantPlacementActivity.ReconstructPlacement(
            Guid.NewGuid().ToString("D"), schemaName, "BuildConnectionString");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("BuildConnectionString:*SchemaName*");
    }
}
