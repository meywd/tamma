using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.GitLab;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabAuthTests
{
    [Test]
    public void FromPlaintext_OAuth_prefix_returns_OAuth2()
    {
        var auth = GitLabAuth.FromPlaintext("oauth:abc-token");
        auth.Should().BeOfType<GitLabAuth.OAuth2>();
        ((GitLabAuth.OAuth2)auth).AccessToken.Should().Be("abc-token");
    }

    [Test]
    public void FromPlaintext_glptt_prefix_returns_ProjectAccessToken()
    {
        var auth = GitLabAuth.FromPlaintext("glptt-abc123");
        auth.Should().BeOfType<GitLabAuth.ProjectAccessToken>();
    }

    [Test]
    public void FromPlaintext_glgtt_prefix_returns_GroupAccessToken()
    {
        var auth = GitLabAuth.FromPlaintext("glgtt-grp456");
        auth.Should().BeOfType<GitLabAuth.GroupAccessToken>();
    }

    [Test]
    public void FromPlaintext_glpat_prefix_returns_PersonalAccessToken()
    {
        var auth = GitLabAuth.FromPlaintext("glpat-personal");
        auth.Should().BeOfType<GitLabAuth.PersonalAccessToken>();
    }

    [Test]
    public void FromPlaintext_unprefixed_defaults_to_PersonalAccessToken()
    {
        var auth = GitLabAuth.FromPlaintext("self-hosted-token");
        auth.Should().BeOfType<GitLabAuth.PersonalAccessToken>();
    }

    [Test]
    public void FromPlaintext_empty_throws()
    {
        Action act = () => GitLabAuth.FromPlaintext("");
        act.Should().Throw<ArgumentException>();
    }
}
