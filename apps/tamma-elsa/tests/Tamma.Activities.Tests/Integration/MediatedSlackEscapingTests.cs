using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Tests.Integration;

/// <summary>
/// Security hardening for the MEDIATED Slack path. The 16 domain activities
/// (Mentorship / Review / Blocker / Assessment) build their outbox request through
/// <see cref="MediatedSlack.BuildDirectMessage"/> / <see cref="MediatedSlack.BuildChannelMessage"/>,
/// which fold in bodies derived from issue titles and LLM output. Those bodies MUST be
/// hardened against Slack broadcast / mention / channel control tokens exactly once at
/// this construction seam — before the request is enqueued to <c>slack_outbox</c> — so a
/// body containing <c>&lt;!channel&gt;</c>, <c>&lt;!here&gt;</c>, <c>&lt;@Uxxxx&gt;</c> or
/// <c>&lt;!subteam^Sxxx&gt;</c> can never ping the whole workspace. Ordinary text and URLs
/// must be left intact.
/// </summary>
[TestFixture]
public class MediatedSlackEscapingTests
{
    [Test]
    public void BuildDirectMessage_NeutralizesBroadcastToken()
    {
        var req = MediatedSlack.BuildDirectMessage("U9", "<!channel> deploy done", "Info", "SendDirect");

        req.Message.Should().NotContain("<!channel>", "the @channel broadcast must not reach Slack live");
        req.Message.Should().Be("&lt;!channel&gt; deploy done");
    }

    [Test]
    public void BuildDirectMessage_NeutralizesMentionAndSubteam()
    {
        var req = MediatedSlack.BuildDirectMessage("U9", "<@U123> <!subteam^S1>", "Info", "SendDirect");

        req.Message.Should().NotContain("<@U123>");
        req.Message.Should().NotContain("<!subteam^S1>");
        req.Message.Should().Be("&lt;@U123&gt; &lt;!subteam^S1&gt;");
    }

    [Test]
    public void BuildChannelMessage_NeutralizesHereAndEveryone()
    {
        var req = MediatedSlack.BuildChannelMessage("eng", "<!here> and <!everyone>", "Warning", "SendChannel");

        req.Message.Should().NotContain("<!here>");
        req.Message.Should().NotContain("<!everyone>");
        req.Message.Should().Be("&lt;!here&gt; and &lt;!everyone&gt;");
    }

    [Test]
    public void BuildChannelMessage_PreservesPlainTextAndUrls()
    {
        var req = MediatedSlack.BuildChannelMessage(
            "eng", "Review PR at https://tamma.dev/pr/1 please", "Info", "SendChannel");

        req.Message.Should().Be("Review PR at https://tamma.dev/pr/1 please",
            "ordinary text and URLs must not be mangled by the control-token escape");
    }
}
