using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.Conventions;

[TestFixture]
public class ConventionEndpointsIntegrationTests
{
    private ApiTestFixture _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new ApiTestFixture();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task ListAll_Returns200WithTenEntries()
    {
        var response = await _client.GetAsync("/api/convention-templates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConventionTemplateSummaryDto>>();
        items.Should().NotBeNull();
        items!.Should().HaveCount(10);
        items.Select(i => i.Key).Should().Contain(new[]
        {
            "typescript-react",
            "typescript-node",
            "python-fastapi",
            "python-django",
            "csharp-aspnet",
            "rust-actix",
            "go-stdlib",
            "java-spring",
            "ruby-rails",
            "elixir-phoenix"
        });
        items.Should().AllSatisfy(i =>
        {
            i.Name.Should().NotBeNullOrWhiteSpace();
            i.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Test]
    public async Task ListAll_DoesNotIncludeConventionsBody()
    {
        var response = await _client.GetAsync("/api/convention-templates");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            element.TryGetProperty("conventions", out _).Should().BeFalse(
                "the list endpoint must expose metadata only, not the full conventions body");
        }
    }

    [Test]
    public async Task GetByKey_KnownKey_Returns200WithConventionsBody()
    {
        var response = await _client.GetAsync("/api/convention-templates/typescript-react");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var template = await response.Content.ReadFromJsonAsync<ConventionTemplateDetailDto>();
        template.Should().NotBeNull();
        template!.Key.Should().Be("typescript-react");
        template.Name.Should().NotBeNullOrWhiteSpace();
        template.Description.Should().NotBeNullOrWhiteSpace();
        template.Conventions.Should().NotBeNullOrWhiteSpace();
        template.Conventions.Should().Contain("React");
    }

    [Test]
    public async Task GetByKey_CsharpAspnet_ReturnsFullBody()
    {
        var response = await _client.GetAsync("/api/convention-templates/csharp-aspnet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var template = await response.Content.ReadFromJsonAsync<ConventionTemplateDetailDto>();
        template.Should().NotBeNull();
        template!.Conventions.Should().Contain(".NET");
    }

    [Test]
    public async Task GetByKey_UnknownKey_Returns404()
    {
        var response = await _client.GetAsync("/api/convention-templates/this-key-does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // DTOs used only by these tests — mirror the JSON contract of the endpoints.
    private sealed record ConventionTemplateSummaryDto(string Key, string Name, string Description);

    private sealed record ConventionTemplateDetailDto(
        string Key,
        string Name,
        string Description,
        string Conventions);
}
