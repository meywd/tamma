using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Tests.Conventions;

[TestFixture]
public class ConventionTemplatesTests
{
    private static readonly string[] ExpectedKeys =
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
    };

    [Test]
    public void ListAll_ReturnsExactlyTenTemplates()
    {
        var service = new ConventionTemplateService();

        var templates = service.ListAll();

        templates.Should().HaveCount(10);
    }

    [Test]
    public void ListAll_ExposesAllExpectedKeys()
    {
        var service = new ConventionTemplateService();

        var templates = service.ListAll();

        templates.Select(t => t.Key).Should().BeEquivalentTo(ExpectedKeys);
    }

    [TestCaseSource(nameof(ExpectedKeys))]
    public void GetByKey_ReturnsTemplateWithNonEmptyConventions(string key)
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey(key);

        template.Should().NotBeNull();
        template!.Key.Should().Be(key);
        template.Name.Should().NotBeNullOrWhiteSpace();
        template.Description.Should().NotBeNullOrWhiteSpace();
        template.Conventions.Should().NotBeNullOrWhiteSpace(
            "every convention template must ship a real body — the LLM relies on it");
        template.Conventions.Length.Should().BeGreaterThan(200,
            "template bodies should contain substantive coding rules, not a placeholder");
    }

    [Test]
    public void GetByKey_UnknownKey_ReturnsNull()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("this-key-does-not-exist");

        template.Should().BeNull();
    }

    [Test]
    public void GetByKey_TypescriptReact_MentionsReact()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("typescript-react");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("React");
    }

    [Test]
    public void GetByKey_GoStdlib_MentionsGoroutines()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("go-stdlib");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("goroutines");
    }

    [Test]
    public void GetByKey_PythonFastApi_MentionsPydantic()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("python-fastapi");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Pydantic");
    }

    [Test]
    public void GetByKey_CsharpAspnet_MentionsDotNet()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("csharp-aspnet");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain(".NET");
    }

    [Test]
    public void GetByKey_RustActix_MentionsCargo()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("rust-actix");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("cargo");
    }

    [Test]
    public void GetByKey_JavaSpring_MentionsSpringBoot()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("java-spring");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Spring Boot");
    }

    [Test]
    public void GetByKey_RubyRails_MentionsActiveRecord()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("ruby-rails");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("ActiveRecord");
    }

    [Test]
    public void GetByKey_ElixirPhoenix_MentionsEcto()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("elixir-phoenix");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Ecto");
    }

    [Test]
    public void GetByKey_TypescriptNode_MentionsEsm()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("typescript-node");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("ESM");
    }

    [Test]
    public void GetByKey_PythonDjango_MentionsDjango()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("python-django");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Django");
    }
}
