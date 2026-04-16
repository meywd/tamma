using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Sanitization;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Sanitization;

/// <summary>
/// Guards against ReDoS. The sanitization engine must compile regex with
/// <c>RegexOptions.Compiled</c> and a 100 ms <c>MatchTimeout</c> so that a
/// pathological combination of pattern + input cannot hang the request.
/// </summary>
[TestFixture]
public class SanitizationRegexTimeoutTests
{
    [Test]
    public async Task SanitizeAsync_PathologicalPattern_HitsTimeoutAndSkipsRuleWithinOneSecond()
    {
        // Classic catastrophic-backtracking recipe: (a+)+$ against long run of 'a' + 'b'.
        var repo = new Mock<ISanitizationRepository>();
        repo.Setup(r => r.GetRulesAsync(It.IsAny<Guid?>()))
            .ReturnsAsync(new List<SanitizationRuleDefinition>
            {
                new("redos", "(a+)+$", "[X]", true, 1, true),
                new("email", @"\b[\w.-]+@[\w.-]+\.\w+\b", "[REDACTED]", false, 100, true),
            });

        var svc = new SanitizationService(repo.Object, NullLogger<SanitizationService>.Instance);

        // 28 a's + b triggers catastrophic backtracking; anchored $ forces full scan.
        var input = new string('a', 28) + "b contact alice@example.com";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.SanitizeAsync(input, null);
        sw.Stop();

        // The ReDoS rule must NOT stall the whole pipeline. Even with a 100 ms
        // timeout plus some overhead, the full call must complete well under
        // one second.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

        // The email rule (priority 100) must still have a chance to run after
        // the pathological rule is skipped.
        result.Hits.Should().Contain(h => h.RuleName == "email");
        result.SanitizedText.Should().NotContain("alice@example.com");
    }
}
