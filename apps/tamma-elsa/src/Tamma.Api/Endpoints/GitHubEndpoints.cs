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

    public static async Task<IResult> Webhooks(
        HttpContext context,
        IConfiguration config,
        IInstallationRepository installRepo)
    {
        var signature = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (string.IsNullOrEmpty(signature))
            return Results.Unauthorized();

        string body;
        using (var reader = new StreamReader(context.Request.Body))
            body = await reader.ReadToEndAsync();

        var secret = config["GitHub:WebhookSecret"];
        if (!string.IsNullOrEmpty(secret) && !VerifySignature(secret, body, signature))
            return Results.Unauthorized();

        var eventType = context.Request.Headers["X-GitHub-Event"].FirstOrDefault();
        if (string.IsNullOrEmpty(eventType))
            return Results.BadRequest(new { error = "Missing X-GitHub-Event header" });

        switch (eventType)
        {
            case "installation":
            case "installation_repositories":
                break;
            case "push":
            case "issues":
                break;
        }

        return Results.Ok(new { received = true, @event = eventType });
    }

    private static bool VerifySignature(string secret, string body, string signatureHeader)
    {
        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;
        var expected = signatureHeader["sha256=".Length..];
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computed),
            System.Text.Encoding.UTF8.GetBytes(expected.ToLowerInvariant()));
    }
}
