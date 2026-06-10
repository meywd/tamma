using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.GitLab;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabErrorMapperTests
{
    [Test]
    public void Maps_401_to_AuthExpired()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.Unauthorized, null, null);
        err.Should().BeOfType<PlatformError.AuthExpired>();
    }

    [Test]
    public void Maps_403_to_PermissionDenied()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.Forbidden, null, null);
        err.Should().BeOfType<PlatformError.PermissionDenied>();
    }

    [Test]
    public void Maps_404_to_NotFound()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.NotFound, null, null);
        err.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public void Maps_429_to_RateLimited_with_retryAfter()
    {
        var err = GitLabErrorMapper.Map((HttpStatusCode)429, null, TimeSpan.FromSeconds(60));
        err.Should().BeOfType<PlatformError.RateLimited>()
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Test]
    public void Maps_500_to_ServiceUnavailable()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.InternalServerError, null, null);
        err.Should().BeOfType<PlatformError.ServiceUnavailable>();
    }

    [Test]
    public void Maps_503_to_ServiceUnavailable()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.ServiceUnavailable, null, null);
        err.Should().BeOfType<PlatformError.ServiceUnavailable>();
    }

    [Test]
    public void Maps_400_with_message_string_to_InvalidRequest()
    {
        var err = GitLabErrorMapper.Map(
            HttpStatusCode.BadRequest, "{\"message\":\"branch already exists\"}", null);
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        var ir = (PlatformError.InvalidRequest)err;
        ir.Code.Should().Be("bad_request");
        ir.Hint.Should().Be("branch already exists");
    }

    [Test]
    public void Maps_422_with_message_object_to_validation_failed()
    {
        var err = GitLabErrorMapper.Map(
            HttpStatusCode.UnprocessableEntity,
            "{\"message\":{\"branch\":[\"already exists\"],\"title\":[\"can't be blank\"]}}",
            null);
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        var ir = (PlatformError.InvalidRequest)err;
        ir.Code.Should().Be("validation_failed");
        ir.Hint.Should().Contain("branch: already exists");
        ir.Hint.Should().Contain("title: can't be blank");
    }

    [Test]
    public void Maps_400_with_oauth_error_shape_to_InvalidRequest()
    {
        var err = GitLabErrorMapper.Map(
            HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"refresh expired\"}",
            null);
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        var ir = (PlatformError.InvalidRequest)err;
        ir.Code.Should().Be("invalid_grant");
        ir.Hint.Should().Be("refresh expired");
    }

    [Test]
    public void Maps_409_to_InvalidRequest_with_conflict_code()
    {
        var err = GitLabErrorMapper.Map(HttpStatusCode.Conflict, null, null);
        err.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("conflict");
    }

    [Test]
    public void Maps_unknown_status_to_Unknown()
    {
        var err = GitLabErrorMapper.Map((HttpStatusCode)418, "I'm a teapot", null);
        err.Should().BeOfType<PlatformError.Unknown>();
    }

    [Test]
    public void Maps_400_with_unparseable_body_to_InvalidRequest()
    {
        var err = GitLabErrorMapper.Map(
            HttpStatusCode.BadRequest, "not-json-at-all", null);
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        var ir = (PlatformError.InvalidRequest)err;
        ir.Code.Should().Be("bad_request");
        ir.Hint.Should().Contain("not-json-at-all");
    }
}
