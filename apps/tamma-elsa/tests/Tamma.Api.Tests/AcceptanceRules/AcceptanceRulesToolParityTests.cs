using System.Text.Json;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.AcceptanceRules;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Tests.AcceptanceRules;

/// <summary>
/// AC3 parity pin: the <c>get_acceptance_rules</c> tool output and the payload
/// embedded in an <see cref="AcceptanceRequest"/> both come from
/// <see cref="IAcceptanceRulesResolver"/> and serialize identically through the
/// one canonical <see cref="AcceptanceRulesJson.Options"/>. Also: the tool binds
/// its principal at construction (a principal smuggled into <c>argumentsJson</c>
/// is ignored), and never throws.
/// </summary>
[TestFixture]
public class AcceptanceRulesToolParityTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static ResolvedAcceptanceRules Resolved() => new(
        Rules: AcceptanceDefaults.For(DocumentTypeKey.Plan) with { AutonomyLevel = 91 },
        Source: AcceptanceRulesSource.TypeOverride,
        Version: 5,
        DocumentTypeKey: DocumentTypeKey.Plan.ToWire(),
        ResolvedAt: DateTimeOffset.UnixEpoch);

    private static GetAcceptanceRulesTool ToolBoundTo(Guid userId, ResolvedAcceptanceRules resolved,
        out Mock<IAcceptanceRulesResolver> resolver)
    {
        resolver = new Mock<IAcceptanceRulesResolver>();
        resolver.Setup(r => r.ResolveAsync(userId, DocumentTypeKey.Plan, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        var factory = new GetAcceptanceRulesToolFactory(resolver.Object);
        return factory.Create(userId: userId);
    }

    [Test]
    public async Task Tool_output_equals_request_embedded_payload_byte_for_byte()
    {
        var resolved = Resolved();
        var tool = ToolBoundTo(UserId, resolved, out _);

        var result = await tool.ExecuteAsync("call-1", "{\"documentTypeKey\":\"plan\"}");
        result.Success.Should().BeTrue();

        // The same resolved rules embedded in a request.
        var document = MakeEnvelope(DocumentTypeKey.Plan);
        var review = MakeEnvelope(DocumentTypeKey.Review);
        var request = AcceptanceRequestFactory.Create(document, review, new[] { document }, 0, resolved);

        var embedded = JsonSerializer.Serialize(request.Rules, AcceptanceRulesJson.Options);
        result.Output.Should().Be(embedded);
    }

    [Test]
    public async Task Tool_ignores_a_principal_smuggled_into_arguments()
    {
        var resolved = Resolved();
        var tool = ToolBoundTo(UserId, resolved, out var resolver);

        var smuggled = "{\"userId\":\"99999999-9999-9999-9999-999999999999\"," +
                       "\"tenantId\":\"88888888-8888-8888-8888-888888888888\"," +
                       "\"documentTypeKey\":\"plan\"}";
        var result = await tool.ExecuteAsync("call-2", smuggled);

        result.Success.Should().BeTrue();
        // Resolver was called ONLY with the construction-bound principal.
        resolver.Verify(r => r.ResolveAsync(UserId, DocumentTypeKey.Plan, It.IsAny<CancellationToken>()), Times.Once);
        resolver.Verify(r => r.ResolveForTenantAsync(It.IsAny<Guid>(), It.IsAny<DocumentTypeKey>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Tool_returns_failure_not_throws_on_unknown_type()
    {
        var tool = ToolBoundTo(UserId, Resolved(), out _);
        var result = await tool.ExecuteAsync("call-3", "{\"documentTypeKey\":\"not-a-type\"}");
        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task Tool_returns_failure_not_throws_on_malformed_arguments()
    {
        var tool = ToolBoundTo(UserId, Resolved(), out _);
        var result = await tool.ExecuteAsync("call-4", "{ this is not json ");
        result.Success.Should().BeFalse();
    }

    [Test]
    public void Factory_requires_exactly_one_principal()
    {
        var resolver = new Mock<IAcceptanceRulesResolver>().Object;
        var factory = new GetAcceptanceRulesToolFactory(resolver);
        factory.Invoking(f => f.Create()).Should().Throw<ArgumentException>();
        factory.Invoking(f => f.Create(userId: UserId, tenantId: Guid.NewGuid()))
            .Should().Throw<ArgumentException>();
    }

    private static DocumentEnvelope MakeEnvelope(DocumentTypeKey type)
    {
        using var payload = JsonDocument.Parse("{}");
        return DocumentEnvelope.CreateDraft(
            type, 1, "ISSUE-1", "corr-1",
            DocumentProducer.Create("architect", "plan-review", "plan-review"),
            payload.RootElement);
    }
}
