namespace Tamma.Api.Endpoints;

public static class ConventionEndpoints
{
    private static readonly List<object> Templates =
    [
        new { key = "typescript-react", name = "TypeScript + React", description = "Modern React with TypeScript conventions" },
        new { key = "typescript-node", name = "TypeScript + Node.js", description = "Node.js backend with TypeScript" },
        new { key = "python-fastapi", name = "Python + FastAPI", description = "FastAPI web framework conventions" },
        new { key = "python-django", name = "Python + Django", description = "Django web framework conventions" },
        new { key = "csharp-aspnet", name = "C# + ASP.NET Core", description = ".NET web API conventions" },
        new { key = "rust-actix", name = "Rust + Actix", description = "Actix web framework conventions" },
        new { key = "go-stdlib", name = "Go + Standard Library", description = "Go standard library conventions" },
        new { key = "java-spring", name = "Java + Spring Boot", description = "Spring Boot conventions" },
        new { key = "ruby-rails", name = "Ruby on Rails", description = "Rails conventions" },
        new { key = "elixir-phoenix", name = "Elixir + Phoenix", description = "Phoenix framework conventions" },
    ];

    public static IResult ListAll()
        => Results.Ok(Templates);

    public static IResult GetByKey(string key)
    {
        var template = Templates.FirstOrDefault(t =>
            ((dynamic)t).key == key);
        return template is not null
            ? Results.Ok(template)
            : Results.NotFound(new { error = "Template not found" });
    }
}
