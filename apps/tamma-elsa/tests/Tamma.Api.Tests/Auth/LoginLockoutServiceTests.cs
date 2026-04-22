using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class LoginLockoutServiceTests
{
    [Test]
    public void RecordFailedAttempt_FiveTimes_LocksAccount()
    {
        var svc = new LoginLockoutService();
        for (int i = 0; i < 4; i++)
            svc.RecordFailedAttempt("a@b.com").Should().BeFalse();
        svc.RecordFailedAttempt("a@b.com").Should().BeTrue();
        svc.IsLocked("a@b.com").Should().BeTrue();
    }

    [Test]
    public void ResetAttempts_ClearsState()
    {
        var svc = new LoginLockoutService();
        for (int i = 0; i < 5; i++) svc.RecordFailedAttempt("a@b.com");
        svc.IsLocked("a@b.com").Should().BeTrue();
        svc.ResetAttempts("a@b.com");
        svc.IsLocked("a@b.com").Should().BeFalse();
    }

    [Test]
    public void IsLocked_ReturnsFalseForUnknownEmail()
    {
        var svc = new LoginLockoutService();
        svc.IsLocked("never-seen@x.com").Should().BeFalse();
    }
}
