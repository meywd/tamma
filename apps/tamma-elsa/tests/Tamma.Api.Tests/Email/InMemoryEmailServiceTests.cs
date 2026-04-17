using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Email;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Unit tests for the in-memory test-harness email implementation. The
/// integration tests depend on its SentMessages capture; if these tests
/// regress the integration suite will too.
/// </summary>
[TestFixture]
public class InMemoryEmailServiceTests
{
    [Test]
    public async Task SendAsync_CapturesMessage()
    {
        var svc = new InMemoryEmailService();
        var msg = new EmailMessage(
            To: "x@example.com",
            Subject: "hi",
            Html: "<p>hi</p>",
            Text: "hi");

        await svc.SendAsync(msg);

        svc.SentMessages.Should().ContainSingle();
        svc.SentMessages[0].To.Should().Be("x@example.com");
        svc.SentMessages[0].Subject.Should().Be("hi");
    }

    [Test]
    public async Task SendAsync_IsThreadSafe()
    {
        var svc = new InMemoryEmailService();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => svc.SendAsync(new EmailMessage(
                To: $"u{i}@example.com",
                Subject: "s",
                Html: "<p>h</p>",
                Text: "h")));
        await Task.WhenAll(tasks);

        svc.SentMessages.Should().HaveCount(100);
    }

    [Test]
    public void SentMessages_StartsEmpty()
    {
        var svc = new InMemoryEmailService();
        svc.SentMessages.Should().BeEmpty();
    }
}
