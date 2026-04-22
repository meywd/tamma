using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Rotation;

namespace Tamma.Api.Tests.Secrets.Rotation;

/// <summary>
/// Story 29-6 — unit tests for the <see cref="SecretStoreRotationGateway"/>'s
/// pure helper <c>ParseFirstConsumerRef</c>. The mutating pieces
/// (mint / activate / delete / retire) are tested via the
/// <c>SagaRunnerTests</c> integration stubs so we avoid standing up an
/// EF in-memory DB for every one-line helper.
/// </summary>
[TestFixture]
public class SecretStoreRotationGatewayTests
{
    [Test]
    public void ParseFirstConsumerRef_EmptyArray_FallsBackToGenericHttp()
    {
        var (system, ident) = SecretStoreRotationGateway.ParseFirstConsumerRef("[]");
        system.Should().Be("generic-http");
        ident.Should().BeEmpty();
    }

    [Test]
    public void ParseFirstConsumerRef_Malformed_FallsBackToGenericHttp()
    {
        var (system, ident) = SecretStoreRotationGateway.ParseFirstConsumerRef("not json");
        system.Should().Be("generic-http");
        ident.Should().BeEmpty();
    }

    [Test]
    public void ParseFirstConsumerRef_PascalCaseKeys_Parsed()
    {
        var json = "[{\"System\":\"postgres\",\"Identifier\":\"role=tamma_app\"}]";
        var (system, ident) = SecretStoreRotationGateway.ParseFirstConsumerRef(json);
        system.Should().Be("postgres");
        ident.Should().Be("role=tamma_app");
    }

    [Test]
    public void ParseFirstConsumerRef_CamelCaseKeys_Parsed()
    {
        var json = "[{\"system\":\"cranl\",\"identifier\":\"app=app_1\"}]";
        var (system, ident) = SecretStoreRotationGateway.ParseFirstConsumerRef(json);
        system.Should().Be("cranl");
        ident.Should().Be("app=app_1");
    }

    [Test]
    public void ParseFirstConsumerRef_MultipleRefs_UsesFirst()
    {
        var json = "[{\"System\":\"postgres\",\"Identifier\":\"role=a\"},{\"System\":\"cranl\",\"Identifier\":\"app=b\"}]";
        var (system, _) = SecretStoreRotationGateway.ParseFirstConsumerRef(json);
        system.Should().Be("postgres");
    }

    [Test]
    public void ParseFirstConsumerRef_EmptySystem_FallsBackToGenericHttp()
    {
        var json = "[{\"System\":\"\",\"Identifier\":\"x\"}]";
        var (system, _) = SecretStoreRotationGateway.ParseFirstConsumerRef(json);
        system.Should().Be("generic-http");
    }
}
