using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — wiring assertions for
/// <see cref="CreateTenantSchemaActivity"/>. The activity is a thin
/// wrapper over <c>ITenantProvisioningService.CreateSchemaAsync</c> and
/// only runs inside the Elsa runtime, so these tests lock the
/// runtime-free surface (base class + step name). The schema DDL itself
/// (CREATE SCHEMA AUTHORIZATION + GRANT CONNECT + per-database
/// search_path, idempotency, role isolation) is covered against a real
/// Postgres by <c>Tamma.Api.Tests/Tenancy/TenantProvisioningServiceTests</c>.
/// </summary>
[TestFixture]
public class CreateTenantSchemaActivityTests
{
    [Test]
    public void CreateTenantSchemaActivity_HasCorrectStepName()
    {
        new CreateTenantSchemaActivity().StepName.Should().Be("create-schema");
    }

    [Test]
    public void CreateTenantSchemaActivity_InheritsTenantLifecycleActivity()
    {
        typeof(CreateTenantSchemaActivity)
            .Should()
            .BeDerivedFrom<TenantLifecycleActivity>();
    }
}
