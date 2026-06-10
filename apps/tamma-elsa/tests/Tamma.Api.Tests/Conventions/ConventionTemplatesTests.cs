using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Tests.Conventions;

[TestFixture]
public class ConventionTemplatesTests
{
    // The full catalogue shipped by Tamma — 20 language/framework + 11 action
    // + 8 role + 7 cross-cutting = 46 templates. This array is the catalogue
    // lock: it must stay in sync with ConventionTemplates.All. Adding or
    // removing a template without updating this list fails
    // ListAll_ExposesAllExpectedKeys, which is intentional.
    private static readonly string[] ExpectedKeys =
    {
        // Language / framework (20)
        "typescript-react",
        "typescript-node",
        "typescript-react-native",
        "python",
        "python-fastapi",
        "python-django",
        "csharp",
        "rust",
        "go",
        "java",
        "kotlin",
        "swift",
        "swift-uikit",
        "dart-flutter",
        "c",
        "cpp",
        "ruby-rails",
        "php-laravel",
        "elixir-phoenix",
        "scala",
        // Action-triggered (11)
        "action-write-code",
        "action-review-code",
        "action-design",
        "action-write-tests",
        "action-debug",
        "action-refactor",
        "action-document",
        "action-plan",
        "action-context-scan",
        "action-triage",
        "action-deploy",
        // Role-triggered (8)
        "role-security-reviewer",
        "role-architect",
        "role-qa-engineer",
        "role-devops-engineer",
        "role-tech-lead",
        "role-developer",
        "role-product-owner",
        "role-tech-writer",
        // Cross-cutting (7)
        "universal-safety",
        "universal-quality",
        "git-conventions",
        "error-handling",
        "api-design",
        "database-conventions",
        "observability"
    };

    [Test]
    public void ListAll_ReturnsExactlyTheExpectedCatalogue()
    {
        var service = new ConventionTemplateService();

        var templates = service.ListAll();

        templates.Should().HaveCount(ExpectedKeys.Length);
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

    // ────────────────────────────────────────────────────────────────────
    // Per-template substring spot-checks — proves each body is the TS
    // equivalent and not a placeholder or a copy from another template.
    // Each assertion picks a term that appears in the TS body for that key
    // and is distinctive enough to rule out accidental cross-contamination.
    // ────────────────────────────────────────────────────────────────────

    [Test]
    public void GetByKey_TypescriptReact_MentionsReact()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("typescript-react");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("React");
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
    public void GetByKey_TypescriptReactNative_MentionsExpo()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("typescript-react-native");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Expo");
    }

    [Test]
    public void GetByKey_Python_MentionsAsyncio()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("python");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("asyncio");
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
    public void GetByKey_PythonDjango_MentionsDjango()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("python-django");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Django");
    }

    [Test]
    public void GetByKey_Csharp_MentionsDotNet()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("csharp");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain(".NET");
    }

    [Test]
    public void GetByKey_Rust_MentionsCargo()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("rust");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("cargo");
    }

    [Test]
    public void GetByKey_Go_MentionsGoroutines()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("go");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("goroutines");
    }

    [Test]
    public void GetByKey_Java_MentionsSpringBoot()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("java");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Spring Boot");
    }

    [Test]
    public void GetByKey_Kotlin_MentionsDataClass()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("kotlin");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("data class");
    }

    [Test]
    public void GetByKey_Scala_MentionsCaseClass()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("scala");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("case class");
    }

    [Test]
    public void GetByKey_Swift_MentionsGuardLet()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("swift");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("guard let");
    }

    [Test]
    public void GetByKey_SwiftUikit_MentionsUikit()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("swift-uikit");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("UIKit");
    }

    [Test]
    public void GetByKey_DartFlutter_MentionsWidget()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("dart-flutter");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Widget");
    }

    [Test]
    public void GetByKey_C_MentionsMalloc()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("c");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("malloc");
    }

    [Test]
    public void GetByKey_Cpp_MentionsRaii()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("cpp");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("RAII");
    }

    [Test]
    public void GetByKey_Cpp_IncludesReadabilityClause()
    {
        // Audit prompts/002: the TS source's auto-vs-explicit-types bullet
        // ends with "for readability in function signatures". An earlier
        // port dropped those two words; this test prevents regression.
        var service = new ConventionTemplateService();

        var template = service.GetByKey("cpp");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("explicit types for readability in function signatures");
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
    public void GetByKey_PhpLaravel_MentionsEloquent()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("php-laravel");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Eloquent");
    }

    [Test]
    public void GetByKey_ElixirPhoenix_MentionsEcto()
    {
        var service = new ConventionTemplateService();

        var template = service.GetByKey("elixir-phoenix");

        template.Should().NotBeNull();
        template!.Conventions.Should().Contain("Ecto");
    }
}
