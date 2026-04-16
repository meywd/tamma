using System.Text.Json;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

public static class GitHubEndpoints
{
    public static Task<IResult> Callback(HttpContext context)
    {
        // TODO: Process GitHub App installation callback
        return Task.FromResult(Results.Ok(new { message = "GitHub callback processed (stub)" }));
    }

    public static async Task<IResult> Webhooks(HttpContext context, IInstallationRepository installRepo)
    {
        // TODO: Verify X-Hub-Signature-256
        var eventType = context.Request.Headers["X-GitHub-Event"].FirstOrDefault();
        if (string.IsNullOrEmpty(eventType))
            return Results.BadRequest(new { error = "Missing X-GitHub-Event header" });

        string body;
        using (var reader = new StreamReader(context.Request.Body))
            body = await reader.ReadToEndAsync();

        switch (eventType)
        {
            case "installation":
            case "installation_repositories":
                // Process installation events
                break;
            case "push":
            case "issues":
                // Process code/issue events
                break;
        }

        return Results.Ok(new { received = true, @event = eventType });
    }
}
