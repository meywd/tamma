namespace Tamma.Api.Endpoints;

public static class HealthEndpoints
{
    public static IResult GetHealth()
        => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, version = "2.0.0" });
}
